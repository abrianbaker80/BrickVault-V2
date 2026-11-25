<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, explain, type Catalog, type Location, type TransferControls } from './api'
import { countReceiptLink, holdPositionLink, lossReceiptLink } from './countLinks'
import { itemLabel, money } from './receiving'
import { label, time } from './presentation'
import StockLossReceipt from './StockLossReceipt.vue'

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
const code = ref('')
const reason = ref('')
const loading = ref(false)
const stale = ref(false)
const error = ref('')
const attempted = ref(false)
const sending = ref(false)
const retry = ref(0)
const resolved = ref('')
const selected = computed(() => Object.values(selections.value))
const validQuantity = (entry: {offer: Offer; quantity: string}) => /^[0-9]+$/.test(entry.quantity) &&
  Number(entry.quantity) > 0 && Number(entry.quantity) <= entry.offer.backedQuantity
const chosen = computed(() => selected.value.reduce((total, row) => total + (validQuantity(row) ? Number(row.quantity) : 0),0))
const ready = computed(() => props.connected && props.controls.enabled && !sending.value && !loading.value &&
  !error.value && !stale.value && !resolved.value && !!page.value)
const permitted = computed(() => ready.value && !!page.value?.candidateLossQuantity && !page.value.protectedProjectShortfall && page.value.quarantineBlocksLoss === false)
const canPost = computed(() => permitted.value && page.value && selected.value.every(validQuantity) &&
  chosen.value === page.value.minimumOfferBackingReduction && code.value === page.value.location.code && reason.value.trim() !== '')
let mounted = true
onBeforeUnmount(() => { mounted = false })
onMounted(() => dialog.value?.showModal())

watch(() => [props.caseId, props.asOf, props.connected, requested.value, retry.value] as const, async ([caseId,,online,cursors],_,cleanup) => {
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
    const moved = cursors.length !== displayed.value.length
    page.value = value
    displayed.value = cursors
    if (moved) { await nextTick(); if(active) heading.value?.focus() }
  } catch (failure) {
    if (!active) return
    if (failure instanceof ApiFailure && failure.detail?.code === 'stale_adjustment_page') { stale.value = true; return }
    if (failure instanceof ApiFailure && failure.detail?.code === 'unknown_open_shortage') {
      try {
        const known = await api.postedLoss(props.location.id,props.position,{caseId},'',1)
        if (active && known.complete) { resolved.value = known.postingId; return }
      } catch (lookup) { if (active) emit('failure', lookup) }
    }
    if (active) { error.value = explain(failure); emit('failure',failure) }
  } finally { if (active) loading.value = false }
}, {immediate:true})
function restart() {
  selections.value = {}; code.value = ''; reason.value = ''; attempted.value = false
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
  sending.value = true
  try {
    const result = await props.controls.dispatch({kind:'stock-loss',workspaceId:props.controls.workspaceId,body:{
      commandId:crypto.randomUUID(),expectedRevision:current.purchaseRevision,purchaseId:current.purchaseId,positionId:props.position,
      locationId:props.location.id,confirmedLocationCode:code.value,caseId:current.caseId,expectedObservationId:current.latestObservation.id,
      reason:reason.value.trim(),offerReductions:selected.value.map(s=>({offerId:s.offer.id,quantity:Number(s.quantity)}))
    }})
    if (!mounted) return
    if (result) window.location.hash = lossReceiptLink(props.location.id,props.position,result.entityId)
    else if (props.controls.pending) emit('recover')
  } finally {sending.value=false}
}
</script>

