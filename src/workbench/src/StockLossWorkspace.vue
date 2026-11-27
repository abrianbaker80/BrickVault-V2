<script setup lang="ts">
import { useBinRegistry } from "./useBinRegistry"
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, explain, requiresFreshCount, type Catalog, type Location, type QuarantineSnapshot, type TransferControls } from './api'
import { closureLink, countPositionLink, countReceiptLink, holdPositionLink, lossReceiptLink } from './countLinks'
import { itemLabel, money } from './receiving'
import { label, time } from './presentation'
import StockLossReceipt from './StockLossReceipt.vue'
import LossAttribution from './LossAttribution.vue'

const props = defineProps<{ location: Location; catalog: Catalog[]; position: string; caseId?: string; posting?: string;
  asOf: string; connected: boolean; controls: TransferControls }>()
const emit = defineEmits<{ failure: [reason: unknown]; close: []; recover: [] }>()
type Review = Awaited<ReturnType<typeof api.adjustmentReview>>
type Offer = Review['offers'][number]
const dialog = ref<HTMLDialogElement>()
const heading = ref<HTMLElement>()
const page = shallowRef<Review>()
const displayed = shallowRef<string[]>([''])
const requested = shallowRef<string[]>([''])
const selections = ref<Record<string,{offer: Offer; quantity: string}>>({})
const stock = shallowRef<QuarantineSnapshot>()
const quarantineCursors = shallowRef<string[]>([''])
const shownQuarantineCursors = shallowRef<string[]>([''])
const mode = ref<''|'none'|'records'>('')
const entries = ref<Record<string,{row:QuarantineSnapshot['quarantines'][number]; amount:string}>>({})
const code = ref('')
const bins=useBinRegistry(()=>[code.value])
const reason = ref('')
const loading = ref(false)
const stale = ref(false)
const recountRequired = ref(false)
const error = ref('')
const attempted = ref(false)
const sending = ref(false)
const retry = ref(0)
const resolved = ref('')
const closedWithoutLoss = ref('')
const selected = computed(() => Object.values(selections.value))
const validQuantity = (entry: {offer: Offer; quantity: string}) => /^[0-9]+$/.test(entry.quantity) &&
  Number(entry.quantity) > 0 && Number(entry.quantity) <= entry.offer.backedQuantity
const chosen = computed(() => selected.value.reduce((total, row) => total + (validQuantity(row) ? Number(row.quantity) : 0),0))
const ready = computed(() => props.connected && props.controls.enabled && !sending.value && !loading.value &&
  !error.value && !stale.value && !resolved.value && !closedWithoutLoss.value && !!page.value)
const needsAttribution = computed(() => Boolean(page.value?.quarantinedQuantity && page.value.candidateLossQuantity))
const attributed = computed(() => {
  if (!needsAttribution.value || mode.value === 'none') return 0
  const rows = Object.values(entries.value)
  if (mode.value !== 'records' || !rows.length || rows.some(({row,amount}) =>
      !/^[0-9]+$/.test(amount) || Number(amount)<1 || Number(amount)>row.remainingQuantity)) return null
  return rows.reduce((sum,entry) => sum+Number(entry.amount),0)
})
const projection = computed(() => {
  const review = page.value, lostFromQuarantine = attributed.value
  if (!review || lostFromQuarantine === null || review.quarantinedQuantity === undefined) return
  const ordinaryLoss = review.candidateLossQuantity-lostFromQuarantine
  const unavailableAfter = review.quarantinedQuantity-lostFromQuarantine
  if (ordinaryLoss<0 || ordinaryLoss>review.ledgerQuantity-review.quarantinedQuantity || unavailableAfter<0) return
  const ordinaryAfter=review.postLossLedgerQuantity-unavailableAfter
  const holdShortfall=Math.max(0,review.protectedProjectQuantity-ordinaryAfter)
  const capacity=Math.max(0,ordinaryAfter-review.protectedProjectQuantity)
  return {ordinaryLoss,unavailableAfter,ordinaryAfter,holdShortfall,capacity,reduction:Math.max(0,review.offerBackedQuantity-capacity)}
})
const permitted = computed(() => ready.value && !!page.value?.candidateLossQuantity && projection.value?.holdShortfall === 0)
const canPost = computed(() => permitted.value && page.value && selected.value.every(validQuantity) &&
  chosen.value === projection.value?.reduction && bins.matches(page.value.location,code.value) && reason.value.trim() !== '')
let mounted = true
onBeforeUnmount(() => { mounted = false })
onMounted(() => dialog.value?.showModal())

