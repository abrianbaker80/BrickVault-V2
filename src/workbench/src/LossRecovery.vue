<script setup lang="ts">
import { useBinRegistry } from "./useBinRegistry"
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, ContractFailure, explain, type Location, type RecoveryEvidence, type RecoveryReview, type TransferControls } from './api'
import { lossReceiptLink, quarantineLink, recoveryLink, type CountLink } from './countLinks'
import { itemLabel, receiptCost } from './receiving'
import { time } from './presentation'

type Route = NonNullable<NonNullable<CountLink>['recovery']>
type Source = RecoveryReview['sources'][number]
type Choices = { source: Source; saleable: string; quarantine: string }
const props = defineProps<{ location: Location; route: Route; asOf: string; connected: boolean; workspaceReady: boolean; controls: TransferControls }>()
const emit = defineEmits<{ close: []; failure: [reason: unknown]; recover: [] }>()
const dialog = ref<HTMLDialogElement>()
const sourceHeading = ref<HTMLElement>(), historyHeading = ref<HTMLElement>()
const review = shallowRef<RecoveryReview>()
const receipt = shallowRef<RecoveryEvidence>()
const first = () => ({ sources: [''], history: [''] })
const requested = shallowRef(first()), confirmed = shallowRef(first())
const loading = ref(false), stale = ref(false), error = ref(''), attempt = ref(0)
const choices = ref<Record<string, Choices>>({})
const bin = ref(''), reason = ref(''), identity = ref(false), sending = ref(false), feedback = ref('')
const bins=useBinRegistry(()=>[bin.value])
const dirty = computed(() => Object.keys(choices.value).length || bin.value || reason.value || identity.value)
const current = computed(() => review.value?.position.layer.currentCatalog)
const healthy = computed(() => props.connected && props.workspaceReady && !loading.value && !error.value && !stale.value)
const editable = computed(() => !!review.value?.remainingQuantity && healthy.value && props.controls.enabled && !sending.value)
const rows = computed(() => Object.values(choices.value))
const total = (key: 'saleable' | 'quarantine') => rows.value.reduce((sum, row) => sum + Number(row[key]), 0)
function validRow(row: Choices) {
  const values = [row.saleable, row.quarantine]
  const quantity = Number(row.saleable) + Number(row.quarantine)
  return values.every(n => n === '' || /^\d+$/.test(n)) && Number.isSafeInteger(quantity) && quantity > 0 && quantity <= row.source.remainingQuantity
}
const allowed = computed(() => editable.value && rows.value.length > 0 && rows.value.every(validRow) && identity.value &&
  bins.matches(review.value?.position.location,bin.value) && !!reason.value.trim())
const sourceKey = (row: Source) => row.quarantineId ?? 'ordinary'
function select(row: Source, key: 'saleable' | 'quarantine', event: Event) {
  const id = sourceKey(row)
  const selected = choices.value[id] ?? { source: row, saleable: '', quarantine: '' }
  selected[key] = (event.target as HTMLInputElement).value
  if ([selected.saleable, selected.quarantine].every(value => !value || /^0+$/.test(value))) delete choices.value[id]
  else choices.value[id] = selected
}
function clear() {
  choices.value = {}; bin.value = ''; reason.value = ''; identity.value = false; feedback.value = ''
  requested.value = first(); review.value = undefined; stale.value = false; attempt.value++
}
function pageBy(key: 'sources' | 'history', cursor?: string | null) {
  const stack = confirmed.value[key]
  requested.value = { ...confirmed.value, [key]: cursor ? [...stack, cursor] : stack.slice(0, -1) }
}
onMounted(() => dialog.value?.showModal())
watch([() => props.route.receipt ? undefined : props.asOf, () => props.connected, () => !!props.route.receipt || props.workspaceReady,
  () => props.location.revision, () => requested.value, attempt],
  async ([, connected, available, , target], _, cleanup) => {
    let active = true
    cleanup(() => { active = false })
    loading.value = connected && available
    error.value = ''
    if (!loading.value) return
    const { position, posting, receipt: id } = props.route
    try {
      const cursor = target.sources.at(-1)!, historyCursor = target.history.at(-1)!
      if (id) {
        const page = await api.recoveryReceipt(props.location.id, position, posting, { recoveryId: id }, cursor)
        if (!active) return
        if (!page.complete) { error.value = 'Incomplete recovery receipt.'; return }
        if (page.nextAfter && target.sources.includes(page.nextAfter)) throw new ContractFailure()
        receipt.value = page
      } else {
        const page = await api.recoveryReview(props.location.id, position, posting, cursor, historyCursor)
        if (!active) return
        if (!page.complete) { error.value = 'Incomplete recovery review.'; return }
        if ((page.nextAfter && target.sources.includes(page.nextAfter)) || (page.nextHistoryAfter && target.history.includes(page.nextHistoryAfter))) throw new ContractFailure()
        const prior = review.value
        if (page.position.location.revision !== props.location.revision || page.position.location.code !== props.location.code ||
            (prior && (dirty.value || cursor || historyCursor) && (prior.purchaseRevision !== page.purchaseRevision ||
              prior.position.layer.currentCatalog.revision !== page.position.layer.currentCatalog.revision ||
              prior.position.location.revision !== page.position.location.revision))) { stale.value = true; return }
        review.value = page
      }
      const movedSources=target.sources.at(-1)!==confirmed.value.sources.at(-1), movedHistory=target.history.at(-1)!==confirmed.value.history.at(-1)
      confirmed.value = target
      if (movedSources || movedHistory) {
        await nextTick()
        if (active) (movedSources ? sourceHeading.value : historyHeading.value)?.focus()
      }
    } catch (failure) {
      if (!active) return
      if (failure instanceof ApiFailure && failure.detail?.code === 'stale_stock_loss_recovery_page') stale.value = true
      else error.value = explain(failure)
      emit('failure', failure)
    } finally { if (active) loading.value = false }
  }, { immediate: true })

