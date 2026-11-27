<script setup lang="ts">
import { useBinRegistry } from "./useBinRegistry"
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import { countReceiptLink } from './countLinks'
import type { CountObservation, Location, TransferControls } from './api'
import { label, time } from './presentation'
import { itemLabel } from './receiving'
import HoldReleases from './HoldReleases.vue'

const props = defineProps<{ location: Location; positionId: string; asOf: string; connected: boolean; controls: TransferControls }>()
const emit = defineEmits<{ close: []; failure: [reason: unknown]; recover: [] }>()
type Snapshot = Awaited<ReturnType<typeof api.positionHolds>>
type Hold = Snapshot['holds'][number]
const dialog = ref<HTMLDialogElement>()
const formHeading = ref<HTMLElement>()
const createButton = ref<HTMLButtonElement>()
const confirmation = ref<HTMLElement>()
const snapshot = shallowRef<Snapshot>()
const evidence = shallowRef<CountObservation>()
const partial = ref(false)
const reading = ref(false)
const readError = ref('')
const requested = shallowRef<string[]>([''])
const displayed = shallowRef<string[]>([''])
const change = ref(0)
const sending = ref(false)
const result = ref('')
const historyId = ref('')
const draft = shallowRef<{ revision: number; catalogRevision: number; workspace: string; locationCode: string; selected?: Hold; caseId: string | null; observation: string | null }>()
const amount = ref<number | ''>('')
const binCode = ref('')
const bins=useBinRegistry(()=>[binCode.value])
const purpose = ref('')
const reason = ref('')
const message = ref('')
const stale = computed(() => draft.value && snapshot.value && (draft.value.revision !== snapshot.value.purchaseRevision ||
  draft.value.catalogRevision !== snapshot.value.currentCatalog.revision || draft.value.workspace !== props.controls.workspaceId || draft.value.locationCode !== props.location.code || draft.value.caseId !== snapshot.value.shortageId ||
  draft.value.observation !== snapshot.value.latestObservationId || (draft.value.selected &&
  snapshot.value.holds.find(h => h.id === draft.value?.selected?.id)?.remainingQuantity !== draft.value.selected.remainingQuantity)))
const ready = computed(() => props.connected && props.controls.enabled && !reading.value && !readError.value &&
  snapshot.value?.complete && !partial.value && !sending.value)
const maximum = computed(() => draft.value?.selected?.remainingQuantity ?? snapshot.value?.availableToHoldQuantity ?? 0)
const canSave = computed(() => ready.value && !stale.value && draft.value && Number.isInteger(amount.value) &&
  Number(amount.value) > 0 && Number(amount.value) <= maximum.value && reason.value.trim().length > 0 &&
  (draft.value.selected || (purpose.value.trim().length > 0 && bins.matches(props.location,binCode.value))))

watch(() => [props.positionId, props.location.id, props.connected, props.asOf, requested.value, change.value, props.location.revision] as const,
  async ([position, bin, connected, , cursors], _, clean) => {
    let active = true
    clean(() => { active = false })
    reading.value = false
    if (!connected) return
    reading.value = true
    readError.value = ''
    try {
      const next = await api.positionHolds(bin, position, cursors.at(-1))
      if (!active) return
      if (next.quarantinedQuantity === undefined || next.location.revision < props.location.revision || Boolean(next.shortageId) !== Boolean(next.latestObservationId))
        throw new ContractFailure()
      partial.value = !next.complete
      if (partial.value && snapshot.value?.complete) return
      let observation: CountObservation | undefined
      if (next.latestObservationId && next.complete) {
        const counts = await api.binCounts(bin, { positionId: position, observationId: next.latestObservationId })
        if (!active) return
        if (!counts.complete) { partial.value = true; return }
        const current = counts.shortages.find(c => c.id === next.shortageId)
        if (current?.latestObservationId !== next.latestObservationId) {
          readError.value = 'The shortage changed while its evidence was being read. Retry to review the latest count.'
          return
        }
        if (counts.observations[0]?.purchaseId !== next.purchaseId) throw new ContractFailure()
        observation = counts.observations[0]
      }
      snapshot.value = next
      evidence.value = observation
      displayed.value = cursors
    } catch (failure) {
      if (active) { readError.value = explain(failure); emit('failure', failure) }
    } finally { if (active) reading.value = false }
  }, { immediate: true })