watch(() => [props.caseId, props.asOf, props.connected, requested.value, quarantineCursors.value, retry.value] as const, async ([caseId,,online,cursors,qCursors],_,cleanup) => {
  let active = true
  cleanup(() => {active=false})
  loading.value = !!caseId && online
  if (!caseId || !online) return
  error.value = ''
  try {
    const value = await api.adjustmentReview(props.location.id,props.position,caseId,cursors.at(-1))
    if (!active) return
    if (!value.complete) { error.value = 'Incomplete loss review. Refresh before posting.'; return }
    if (value.location.code !== props.location.code || (page.value && (value.purchaseRevision !== page.value.purchaseRevision ||
        value.latestObservation.id !== page.value.latestObservation.id || value.location.code !== page.value.location.code))) {
      stale.value = true
      return
    }
    let paired:QuarantineSnapshot | undefined
    if (value.quarantinedQuantity && value.candidateLossQuantity) {
      paired=await api.quarantine(props.location.id,props.position,qCursors.at(-1))
      if (!active) return
      if (!paired.complete) {error.value='Incomplete quarantine records. Refresh before posting.'; return}
      if (paired.purchaseRevision!==value.purchaseRevision || !paired.hasOpenShortage || paired.position.location.code!==value.location.code ||
          paired.position.layer.purchaseId!==value.purchaseId || paired.position.layer.deliveryId!==value.receipt.deliveryId ||
          paired.quarantinedQuantity!==value.quarantinedQuantity || paired.onHandQuantity!==value.ledgerQuantity ||
          paired.heldQuantity!==value.protectedProjectQuantity || paired.offerBackedQuantity!==value.offerBackedQuantity) {
        stale.value=true
        return
      }
    }
    if ((value.nextAfter && cursors.includes(value.nextAfter)) || (paired?.nextAfter && qCursors.includes(paired.nextAfter))) {
      error.value='The loss review returned a repeated page. Refresh before posting.'
      return
    }
    const moved = cursors.length !== displayed.value.length
    stock.value=paired
    shownQuarantineCursors.value=qCursors
    recountRequired.value = false
    page.value = value
    displayed.value = cursors
    if (moved) { await nextTick(); if(active) heading.value?.focus() }
  } catch (failure) {
    if (!active) return
    if (requiresFreshCount(failure)) {
      recountRequired.value = true
      clearDecision()
      stale.value = false
      displayed.value = ['']
      page.value = undefined
      if (requested.value.length > 1) requested.value = ['']
      return
    }
    if (failure instanceof ApiFailure && failure.detail?.code === 'stale_adjustment_page') { stale.value = true; return }
    if (failure instanceof ApiFailure && failure.detail?.code === 'unknown_open_shortage') {
      try {
        const known = await api.postedLoss(props.location.id,props.position,{caseId},'',1)
        if (active && known.complete) { recountRequired.value=false; resolved.value = known.postingId; return }
      } catch (lookup) {
        if (!active) return
        if (lookup instanceof ApiFailure && lookup.status===404 && lookup.detail?.code==='unknown_stock_loss') {
          try {
            const closed=await api.closure(props.location.id,props.position,caseId)
            if (!active) return
            if (!closed.complete) {error.value='Incomplete shortage closure receipt.';return}
            recountRequired.value=false; closedWithoutLoss.value=closed.id
            return
          } catch (e) {if(active) {error.value=explain(e);emit('failure',e)};return}
        }
        error.value=explain(lookup);emit('failure',lookup);return
      }
    }
    if (active) { error.value = explain(failure); emit('failure',failure) }
  } finally { if (active) loading.value = false }
}, {immediate:true})
function clearDecision() {
  selections.value = {}; code.value = ''; reason.value = ''; attempted.value = false
  stock.value=undefined; mode.value=''; entries.value={}
  if (quarantineCursors.value.length > 1) quarantineCursors.value=['']
  shownQuarantineCursors.value=['']
}
function restart() {
  clearDecision()
  page.value = undefined; stale.value = false; displayed.value = ['']; requested.value = ['']; ++retry.value
}
function edit(offer: Offer, event: Event) {
  const value = (event.target as HTMLInputElement).value
  if (value === '' || /^0+$/.test(value)) delete selections.value[offer.id]
  else selections.value[offer.id] = {offer,quantity:value}
}
async function post() {
  const current = page.value
  if (!current || !canPost.value) return
  attempted.value = true
  const issuingHash = window.location.hash, issuingDialog = dialog.value
  sending.value = true
  try {
    const result = await props.controls.dispatch({kind:'stock-loss',workspaceId:props.controls.workspaceId,body:{
      commandId:crypto.randomUUID(),expectedRevision:current.purchaseRevision,purchaseId:current.purchaseId,positionId:props.position,
      locationId:props.location.id,confirmedLocationCode:code.value,caseId:current.caseId,expectedObservationId:current.latestObservation.id,
      ...(needsAttribution.value ? {quarantineAttributions:Object.values(entries.value).map(({row,amount})=>({quarantineId:row.id,quantity:Number(amount)}))} : {}),
      reason:reason.value.trim(),offerReductions:selected.value.map(s=>({offerId:s.offer.id,quantity:Number(s.quantity)}))
    }})
    if (!mounted) return
    if (!issuingDialog?.isConnected || window.location.hash !== issuingHash) return
    if (result) window.location.hash = lossReceiptLink(props.location.id,props.position,result.entityId)
    else if (props.controls.pending) emit('recover')
  } finally {sending.value=false}
}
</script>

