<script setup lang="ts">
import { useBinRegistry } from "./useBinRegistry"
import { computed, nextTick, onMounted, ref, shallowRef } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { Location, QuarantineEvidence, QuarantineSnapshot, TransferControls } from './api'
import { partOutLink, draftBackingLink, quarantineLink, recoveryLink } from './countLinks'
import { itemLabel } from './receiving'
import { positionContents } from './setContents'
import { time } from './presentation'
import { usePositionPage } from './usePositionPage'

const props = defineProps<{ location: Location; positionId: string; receiptId?: string; connected: boolean; asOf: string; controls: TransferControls }>()
const emit = defineEmits<{ close: []; failure: [reason: unknown]; recover: [] }>()
const dialog = ref<HTMLDialogElement>()
const create = ref<HTMLButtonElement>()
const formTitle = ref<HTMLElement>()
const readState = { get asOf() { return props.receiptId ? String(props.location.revision) : props.asOf }, get state() { return props.connected ? 'ready' as const : 'unavailable' as const } }
type Page = QuarantineSnapshot | (QuarantineEvidence & { nextAfter: null })
const { page, confirmed, requested, attempt, loading, partial, error, heading, retry } = usePositionPage<Page>(readState, async after => {
  if (props.receiptId) return { ...await api.quarantineReceipt(props.location.id, props.positionId, { receiptId: props.receiptId }), nextAfter: null }
  const result = await api.quarantine(props.location.id, props.positionId, after)
  if (result.position.location.revision < props.location.revision) throw new ContractFailure()
  return result
}, reason => emit('failure', reason))
const stock = computed(() => page.value?.scope === 'position-quarantine' && page.value.complete ? page.value : undefined)
const receipt = computed(() => page.value?.scope === 'quarantine-receipt' && page.value.complete ? page.value : undefined)
const layer = computed(() => stock.value?.position.layer)
const draft = shallowRef<{ stock: QuarantineSnapshot; record?: QuarantineSnapshot['quarantines'][number]; originalReceipt?: QuarantineEvidence; workspaceId: string }>()
const bin = ref('')
const bins=useBinRegistry(()=>[bin.value])
const quantity = ref<number | ''>('')
const reason = ref('')
const identity = ref(false)
const passed = ref(false)
const saving = ref(false)
const opening = ref(false)
const originalError = ref('')
const message = ref('')
const ready = computed(() => Boolean(props.connected && props.controls.enabled && !loading.value && !error.value && !partial.value && stock.value && !saving.value && !opening.value))
const stale = computed(() => {
  const old = draft.value, now = stock.value
  if (!old || !now) return false
  return old.workspaceId !== props.controls.workspaceId || old.stock.purchaseRevision !== now.purchaseRevision ||
    old.stock.position.location.revision !== now.position.location.revision ||
    old.stock.position.layer.currentCatalog.revision !== now.position.layer.currentCatalog.revision ||
    old.stock.hasOpenShortage !== now.hasOpenShortage || old.stock.availableToQuarantineQuantity !== now.availableToQuarantineQuantity ||
    Boolean(old.record && !now.quarantines.some(q => q.id === old.record?.id && q.remainingQuantity === old.record.remainingQuantity))
})
const maximum = computed(() => draft.value?.record?.remainingQuantity ?? stock.value?.availableToQuarantineQuantity ?? 0)
const canSave = computed(() => ready.value && !stale.value && !stock.value?.hasOpenShortage && identity.value &&
  bins.matches(stock.value?.position.location,bin.value) && Number.isInteger(quantity.value) && Number(quantity.value) > 0 &&
  Number(quantity.value) <= maximum.value && Boolean(reason.value.trim()) &&
  (!draft.value?.record || (passed.value && layer.value?.currentCatalog.integrationEligible)))
