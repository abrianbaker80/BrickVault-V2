<script setup lang="ts">
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, ContractFailure, explain } from './api'
import type { Catalog, CountPosition, CountSnapshot, Location, TransferControls } from './api'
import { countPositionLink, countReceiptLink, holdPositionLink } from './countLinks'
import { itemLabel } from './receiving'
import { time } from './presentation'
import AdjustmentReview from './AdjustmentReview.vue'

const props = defineProps<{ location: Location; positionId?: string; receiptId?: string; asOf: string;
  connected: boolean; controls: TransferControls; catalog: Catalog[] }>()
const emit = defineEmits<{ failure: [reason: unknown]; fenced: [id: string]; close: []; recover: [] }>()
const dialog = ref<HTMLDialogElement>()
const historyHeading = ref<HTMLElement>()
const snapshot = shallowRef<CountSnapshot>()
const baseline = shallowRef<{ position: CountPosition; code: string }>()
const confirmed = shallowRef<string[]>([''])
const requested = shallowRef<string[]>([''])
const attempt = ref(0)
const loading = ref(false)
const error = ref('')
const partial = ref(false)
const typedCode = ref('')
const counted = ref<number | ''>('')
const reason = ref('')
const warning = ref('')
const attempted = ref(false)
const sending = ref(false)
const position = computed(() => snapshot.value?.positions[0])
const item = computed(() => props.catalog.find(row => row.id === position.value?.catalogId))
const stale = computed(() => !!baseline.value && (!position.value || baseline.value.position.purchaseRevision !== position.value.purchaseRevision ||
  baseline.value.code !== props.location.code || snapshot.value?.location.code !== props.location.code))
const writable = computed(() => !!position.value && !props.receiptId && !stale.value && !loading.value && !error.value &&
  !partial.value && props.connected && props.controls.enabled && !sending.value)
const receipt = computed(() => props.receiptId ? snapshot.value?.observations[0] : undefined)
watch([typedCode, counted, reason], () => { warning.value = ''; attempted.value = false })
onMounted(() => {
  const parent = dialog.value?.parentElement?.closest('dialog')
  if (parent && !parent.open) parent.showModal()
  dialog.value?.showModal()
})
watch(() => [props.asOf, props.location.revision, props.connected, requested.value, attempt.value] as const,
  async ([, revision, online, cursors], _, onCleanup) => {
    let active = true
    onCleanup(() => { active = false })
    error.value = ''
    loading.value = online
    if (!online) return
    try {
      const query = props.receiptId ? { observationId: props.receiptId } :
        { positionId: props.positionId, limit: '10', ...(cursors.at(-1) ? { after: cursors.at(-1) } : {}) }
      const result = await api.binCounts(props.location.id, query)
      if (!active) return
      if (result.location.revision < revision) throw new ContractFailure()
      partial.value = !result.complete
      if (!result.complete && snapshot.value?.complete) return
      const moved = confirmed.value.length !== cursors.length
      snapshot.value = result
      confirmed.value = cursors
      const current = result.positions[0]
      if (current && result.complete && !baseline.value) baseline.value = { position: current, code: result.location.code }
      if (result.shortages.length && result.positionId) emit('fenced', result.positionId)
      if (moved) { await nextTick(); if (active) historyHeading.value?.focus() }
    } catch (failure) {
      if (!active) return
      error.value = failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_count_observation'
        ? 'No count receipt with this ID was found in this bin.' : explain(failure)
      emit('failure', failure)
    } finally { if (active) loading.value = false }
  }, { immediate: true })