<template>
  <dialog ref="dialog" aria-labelledby="loss-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="loss-title">{{ posting ? 'Posted stock loss' : 'Post stock loss' }}</h2>
      <button class="close" aria-label="Close stock loss" :disabled="sending" @click="emit('close')">×</button></header>
    <StockLossReceipt v-if="posting" :posting="posting" :bin="location.id" :position="position" :as-of="asOf" :connected="connected"
      @failure="emit('failure',$event)" />
    <div v-else class="loss-body">
      <p><strong>{{ location.code }} · {{ location.name }}</strong><small>Position ID {{ position }}</small></p>
      <p v-if="resolved" class="notice" role="status">The loss for this case has been posted.
        <a :href="lossReceiptLink(location.id,position,resolved)">View posted stock loss</a></p>
      <p v-if="stale" class="warning" role="alert">The count or purchase changed. Review current quantities and choose reductions again.
        <button :disabled="!connected || loading" @click="restart">Review updated quantities</button></p>
      <p v-if="!connected || error" class="warning" role="status">{{ error || 'The workspace is offline.' }}
        {{ page ? 'Showing the last confirmed review.' : 'Loss review unavailable.' }}
        <button v-if="connected" :disabled="loading" @click="retry++">Retry loss review</button></p>
      <p v-else-if="loading" role="status">Reading stock and offer backing…</p>
      <template v-if="page && !resolved">
        <p><strong>{{ page.receipt.deliveryReference }}</strong> · Purchase revision {{ page.purchaseRevision }}
          <small>{{ label(catalog.find(item=>item.id===page!.receipt.catalogId)?.itemType || 'Item') }} · {{ itemLabel(catalog,page.receipt.catalogId,page.receipt.catalogRevision) }}</small>
          <small>Read {{ time(page.asOf) }}</small></p>
        <a :href="countReceiptLink(location.id,page.latestObservation.id)">Latest witnessed count</a>
        <dl class="details"><dt>Current ledger</dt><dd>{{ page.ledgerQuantity }}</dd><dt>Observed quantity</dt><dd>{{ page.latestObservation.countedQuantity }}</dd>
          <dt>Loss to post</dt><dd>{{ page.candidateLossQuantity }}</dd><dt>Ledger after posting</dt><dd>{{ page.postLossLedgerQuantity }}</dd>
          <dt>Protected build holds</dt><dd>{{ page.protectedProjectQuantity }}</dd><dt>Capacity for offers after loss</dt><dd>{{ page.offerCapacityAfterLoss }}</dd>
          <dt>Currently backed here</dt><dd>{{ page.offerBackedQuantity }}</dd></dl>
        <a v-if="page.protectedProjectQuantity" :href="holdPositionLink(location.id,position)">Inspect named build holds</a>
        <p v-if="page.quarantineBlocksLoss" class="warning">{{ page.quarantinedQuantity }} quarantined in this position. The count does not identify which pieces are missing. Loss posting is blocked.</p>
        <p v-if="page.protectedProjectShortfall" class="warning">Build holds exceed remaining stock by {{ page.protectedProjectShortfall }}. Review the named holds before posting.</p>
        <p v-if="!page.candidateLossQuantity" class="warning">The latest witnessed count supports no stock loss.</p>
        <form @submit.prevent="post">
          <h3 ref="heading" tabindex="-1">Choose offer reductions</h3>
          <p>Reduce exactly {{ page.minimumOfferBackingReduction }} units of backing on this position. Enter 0 or leave a field blank to keep that offer’s backing.</p>
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
            <p aria-live="polite">{{ chosen }} of {{ page.minimumOfferBackingReduction }} units selected</p>
            <ul v-if="selected.length"><li v-for="entry in selected" :key="entry.offer.id">
              {{ entry.quantity }} of {{ entry.offer.backedQuantity }} · {{ money(entry.offer.unitPriceMinor,entry.offer.currency) }}
              <small>Offer {{ entry.offer.id }}</small>
              <button type="button" :disabled="!permitted" :aria-label="'Remove reduction for offer ' + entry.offer.id" @click="delete selections[entry.offer.id]">Remove</button></li></ul>
          </section>
          <label>Confirm physical bin code<input v-model="code" required maxlength="48" autocomplete="off" :disabled="!permitted" /></label>
          <small v-if="code && code !== page.location.code" class="warning">Enter the code on the selected bin.</small>
          <label>Reason for posting<input v-model="reason" required maxlength="200" :disabled="!permitted" /></label>
          <p v-if="attempted && controls.feedback" class="warning" role="alert">{{ controls.feedback }}</p>
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