onMounted(() => dialog.value?.showModal())
async function edit(record?: QuarantineSnapshot['quarantines'][number]) {
  if (!stock.value || !ready.value || stock.value.hasOpenShortage ||
      (record ? !record.remainingQuantity || !layer.value?.currentCatalog.integrationEligible : !stock.value.availableToQuarantineQuantity)) return
  const reviewed = stock.value, workspaceId = props.controls.workspaceId, issuingRoute = window.location.hash
  let originalReceipt: QuarantineEvidence | undefined
  originalError.value = ''
  if (record) {
    opening.value = true
    try {
      originalReceipt = await api.quarantineReceipt(props.location.id,props.positionId,{receiptId:record.id})
      if (!originalReceipt.complete || originalReceipt.kind !== 'quarantine' || originalReceipt.purchaseId !== reviewed.position.layer.purchaseId ||
          originalReceipt.catalogId !== reviewed.position.layer.catalogId || originalReceipt.quantity !== record.quantity ||
          originalReceipt.reason !== record.reason) throw new ContractFailure()
    } catch (failure) {
      originalError.value = 'Could not verify the original quarantine receipt. ' + explain(failure)
      emit('failure',failure)
      return
    } finally { opening.value = false }
  }
  if (!dialog.value?.isConnected || window.location.hash !== issuingRoute || !ready.value) return
  draft.value = { stock:reviewed, record, originalReceipt, workspaceId }
  bin.value = ''; quantity.value = ''; reason.value = ''; identity.value = false; passed.value = false; message.value = ''
  await nextTick()
  formTitle.value?.focus()
}
async function cancel() {
  const id = draft.value?.record?.id
  draft.value = undefined
  await nextTick()
  const button = id ? dialog.value?.querySelector<HTMLButtonElement>('[data-inspect="'+id+'"]') : create.value
  if (button && !button.disabled) button.focus()
  else heading.value?.focus()
}
async function save() {
  const form = draft.value
  if (!form || !canSave.value) return
  const positioned = form.stock.position
  const body = { commandId:crypto.randomUUID(), purchaseId:positioned.layer.purchaseId, expectedRevision:form.stock.purchaseRevision,
    positionId:props.positionId, locationId:props.location.id, confirmedLocationCode:bin.value,
    catalogId:positioned.layer.catalogId, catalogRevision:positioned.layer.currentCatalog.revision, quantity:Number(quantity.value), reason:reason.value.trim() }
  const issuingDialog = dialog.value, hash = window.location.hash
  saving.value = true
  try {
    const record = form.record
    const saved = await props.controls.dispatch(record ?
      { kind:'quarantine-release', workspaceId:form.workspaceId, body:{ ...body, quarantineId:record.id, inspectionResult:'passed' } } :
      { kind:'quarantine', workspaceId:form.workspaceId, body })
    await nextTick()
    if (!issuingDialog?.isConnected || !issuingDialog.open || hash !== window.location.hash) return
    if (saved) window.location.hash = quarantineLink(props.location.id, props.positionId, saved.entityId)
    else if (props.controls.pending) emit('recover')
    else message.value = props.controls.feedback
  } finally { saving.value=false }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="quarantine-title" @cancel.prevent="!saving && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 ref="heading" id="quarantine-title" tabindex="-1">{{ receiptId ? 'Quarantine receipt' : 'Position quarantine' }}</h2>
      <button type="button" class="close" aria-label="Close quarantine details" :disabled="saving" @click="emit('close')">×</button></header>
    <div class="quarantine-body">
      <p v-if="error || partial || !connected" class="warning" role="status">{{ error || (partial ? 'Partial quarantine read.' : 'The workspace is offline.') }}
        {{ page?.complete ? 'Showing the last confirmed read.' : 'Quarantine details are unavailable.' }}</p>
      <p v-else-if="loading" role="status">Reading quarantine records…</p>
      <button v-if="connected && (error || partial)" ref="retry" :disabled="loading" @click="attempt++">Retry quarantine read</button>
      <template v-if="receipt">
        <h3>{{ receipt.kind === 'quarantine' ? 'Placed in quarantine' : 'Released after inspection' }}</h3>
        <p><strong>Confirmed catalog identity</strong><br />{{ itemLabel([receipt.recordedCatalog],receipt.catalogId) }}</p>
        <p><strong>{{ receipt.quantity }} units</strong> · Bin {{ receipt.confirmedLocationCode }}</p>
        <p v-if="receipt.location.code !== receipt.confirmedLocationCode">Current bin code {{ receipt.location.code }}</p>
        <p v-if="receipt.kind === 'quarantine-release'">Inspection passed for the released quantity.</p>
        <p>{{ receipt.reason }}</p>
        <small>Recorded {{ time(receipt.recordedAt) }} · Purchase revision {{ receipt.revision }}</small>
        <dl class="details"><dt>Receipt ID</dt><dd>{{ receipt.id }}</dd><dt>Position ID</dt><dd>{{ receipt.positionId }}</dd>
          <dt>Confirmed catalog ID</dt><dd>{{ receipt.catalogId }} · Revision {{ receipt.catalogRevision }}</dd>
          <dt>Command ID</dt><dd>{{ receipt.commandId }}</dd><dt>Correlation ID</dt><dd>{{ receipt.correlationId }}</dd></dl>
        <a v-if="receipt.partOutId && receipt.partOutSourcePositionId" :href="partOutLink(receipt.partOutSourcePositionId,receipt.partOutId)">Originating part-out receipt</a>
        <a v-if="receipt.recoveryPostingId" :href="recoveryLink(location.id,positionId,receipt.recoveryPostingId,receipt.quarantineId)">Recovery inspection receipt</a>
        <a v-if="receipt.kind === 'quarantine-release'" :href="quarantineLink(location.id,positionId,receipt.quarantineId)">Original quarantine receipt</a>
        <nav aria-label="Quarantine receipt navigation"><a :href="quarantineLink(location.id,positionId)">Current position quarantine</a>
          <a :href="'#inventory/'+positionId">Position timeline</a><a :href="'#inventory/lot/'+receipt.deliveryId">Receipt lot</a></nav>
      </template>
      <template v-else-if="stock && layer">
        <section aria-label="Selected receipt position">
          <strong>{{ layer.purchaseReference }} · {{ layer.deliveryReference }}</strong>
          <p>{{ itemLabel([layer.currentCatalog],layer.catalogId) }} · Bin {{ stock.position.location.code }}</p>
          <p v-if="positionContents(layer)">{{ positionContents(layer) }}</p>
          <small>Position {{ positionId }}</small>
          <p v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="warning">Placed at catalog revision {{ layer.catalogRevision }}. Current identity is revision {{ layer.currentCatalog.revision }}.</p>
        </section>
        <dl class="details" aria-label="Current position quantities">
          <dt>Physical on hand</dt><dd>{{ stock.onHandQuantity }}</dd><dt>Quarantined in bin</dt><dd>{{ stock.quarantinedQuantity }}</dd>
          <dt>Reserved for builds</dt><dd>{{ stock.heldQuantity }}</dd><dt>Saleable stock</dt><dd>{{ stock.sellableQuantity }}</dd>
          <dt>Allocated to draft offers</dt><dd>{{ stock.offerBackedQuantity }}</dd><dt>Available to back a new offer</dt><dd>{{ layer.freeToBackQuantity }}</dd><dt>Available to quarantine</dt><dd>{{ stock.availableToQuarantineQuantity }}</dd>
        </dl>
        <p v-if="layer.catalogRevision !== layer.currentCatalog.revision && !layer.freeToBackQuantity" class="warning">The recorded catalog revision differs from the current identity. This position cannot back a new offer.</p>
        <small>Read {{ time(stock.asOf) }} · Purchase revision {{ stock.purchaseRevision }}</small>
        <p v-if="stock.hasOpenShortage" class="warning">An open shortage blocks quarantine and release for this position.</p>
        <p v-if="!layer.currentCatalog.integrationEligible" class="warning">The current catalog identity needs confirmation before an inspection release.</p>
        <template v-if="!draft">
          <p v-if="opening" role="status">Reading the original quarantine receipt…</p>
          <p v-if="originalError" class="warning" role="alert">{{ originalError }} Select the record to retry.</p>
          <button ref="create" class="primary" :disabled="!ready || stock.hasOpenShortage || !stock.availableToQuarantineQuantity" @click="edit()">Quarantine pieces</button>
          <ul v-if="stock.quarantines.length" class="receipt-lines" aria-label="Quarantine records">
            <li v-for="q in stock.quarantines" :key="q.id"><strong>{{ q.reason }}</strong>
              <p>{{ q.remainingQuantity }} still quarantined · {{ q.releasedQuantity }} released<template v-if="q.lostQuantity"> · {{ q.lostQuantity }} lost</template> of {{ q.quantity }}</p>
              <small>Quarantine {{ q.id }} · {{ time(q.recordedAt) }}</small>
              <a :href="quarantineLink(location.id,positionId,q.id)">Original quarantine receipt</a>
              <a v-if="q.partOutId && q.partOutSourcePositionId" :href="partOutLink(q.partOutSourcePositionId,q.partOutId)">Originating part-out receipt</a>
              <a v-if="q.recoveryPostingId" :href="recoveryLink(location.id,positionId,q.recoveryPostingId,q.id)">Recovery inspection receipt</a>
              <button v-if="q.remainingQuantity" :data-inspect="q.id" :disabled="!ready || stock.hasOpenShortage || !layer.currentCatalog.integrationEligible"
                :aria-label="'Inspect and release from '+q.reason+' · Quarantine '+q.id" @click="edit(q)">Inspect and release</button></li>
          </ul>
          <p v-else-if="!loading && connected && !error && !partial">No quarantine records on this page.</p>
          <nav v-if="confirmed.length>1 || stock.nextAfter" aria-label="Quarantine record pages">
            <button :disabled="loading || !connected || confirmed.length===1" @click="requested=confirmed.slice(0,-1)">Newer records</button>
            <button :disabled="loading || !connected || partial || !!error || !stock.nextAfter" @click="stock.nextAfter && (requested=[...confirmed,stock.nextAfter])">Older records</button></nav>
          <nav aria-label="Position references"><a :href="draftBackingLink(location.id,positionId)">Review draft offer backing</a>
            <a :href="'#inventory/'+positionId">Position timeline</a><a :href="'#inventory/lot/'+layer.deliveryId">Receipt lot</a></nav>
        </template>
        <form v-else @submit.prevent="save">
          <h3 ref="formTitle" tabindex="-1">{{ draft.record ? 'Inspect and release pieces' : 'Quarantine pieces in this bin' }}</h3>
          <p v-if="draft.record"><strong>{{ draft.record.reason }}</strong><small>Quarantine {{ draft.record.id }} · {{ draft.record.remainingQuantity }} available for inspection</small></p>
          <p v-else>Pieces stay in this bin and become unavailable for offers, builds and transfers.</p>
          <section v-if="draft.originalReceipt" aria-label="Inspection identity">
            <p><strong>Recorded at quarantine</strong></p>
            <p>{{ itemLabel([draft.originalReceipt.recordedCatalog],layer.catalogId) }} · Revision {{ draft.originalReceipt.catalogRevision }}</p>
            <p><strong>Current identity to inspect</strong></p><p>{{ itemLabel([layer.currentCatalog],layer.catalogId) }} · Revision {{ layer.currentCatalog.revision }}</p>
          </section>
          <p v-if="stale" role="alert" class="warning">The stock or catalog changed. Cancel and review the position again.</p>
          <fieldset :disabled="!ready || stale">
            <label>Confirm physical bin code<input v-model="bin" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
            <small v-if="bin && !bins.pending(bin) && !bins.matches(stock.position.location,bin)" class="warning">Enter the code printed on the selected bin.</small>
            <label>{{ draft.record ? 'Quantity inspected and approved' : 'Quantity to quarantine' }}<input type="number" v-model="quantity" inputmode="numeric" min="1" :max="maximum" required /></label>
            <small v-if="quantity !== '' && (!Number.isInteger(quantity) || Number(quantity) < 1 || Number(quantity) > maximum)" class="warning">Enter 1–{{ maximum }} whole units.</small>
            <label>{{ draft.record ? 'Inspection evidence and reason' : 'Reason for quarantine' }}<input v-model="reason" required maxlength="200" /></label>
            <label class="check"><input type="checkbox" v-model="identity" /> <span>I confirmed {{ itemLabel([layer.currentCatalog],layer.catalogId) }} at catalog revision {{ layer.currentCatalog.revision }}.</span></label>
            <label class="check" v-if="draft.record"><input type="checkbox" v-model="passed" /> <span>I inspected these pieces and they passed for release.</span></label>
          </fieldset>
          <p v-if="message" role="alert" class="warning">{{ message }}</p>
          <div class="actions"><button type="button" :disabled="saving" @click="cancel">Cancel</button>
            <button class="primary" :disabled="!canSave">{{ draft.record ? 'Confirm inspected release' : 'Confirm quarantine' }}</button></div>
        </form>
      </template>
    </div>
  </dialog>
</template>
<style scoped>
.quarantine-body { display:grid; gap:12px; padding:16px; overflow-wrap:anywhere }
.quarantine-body p, .quarantine-body h3, .quarantine-body dl, .quarantine-body ul { margin:0 }
.quarantine-body small { display:block; color:var(--muted) }
.quarantine-body > button { justify-self:start }
.quarantine-body form, .quarantine-body fieldset, .quarantine-body label { display:grid; gap:6px }
.quarantine-body fieldset { min-width:0; border:0; padding:0; gap:10px }
.quarantine-body .check { display:flex; align-items:flex-start; gap:8px }
.check input { flex:0 0 auto; width:16px; height:16px; margin:2px 0 }
.quarantine-body .receipt-lines { padding:0 }
.quarantine-body li > a, .quarantine-body nav a { display:inline-block; margin-right:10px; padding-block:5px; color:#355340 }
.quarantine-body li button { margin-top:4px }
</style>