<template>
  <dialog ref="dialog" aria-labelledby="loss-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="loss-title">{{ posting ? 'Posted stock loss' : 'Post stock loss' }}</h2>
      <button class="close" aria-label="Close stock loss" :disabled="sending" @click="emit('close')">×</button></header>
    <StockLossReceipt v-if="posting" :posting="posting" :bin="location.id" :position="position" :as-of="String(location.revision)" :connected="connected"
      @failure="emit('failure',$event)" />
    <div v-else class="loss-body">
      <p><strong>{{ location.code }} · {{ location.name }}</strong><small>Position ID {{ position }}</small></p>
      <p v-if="closedWithoutLoss && caseId" role="status">The shortage was closed after an exact recount.
        <a :href="closureLink(location.id,position,caseId,'receipt',closedWithoutLoss)">View shortage closure receipt</a></p>
      <p v-if="resolved" class="notice" role="status">The loss for this case has been posted.
        <a :href="lossReceiptLink(location.id,position,resolved)">View posted stock loss</a></p>
      <p v-if="recountRequired" class="warning" role="status">Stock moved after the last witnessed count. Recount this position before posting a loss.
        <a :href="countPositionLink(location.id,position)">Record a new witnessed count</a></p>
      <p v-if="stale && !recountRequired" class="warning" role="alert">The count or purchase changed. Review current quantities and choose reductions again.
        <button :disabled="!connected || loading" @click="restart">Review updated quantities</button></p>
      <p v-if="!connected || error" class="warning" role="status">{{ error || 'The workspace is offline.' }}
        {{ page ? 'Showing the last confirmed review.' : 'Loss review unavailable.' }}
        <button v-if="connected" :disabled="loading" @click="retry++">Retry loss review</button></p>
      <p v-else-if="loading" role="status">Reading stock and offer backing…</p>
      <template v-if="page && !resolved && !closedWithoutLoss">
        <p><strong>{{ page.receipt.deliveryReference }}</strong> · Purchase revision {{ page.purchaseRevision }}
          <small>{{ label(catalog.find(item=>item.id===page!.receipt.catalogId)?.itemType || 'Item') }} · {{ itemLabel(catalog,page.receipt.catalogId,page.receipt.catalogRevision) }}</small>
          <small>Read {{ time(page.asOf) }}</small></p>
        <a :href="countReceiptLink(location.id,page.latestObservation.id)">Latest witnessed count</a>
        <dl class="details"><dt>Current ledger</dt><dd>{{ page.ledgerQuantity }}</dd><dt>Observed quantity</dt><dd>{{ page.latestObservation.countedQuantity }}</dd>
          <dt>Loss to post</dt><dd>{{ page.candidateLossQuantity }}</dd><dt>Ledger after posting</dt><dd>{{ page.postLossLedgerQuantity }}</dd>
          <dt>Protected build holds</dt><dd>{{ page.protectedProjectQuantity }}</dd><dt>Capacity for offers after loss</dt><dd>{{ projection?.capacity ?? 'Pending identification of missing pieces' }}</dd>
          <dt>Currently backed here</dt><dd>{{ page.offerBackedQuantity }}</dd></dl>
        <a v-if="page.protectedProjectQuantity" :href="holdPositionLink(location.id,position)">Inspect named build holds</a>
        <p v-if="projection?.holdShortfall" class="warning">Build holds exceed remaining stock by {{ projection.holdShortfall }}. Review the named holds before posting.</p>
        <p v-if="!page.candidateLossQuantity" class="warning">The latest witnessed count supports no stock loss.</p>
        <form @submit.prevent="post">
          <template v-if="needsAttribution && stock">
            <p>{{ page.quarantinedQuantity }} of the {{ page.ledgerQuantity }} pieces are in quarantine.</p>
            <LossAttribution :stock="stock" :enabled="ready" :page-number="shownQuarantineCursors.length" v-model:mode="mode" v-model:entries="entries"
              @previous="quarantineCursors=shownQuarantineCursors.slice(0,-1)" @next="stock.nextAfter && (quarantineCursors=[...shownQuarantineCursors,stock.nextAfter])" />
            <dl v-if="projection" class="details" aria-label="Loss attribution preview" aria-live="polite">
              <dt>Missing from quarantine</dt><dd>{{ attributed }}</dd><dt>Missing outside quarantine</dt><dd>{{ projection.ordinaryLoss }}</dd>
              <dt>Remaining in quarantine</dt><dd>{{ projection.unavailableAfter }}</dd><dt>Remaining outside quarantine</dt><dd>{{ projection.ordinaryAfter }}</dd></dl>
            <p v-else-if="mode==='none' || Object.keys(entries).length" class="warning">The selected quantities must fit each record, the units outside quarantine and the total loss of {{ page.candidateLossQuantity }}.</p>
          </template>
          <h3 ref="heading" tabindex="-1">Choose offer reductions</h3>
          <p v-if="projection && !projection.holdShortfall">Reduce exactly {{ projection.reduction }} {{ projection.reduction===1 ? 'unit' : 'units' }} of backing on this position. Enter 0 or leave a field blank to keep that offer’s backing.</p>
          <ul v-if="page.offers.length" class="receipt-lines" aria-label="Offer backing on this position">
            <li v-for="offer in page.offers" :key="offer.id">
              <strong>{{ money(offer.unitPriceMinor,offer.currency) }} per unit · {{ offer.backedQuantity }} backed here</strong>
              <label>Reduce units for offer {{ offer.id }}
                <input type="text" inputmode="numeric" pattern="[0-9]+" :value="selections[offer.id]?.quantity || ''" :disabled="!permitted" @input="edit(offer,$event)" /></label>
              <small v-if="selections[offer.id] && !validQuantity(selections[offer.id])" class="warning">Enter 0–{{ offer.backedQuantity }} whole units.</small>
            </li></ul>
          <p v-else-if="!page.offerCount">No unpublished offers use this position.</p>
          <nav v-if="displayed.length > 1 || page.nextAfter" aria-label="Reduction offer pages">
            <button type="button" :disabled="!ready || displayed.length < 2" @click="requested = displayed.slice(0,-1)">Previous offers</button>
            <span>Page {{ displayed.length }}</span>
            <button type="button" :disabled="!ready || !page.nextAfter" @click="page.nextAfter && (requested=[...displayed,page.nextAfter])">More offers</button>
          </nav>
          <section class="decision" aria-label="Selected offer reductions"><h3>Selected reductions</h3>
            <p v-if="projection && !projection.holdShortfall" aria-live="polite">{{ chosen }} of {{ projection.reduction }} {{ projection.reduction===1 ? 'unit' : 'units' }} selected</p>
            <ul v-if="selected.length"><li v-for="entry in selected" :key="entry.offer.id">
              {{ entry.quantity }} of {{ entry.offer.backedQuantity }} · {{ money(entry.offer.unitPriceMinor,entry.offer.currency) }}
              <small>Offer {{ entry.offer.id }}</small>
              <button type="button" :disabled="!permitted" :aria-label="'Remove reduction for offer ' + entry.offer.id" @click="delete selections[entry.offer.id]">Remove</button></li></ul>
          </section>
          <label>Confirm physical bin code<input v-model="code" required maxlength="48" autocomplete="off" :disabled="!permitted" /></label>
          <small v-if="code && !bins.pending(code) && !bins.matches(page.location,code)" class="warning">Enter the code on the selected bin.</small>
          <label>Reason for posting<input v-model="reason" required maxlength="200" :disabled="!permitted" /></label>
          <p v-if="attempted && controls.feedback" class="warning" role="alert">{{ controls.feedback }}</p>
          <p v-if="needsAttribution && projection" aria-label="Posting summary">Posting {{ page.candidateLossQuantity }} missing {{ page.candidateLossQuantity===1 ? 'piece' : 'pieces' }}.
            {{ attributed }} from {{ Object.keys(entries).length }} quarantine {{ Object.keys(entries).length===1 ? 'record' : 'records' }} and {{ projection.ordinaryLoss }} from outside quarantine.</p>
          <div class="actions"><button class="primary" :disabled="!canPost">Post stock loss and selected reductions</button></div>
        </form>
      </template>
    </div>
  </dialog>
</template>
<style scoped>
.loss-body, form, form label, .decision { display: grid; gap: 8px }
.loss-body { padding: 16px; overflow-wrap: anywhere }
.loss-body :is(p,h3,dl,ul) { margin: 0 }
.loss-body small { display: block; color: var(--muted) }
.loss-body .receipt-lines { padding: 0 }
form { gap: 12px } form label { min-width: 0; font-size: 13px }
form :is(input,button), nav { max-width: 100% }
nav { display: flex; flex-wrap: wrap; gap: 8px; align-items: center }
.decision { border-block: 1px solid var(--line); padding: 10px 0 }
.decision ul { list-style: none; padding: 0 } .decision li + li { margin-top: 6px }
a { color: #355340 }
</style>