onMounted(() => dialog.value?.showModal())
async function edit(selected?: Hold) {
  const data = snapshot.value
  if (!ready.value || !data) return
  draft.value = { selected, locationCode: data.location.code, revision: data.purchaseRevision, catalogRevision: data.currentCatalog.revision, workspace: props.controls.workspaceId,
    caseId: data.shortageId, observation: data.latestObservationId }
  amount.value = ''; binCode.value = ''; purpose.value = ''; reason.value = ''; result.value = ''; message.value = ''
  await nextTick()
  formHeading.value?.focus()
}
async function cancel() {
  const id = draft.value?.selected?.id
  draft.value = undefined
  await nextTick()
  const releaseButton = id ? dialog.value?.querySelector<HTMLButtonElement>('button[data-release="' + id + '"]') : null
  const target = releaseButton && !releaseButton.disabled ? releaseButton : createButton.value
  if (target && !target.disabled) target.focus()
  else dialog.value?.querySelector<HTMLButtonElement>('button.close')?.focus()
}
function page(after?: string | null) {
  if (!ready.value || draft.value) return
  requested.value = after ? [...displayed.value, after] : displayed.value.slice(0, -1)
}
async function save() {
  if (!canSave.value || !draft.value || !snapshot.value) return
  const { selected, revision, workspace, caseId, observation } = draft.value
  const body = { commandId: crypto.randomUUID(), expectedRevision: revision, purchaseId: snapshot.value.purchaseId,
    quantity: Number(amount.value), reason: reason.value.trim() }
  sending.value = true
  message.value = ''
  try {
    const receipt = selected
      ? await props.controls.dispatch({ kind: 'hold-release', workspaceId: workspace, locationId: props.location.id, positionId: props.positionId,
          body: { ...body, holdId: selected.id, ...(caseId && observation ? { shortageId: caseId, expectedObservationId: observation } : {}) } })
      : await props.controls.dispatch({ kind: 'hold', workspaceId: workspace,
          body: { ...body, positionId: props.positionId, locationId: props.location.id, confirmedLocationCode: binCode.value, purpose: purpose.value.trim() } })
    if (receipt) {
      result.value = selected ? 'Released ' + body.quantity + ' from ' + selected.purpose + '. Hold ID ' + selected.id :
        'Reserved ' + body.quantity + ' for ' + purpose.value.trim() + '. Hold ID ' + receipt.entityId
      draft.value = undefined; requested.value = ['']; ++change.value
      await nextTick(); confirmation.value?.focus()
    } else if (props.controls.pending) emit('recover')
    else message.value = props.controls.feedback
  } finally { sending.value = false }
}
</script>