async function submit() {
  if (!writable.value || !baseline.value) return
  warning.value = ''
  attempted.value = true
  if (typedCode.value !== baseline.value.code) { warning.value = 'Enter the code from this physical bin label.'; return }
  if (counted.value === '' || !Number.isInteger(counted.value) || counted.value < 0 || counted.value > 1000000) {
    warning.value = 'Enter a whole-number count from 0 to 1,000,000.'; return
  }
  if (!reason.value.trim()) { warning.value = 'Enter the reason for this count.'; return }
  sending.value = true
  try {
    const p = baseline.value.position
    const saved = await props.controls.dispatch({ kind: 'count', workspaceId: props.controls.workspaceId, body: {
      commandId: crypto.randomUUID(), purchaseId: p.purchaseId, positionId: p.positionId, locationId: props.location.id,
      expectedRevision: p.purchaseRevision, confirmedLocationCode: typedCode.value, countedQuantity: counted.value, reason: reason.value.trim()
    } })
    if (saved) window.location.hash = countReceiptLink(props.location.id, saved.entityId)
    else if (props.controls.pending) emit('recover')
  } finally { sending.value = false }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="count-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="count-title">{{ receiptId ? 'Count receipt' : 'Count stock' }}</h2>
      <button class="close" aria-label="Close count details" :disabled="sending" @click="emit('close')">×</button></header>
    <div class="count-body">
      <p><strong>{{ location.code }} · {{ location.name }}</strong></p>
      <p v-if="error || !connected" role="status" class="warning">{{ error || 'The workspace is disconnected.' }}
        {{ snapshot ? 'Showing the last count read.' : 'Count details are unavailable.' }}</p>
      <p v-else-if="loading" role="status">Reading count evidence…</p>
      <p v-if="partial" class="warning" role="status">Partial count read. {{ snapshot?.complete ? 'Showing the last complete count read.' : 'Count history may be incomplete.' }}</p>
      <button v-if="connected && (error || partial)" :disabled="loading" @click="attempt++">Retry count read</button>
      <template v-if="snapshot">
        <small>Count read {{ time(snapshot.asOf) }}</small>
        <p v-if="position"><strong>{{ position.purchaseReference }} · {{ position.deliveryReference }}</strong></p>
        <p v-if="!position && snapshot.complete && !partial && !error && !loading && connected">This position has no current stock in this bin.</p>
        <p v-if="item && position">{{ itemLabel([item], item.id) }}
          <small v-if="item.revision !== position.catalogRevision">Placed at catalog revision {{ position.catalogRevision }}. Item details show current revision {{ item.revision }}.</small></p>
        <section v-for="c in snapshot.shortages" :key="c.id" class="case-note" aria-label="Open shortage">
          <strong>Open shortage</strong><p>{{ c.openingMissingQuantity }} missing at opening · {{ c.latestObservedMissingQuantity }} missing on latest count</p>
          <p>Transfers and new offer backing are blocked for this position. Existing holds keep their priority until the owner selects a hold and quantity to release.</p>
          <a :href="holdPositionLink(location.id, c.positionId)">Review build holds for this shortage</a>
          <a :href="countReceiptLink(location.id, c.openingObservationId)">Opening count receipt</a>
          <AdjustmentReview :key="c.id" :bin="location.id" :position="c.positionId" :case-id="c.id"
            :as-of="snapshot.asOf" :connected="connected" @failure="emit('failure', $event)" />
        </section>
        <template v-if="receipt">
          <h3>Observed quantities</h3>
          <dl class="details"><dt>Witnessed count</dt><dd>{{ receipt.countedQuantity }}</dd><dt>Ledger at count</dt><dd>{{ receipt.ledgerQuantity }}</dd>
            <dt>Backed at count</dt><dd>{{ receipt.offerBackedQuantity }}</dd><dt>Missing units</dt><dd>{{ receipt.missingQuantity }}</dd>
            <dt>Unidentified surplus</dt><dd>{{ receipt.unidentifiedSurplusQuantity }}</dd></dl>
          <p v-if="receipt.unidentifiedSurplusQuantity">Surplus units await identification before they can enter stock.</p>
          <p><strong>{{ receipt.reason }}</strong><br>{{ time(receipt.observedAt) }} · Witnessed in {{ receipt.locationCode }}</p>
          <p class="field-hint">Observation only. No stock adjustment posted.</p>
          <details><summary>Receipt identifiers</summary><small>Observation {{ receipt.id }}</small><small>Position {{ receipt.positionId }}</small>
            <small>Command {{ receipt.commandId }}</small><small>Correlation ID {{ receipt.correlationId }}</small></details>
          <a :href="countPositionLink(location.id, receipt.positionId)">See counts for this position</a>
        </template>
        <template v-else-if="positionId">
          <form v-if="position && baseline" class="form-body" @submit.prevent="submit">
            <p>{{ baseline.position.ledgerQuantity }} in the ledger · {{ baseline.position.offerBackedQuantity }} backed by offers</p>
            <p v-if="stale" class="warning" role="status">The position changed. Close and reopen this count to review current stock before recording a new observation.</p>
            <p v-else-if="!controls.enabled" role="status">{{ sending ? 'Recording observation…' : 'Waiting for a complete, connected register read.' }}</p>
            <fieldset class="fields" :disabled="!writable">
              <label>Confirm physical bin code<input v-model="typedCode" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
              <label>Witnessed quantity<input v-model="counted" required type="number" inputmode="numeric" min="0" max="1000000" step="1" /></label>
              <label class="span">Reason for count<input v-model="reason" required maxlength="200" /></label></fieldset>
            <p class="field-hint">Recording a count preserves the observation and opens a case for missing units. Ledger quantities stay in place.</p>
            <p v-if="warning || (attempted && controls.feedback)" class="warning" role="alert">{{ warning || controls.feedback }}</p>
            <div class="actions"><button class="primary" :disabled="!writable">Record observation</button></div>
          </form>
          <h3 ref="historyHeading" tabindex="-1">Count history</h3>
          <ul v-if="snapshot.observations.length" class="receipt-lines" aria-label="Count observations">
            <li v-for="o in snapshot.observations" :key="o.id">
              <strong>{{ o.countedQuantity }} observed · {{ o.ledgerQuantity }} in ledger</strong>
              <p>{{ o.reason }}</p><small>{{ time(o.observedAt) }}</small>
              <a :href="countReceiptLink(location.id, o.id)">View count receipt</a></li>
          </ul>
          <p v-else-if="snapshot.complete && !partial && !error && connected && !loading">No observations recorded for this position.</p>
          <div v-if="snapshot.nextAfter || confirmed.length > 1" class="actions">
            <span>Page {{ confirmed.length }}</span>
            <button :disabled="loading || !connected || partial || confirmed.length === 1" @click="requested = confirmed.slice(0,-1)">Newer counts</button>
            <button :disabled="loading || !connected || partial || !snapshot.nextAfter" @click="snapshot.nextAfter && (requested = [...confirmed, snapshot.nextAfter])">Older counts</button>
          </div>
        </template>
      </template>
    </div>
  </dialog>
</template>
<style scoped>
.count-body { display: grid; gap: 12px; padding: 16px; overflow-wrap: anywhere }
.count-body p, .count-body h3 { margin: 0 }
.count-body small { display: block; color: var(--muted) }
.count-body .form-body { padding: 12px 0 0 }
.count-body .details, .count-body .receipt-lines { padding: 0 }
.count-body .warning, .case-note { margin: 0; padding: 10px }
.case-note { display: grid; gap: 6px; border-left: 3px solid #ba8f28; background: #fff6dc }
.count-body > button, .count-body > a { justify-self: start }
.count-body a { color: #355340; text-underline-offset: 2px }
summary { cursor: pointer }
</style>