async function submit() {
  const page = review.value, hash = window.location.hash
  if (!page || !allowed.value) return
  sending.value = true; feedback.value = ''
  try {
    const saved = await props.controls.dispatch({ kind: 'stock-loss-recovery', workspaceId: props.controls.workspaceId, body: {
      commandId: crypto.randomUUID(), purchaseId: page.position.layer.purchaseId, expectedRevision: page.purchaseRevision,
      positionId: props.route.position, postingId: props.route.posting, locationId: props.location.id, confirmedLocationCode: bin.value,
      catalogId: page.position.layer.catalogId, catalogRevision: page.position.layer.currentCatalog.revision, confirmedIdentity: true,
      reason: reason.value.trim(), allocations: rows.value.map(row => ({ quarantineId: row.source.quarantineId,
        saleableQuantity: Number(row.saleable), quarantineQuantity: Number(row.quarantine) }))
    } })
    if (!dialog.value?.isConnected || hash !== window.location.hash) return
    if (saved) window.location.hash = recoveryLink(props.location.id, props.route.position, props.route.posting, saved.entityId)
    else if (props.controls.pending) emit('recover')
    else { feedback.value = props.controls.feedback; stale.value = true }
  } finally { sending.value = false }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="recovery-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="recovery-title" autofocus tabindex="-1">{{ route.receipt ? 'Stock recovery receipt' : 'Inspect recovered pieces' }}</h2>
      <button class="close" aria-label="Close recovery details" :disabled="sending" @click="emit('close')">×</button></header>
    <div class="body">
      <p v-if="error || !connected" class="warning" role="status">{{ error || 'The workspace is offline.' }} {{ review || receipt ? 'Showing the last confirmed read.' : '' }}
        <button v-if="connected" :disabled="loading" @click="attempt++">Retry recovery read</button></p>
      <p v-else-if="loading || (!route.receipt && !workspaceReady)" role="status">Reading recovery evidence…</p>
      <p v-if="stale" class="warning" role="alert">The position, bin or reviewed identity changed. Review the current stock and enter your inspection again.
        <button :disabled="loading || !connected" @click="clear">Review updated stock</button></p>
      <template v-if="review && current">
        <p><strong>{{ itemLabel([current],current.id) }} · Bin {{ review.position.location.code }}</strong>
          <small>{{ review.position.layer.purchaseReference }} · {{ review.receipt.deliveryReference }} · Position {{ route.position }}</small></p>
        <p>{{ review.originalReason }}<small>Loss posted {{ time(review.originalPostedAt) }}</small></p>
        <dl class="details" aria-label="Loss balance"><dt>Originally posted loss</dt><dd>{{ review.originalLostQuantity }}</dd>
          <dt>Already recovered</dt><dd>{{ review.recoveredQuantity }}</dd><dt>Still missing</dt><dd>{{ review.remainingQuantity }}</dd>
          <dt>Currently in this position</dt><dd>{{ review.position.layer.quantity }}</dd>
          <dt>Original loss cost</dt><dd>{{ receiptCost(review.originalAllocatedItemCostMinor,review.receipt.currency) }}</dd>
          <dt>Previously restored cost</dt><dd>{{ receiptCost(review.restoredItemCostMinor,review.receipt.currency) }}</dd>
          <dt>Remaining loss cost</dt><dd>{{ receiptCost(review.remainingLostItemCostMinor,review.receipt.currency) }}</dd></dl>
        <p v-if="review.hasOpenShortage" class="warning">This position has an open shortage. It will remain open after recovery.</p>
        <p v-if="review.position.layer.catalogRevision !== current.revision" class="warning">This position was recorded at catalog revision {{ review.position.layer.catalogRevision }}.
          Confirm the current identity at revision {{ current.revision }}. Eligibility to back an offer is checked separately.</p>
        <p v-if="!current.integrationEligible" class="warning">The current catalog identity is ineligible for new offer backing.</p>
        <form @submit.prevent="submit">
          <section aria-label="Original missing pieces"><h3 ref="sourceHeading" tabindex="-1">Original missing pieces</h3>
            <p v-if="review.remainingQuantity">Enter only pieces you inspected. Assign them to saleable stock or quarantine damaged or uncertain pieces.</p>
            <p v-else>All pieces from this loss have been recovered.</p>
            <div v-for="source in review.sources" :key="sourceKey(source)" class="source">
              <strong>{{ source.quarantineId ? source.originalQuarantineReason : 'Ordinary stock' }}</strong>
              <a v-if="source.quarantineId" :href="quarantineLink(location.id,route.position,source.quarantineId)">Original quarantine {{ source.quarantineId }}</a>
              <small>{{ source.originalQuantity }} originally lost · {{ source.recoveredQuantity }} recovered · {{ source.remainingQuantity }} still missing</small>
              <small>Original cost {{ receiptCost(source.originalItemCostMinor,review.receipt.currency) }} · Restored {{ receiptCost(source.restoredItemCostMinor,review.receipt.currency) }} · Remaining {{ receiptCost(source.remainingItemCostMinor,review.receipt.currency) }}</small>
              <fieldset v-if="source.remainingQuantity" :disabled="!editable"><legend>Inspection of {{ source.quarantineId || 'ordinary stock' }}</legend>
                <label>Saleable quantity<input type="text" inputmode="numeric" maxlength="7" :value="choices[sourceKey(source)]?.saleable ?? ''" @input="select(source,'saleable',$event)" /></label>
                <label>Quarantine quantity<input type="text" inputmode="numeric" maxlength="7" :value="choices[sourceKey(source)]?.quarantine ?? ''" @input="select(source,'quarantine',$event)" /></label>
              </fieldset>
              <p v-if="choices[sourceKey(source)] && !validRow(choices[sourceKey(source)]!)" class="warning">Enter whole pieces up to the remaining {{ source.remainingQuantity }}.</p>
            </div>
            <div class="pager" v-if="confirmed.sources.length > 1 || review.nextAfter"><button type="button" :disabled="!healthy || confirmed.sources.length < 2" @click="pageBy('sources')">Previous sources</button>
              <button type="button" :disabled="!healthy || !review.nextAfter" @click="pageBy('sources',review.nextAfter)">Next sources</button></div>
          </section>
          <template v-if="review.remainingQuantity">
            <p aria-live="polite">{{ rows.length }} sources selected · {{ total('saleable') || 0 }} saleable · {{ total('quarantine') || 0 }} to quarantine</p>
            <details v-if="rows.length"><summary>Review selected sources</summary><ul><li v-for="row in rows" :key="sourceKey(row.source)">
              {{ row.source.quarantineId ? row.source.originalQuarantineReason + ' · ' + row.source.quarantineId : 'Ordinary stock' }} · {{ row.saleable || 0 }} saleable · {{ row.quarantine || 0 }} to quarantine</li></ul></details>
            <fieldset class="confirm" :disabled="!editable">
              <label>Confirm physical bin code<input v-model="bin" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
              <small v-if="bin && !bins.pending(bin) && !bins.matches(review.position.location,bin)" class="warning">Enter the code on the registered bin.</small>
              <label>Inspection findings and reason<input v-model="reason" required maxlength="200" /></label>
              <label class="check"><input type="checkbox" v-model="identity" /> I inspected these pieces and confirmed {{ itemLabel([current],current.id) }} at catalog revision {{ current.revision }}.</label>
            </fieldset>
            <p>The receipt records the restored cost. Existing build holds and offer backing stay unchanged.</p>
            <p v-if="feedback" role="alert" class="warning">{{ feedback }}</p>
            <button class="primary" :disabled="!allowed">Record inspected recovery</button>
          </template>
        </form>
        <section aria-label="Earlier recoveries"><h3 ref="historyHeading" tabindex="-1">Earlier recoveries</h3><p v-if="!review.recoveries.length">No recorded recoveries on this page.</p>
          <div v-for="prior in review.recoveries" :key="prior.id" class="source"><a :href="recoveryLink(location.id,route.position,route.posting,prior.id)">Recovery receipt {{ prior.id }}</a>
            <small>{{ time(prior.recordedAt) }} · {{ prior.saleableQuantity }} saleable · {{ prior.quarantineQuantity }} quarantined · {{ receiptCost(prior.restoredItemCostMinor,review.receipt.currency) }} restored</small></div>
          <div v-if="confirmed.history.length > 1 || review.nextHistoryAfter" class="pager"><button :disabled="!healthy || confirmed.history.length < 2" @click="pageBy('history')">Newer recoveries</button>
            <button :disabled="!healthy || !review.nextHistoryAfter" @click="pageBy('history',review.nextHistoryAfter)">Older recoveries</button></div>
        </section>
      </template>
      <template v-if="receipt"><p><strong>{{ itemLabel([receipt.recordedCatalog],receipt.recordedCatalog.id) }} · Confirmed bin {{ receipt.confirmedLocationCode }}</strong>
        <small>{{ receipt.receipt.deliveryReference }} · Inspected at revision {{ receipt.recordedCatalog.revision }} · {{ time(receipt.recordedAt) }}</small></p>
        <p>{{ receipt.reason }}</p>
        <dl class="details" aria-label="Recorded inspection"><dt>Returned to saleable stock</dt><dd>{{ receipt.saleableQuantity }}</dd><dt>Returned to quarantine</dt><dd>{{ receipt.quarantineQuantity }}</dd>
          <dt>Restored item cost</dt><dd>{{ receiptCost(receipt.restoredItemCostMinor,receipt.receipt.currency) }}</dd></dl>
        <h3 ref="sourceHeading" tabindex="-1">Recovery by original source</h3><div v-for="row in receipt.allocations" :key="row.quarantineId ?? 'ordinary'" class="source">
          <a v-if="row.quarantineId" :href="quarantineLink(location.id,route.position,row.quarantineId)">Original quarantine {{ row.quarantineId }}</a><strong v-else>Ordinary stock</strong>
          <small>{{ row.saleableQuantity }} saleable · {{ row.quarantineQuantity }} quarantined · {{ receiptCost(row.restoredItemCostMinor,receipt.receipt.currency) }} restored</small></div>
        <div class="pager" v-if="confirmed.sources.length > 1 || receipt.nextAfter"><button :disabled="!healthy || confirmed.sources.length < 2" @click="pageBy('sources')">Previous receipt sources</button>
          <button :disabled="!healthy || !receipt.nextAfter" @click="pageBy('sources',receipt.nextAfter)">Next receipt sources</button></div>
        <nav aria-label="Recovery evidence"><a v-if="receipt.recoveredQuarantineId" :href="quarantineLink(location.id,route.position,receipt.recoveredQuarantineId)">Recovered pieces in quarantine</a>
          <a :href="recoveryLink(location.id,route.position,route.posting)">Current recovery balance and history</a></nav>
        <details><summary>Receipt identifiers</summary><small>Recovery {{ receipt.id }}</small><small>Position {{ route.position }}</small>
          <small>Command {{ receipt.commandId }}</small><small>Correlation {{ receipt.correlationId }}</small></details>
      </template>
      <nav aria-label="Recovery navigation"><a :href="lossReceiptLink(location.id,route.position,route.posting)">Original posted loss</a><a :href="'#inventory/'+route.position">Position timeline</a>
        <a v-if="review || receipt" :href="'#inventory/lot/'+(review || receipt)!.receipt.deliveryId">Receipt lot</a></nav>
    </div>
  </dialog>
</template>
<style scoped>
.body, form, section, .source, fieldset, label {display:grid;gap:8px;min-width:0}
.body {padding:16px;gap:14px;overflow-wrap:anywhere}.body :is(p,h3,dl){margin:0}
.body small {display:block;color:var(--muted)}.body a {color:#355340}
.source {border-top:1px solid var(--line);padding-block:10px}.source fieldset {grid-template-columns:repeat(2,minmax(0,1fr))}
fieldset {border:0;padding:0}.source legend {font-size:.82rem;font-weight:600;margin-bottom:4px}
.confirm .check {display:flex;gap:8px;align-items:baseline}.check input{width:16px;flex:0 0 auto}
input {min-width:0;width:100%}.pager,nav {display:flex;flex-wrap:wrap;gap:4px 12px}
nav {grid-area:auto;padding:0;border:0}nav a{display:inline-block;padding:4px 0;text-decoration:underline}form>.primary {justify-self:end}
</style>