<template>
  <dialog ref="dialog" aria-labelledby="hold-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="hold-title">Build holds</h2>
      <button aria-label="Close build holds" class="close" :disabled="sending" @click="emit('close')">×</button></header>
    <div class="hold-body">
      <p>{{ location.code }} · {{ location.name }}</p>
      <p v-if="readError || !connected" class="warning" role="alert">{{ readError || 'The workspace is offline.' }}
        {{ snapshot ? 'Showing the last confirmed read.' : 'Build hold details are unavailable.' }}</p>
      <p v-else-if="reading" role="status">Reading build holds…</p>
      <p v-else-if="partial" class="warning" role="status">Partial hold read. {{ snapshot?.complete ? 'Showing the last confirmed read.' : 'Hold or count evidence is incomplete.' }}</p>
      <button v-if="(readError || partial) && connected" :disabled="reading" @click="++change">Retry hold read</button>
      <p v-if="result" ref="confirmation" role="status" tabindex="-1" class="notice">{{ result }}</p>
      <template v-if="snapshot">
        <section aria-label="Selected receipt position" class="position-identity">
          <strong>{{ snapshot.purchaseReference }} · {{ snapshot.deliveryReference }}</strong>
          <p>{{ label(snapshot.currentCatalog.itemType) }} · {{ itemLabel([snapshot.currentCatalog], snapshot.currentCatalog.id, snapshot.catalogRevision) }}</p>
          <small>Position ID {{ snapshot.positionId }}</small>
        </section>
        <small>Position read {{ time(snapshot.asOf) }}</small>
        <dl class="details"><dt>Ledger quantity</dt><dd>{{ snapshot.ledgerQuantity }}</dd>
          <dt>Backed by offers</dt><dd>{{ snapshot.offerBackedQuantity }}</dd>
          <dt>Reserved for builds</dt><dd>{{ snapshot.reservedQuantity }}</dd>
          <template v-if="snapshot.quarantinedQuantity"><dt>Quarantined in bin</dt><dd>{{ snapshot.quarantinedQuantity }}</dd></template>
          <dt>Available for a new hold</dt><dd>{{ snapshot.availableToHoldQuantity }}</dd></dl>
        <section v-if="snapshot.shortageId" class="warning" aria-label="Shortage evidence">
          <strong>This position has an open shortage.</strong>
          <template v-if="evidence">
            <p>{{ evidence.countedQuantity }} observed · {{ evidence.ledgerQuantity }} in the ledger at count · {{ evidence.missingQuantity }} missing</p>
            <p>{{ evidence.reason }} · {{ time(evidence.observedAt) }}</p>
            <a :href="countReceiptLink(location.id, evidence.id)">View latest witnessed count</a>
          </template>
          <p>Choose a project and quantity to release. The shortage stays open for review.</p>
        </section>
        <template v-if="!draft">
          <button ref="createButton" class="primary" :disabled="!ready || !snapshot.availableToHoldQuantity" @click="edit()">Create build hold</button>
          <ul v-if="snapshot.holds.length" class="receipt-lines" aria-label="Project holds">
            <li v-for="hold in snapshot.holds" :key="hold.id">
              <strong>{{ hold.purpose }}</strong><small>Hold ID {{ hold.id }}</small><p>{{ hold.remainingQuantity }} held · {{ hold.releasedQuantity }} released of {{ hold.quantity }}</p>
              <small>{{ hold.reason }} · {{ time(hold.createdAt) }}</small>
              <button v-if="hold.releasedQuantity" :aria-label="'Release history for ' + hold.purpose + ' · Hold ID ' + hold.id" :aria-expanded="historyId === hold.id"
                @click="historyId = historyId === hold.id ? '' : hold.id">Release history</button>
              <HoldReleases v-if="historyId === hold.id" :key="hold.id" :bin="location.id" :position="positionId" :hold="hold.id"
                :connected="connected" :as-of="snapshot.asOf" @failure="emit('failure', $event)" />
              <button v-if="hold.remainingQuantity" :data-release="hold.id" :aria-label="'Release pieces from ' + hold.purpose + ' · Hold ID ' + hold.id" :disabled="!ready" @click="edit(hold)">Release pieces</button>
            </li>
          </ul>
          <p v-else-if="ready">No build holds recorded for this position.</p>
          <nav v-if="displayed.length > 1 || snapshot.nextAfter" aria-label="Hold pages">
            <button :disabled="!ready || displayed.length < 2" @click="page()">Newer holds</button>
            <button :disabled="!ready || !snapshot.nextAfter" @click="page(snapshot.nextAfter)">Older holds</button></nav>
        </template>
        <form v-else @submit.prevent="save">
          <h3 ref="formHeading" tabindex="-1">{{ draft.selected ? 'Release from ' + draft.selected.purpose : 'Create build hold' }}</h3>
          <p v-if="stale" role="alert" class="warning">Stock or count evidence changed. Cancel and review the current quantities before making a decision.</p>
          <fieldset :disabled="!ready || Boolean(stale)">
            <label v-if="!draft.selected">Build or project<input v-model="purpose" required maxlength="120" /></label>
            <label v-if="!draft.selected">Confirm physical bin code<input v-model="binCode" required maxlength="48" autocomplete="off" /></label>
            <label>{{ draft.selected ? 'Quantity to release' : 'Quantity to reserve' }}<input v-model="amount" type="number" min="1" :max="maximum" required /></label>
            <label>Reason<input v-model="reason" maxlength="200" required /></label>
          </fieldset>
          <p v-if="draft.selected" class="release-target"><strong>{{ draft.selected.purpose }}</strong>
            <small>Hold ID {{ draft.selected.id }}</small></p>
          <p v-if="message" role="alert" class="warning">{{ message }}</p>
          <div class="actions"><button type="button" :disabled="sending" @click="cancel">Cancel</button>
            <button class="primary" :disabled="!canSave" :aria-label="draft.selected ? 'Confirm release from ' + draft.selected.purpose + ' · Hold ID ' + draft.selected.id : undefined">{{ draft.selected ? 'Confirm release' : 'Confirm hold' }}</button></div>
        </form>
      </template>
    </div>
  </dialog>
</template>

<style scoped>
.hold-body { display: grid; gap: 12px; padding: 16px; overflow-wrap: anywhere }
.hold-body p, .hold-body h3, .hold-body dl, .hold-body ul { margin: 0 }
.hold-body > button { justify-self: start }
.position-identity, .release-target { display: grid; gap: 3px }
.position-identity small, .release-target small, .receipt-lines small { overflow-wrap: anywhere }
.hold-body form, .hold-body fieldset { display: grid; gap: 10px }
.hold-body fieldset { border: 0; padding: 0; min-width: 0 }
.hold-body label { display: grid; gap: 4px }
.hold-body nav { display: flex; gap: 8px }
.hold-body .receipt-lines { padding: 0 }
</style>
