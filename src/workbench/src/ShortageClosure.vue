<script setup lang="ts">
import { useBinRegistry } from "./useBinRegistry"
import { computed, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, ContractFailure, explain, requiresFreshCount, type Location, type TransferControls } from './api'
import { closureLink, countPositionLink, countReceiptLink, lossReceiptLink, type CountLink } from './countLinks'
import { money } from './receiving'
import { time } from './presentation'

type Route = NonNullable<NonNullable<CountLink>['closure']>
type Review = Awaited<ReturnType<typeof api.adjustmentReview>>
type Evidence = Awaited<ReturnType<typeof api.closure>>
const props=defineProps<{location:Location;route:Route;asOf:string;connected:boolean;workspaceReady:boolean;controls:TransferControls}>()
const emit=defineEmits<{close:[];failure:[reason:unknown];recover:[]}>()
const dialog=ref<HTMLDialogElement>()
const review=shallowRef<Review>()
const receipt=shallowRef<Evidence>()
const openingId=ref('')
const latestId=ref('')
const closureId=ref('')
const lossId=ref('')
const stale=ref(false)
const recountRequired=ref(false)
const error=ref('')
const loading=ref(false)
const retry=ref(0)
const bin=ref('')
const bins=useBinRegistry(()=>[bin.value])
const reason=ref('')
const acknowledged=ref(false)
const sending=ref(false)
const feedback=ref('')
const route=props.route
const exact=computed(()=>review.value && openingId.value!==route.id && review.value.latestObservation.id===route.id &&
  review.value.latestObservation.countedQuantity===review.value.ledgerQuantity && review.value.latestObservation.ledgerQuantity===review.value.ledgerQuantity &&
  !review.value.latestObservation.missingQuantity && !review.value.latestObservation.unidentifiedSurplusQuantity && !review.value.candidateLossQuantity)
const commitmentsFit=computed(()=>review.value && review.value.quarantinedQuantity!==undefined &&
  review.value.quarantinedQuantity+review.value.protectedProjectQuantity+review.value.offerBackedQuantity<=review.value.ledgerQuantity)
const ready=computed(()=>exact.value && commitmentsFit.value && !stale.value && !error.value && !loading.value && props.connected && props.workspaceReady &&
  props.controls.enabled && !sending.value && !closureId.value && !lossId.value)
const allowed=computed(()=>ready.value && bins.matches(review.value?.location,bin.value) && reason.value.trim().length>0 && acknowledged.value)
onMounted(()=>dialog.value?.showModal())
watch(()=>[props.asOf,props.connected,props.workspaceReady,props.location.revision,retry.value] as const,async (_,__,cleanup)=>{
  let active=true
  cleanup(()=>{active=false})
  loading.value=props.connected && props.workspaceReady
  if (!loading.value) return
  error.value=''
  try {
    if (route.mode==='receipt') {
      const saved=await api.closure(props.location.id,route.position,route.caseId,{receiptId:route.id})
      if (!active) return
      if (!saved.complete) {error.value='Incomplete shortage closure receipt.';return}
      receipt.value=saved
      return
    }
    const counts=await api.binCounts(props.location.id,{positionId:route.position,observationId:route.id},true)
    if (!active) return
    if (!counts.complete) {error.value='Incomplete count evidence.';return}
    const current=counts.shortages.find(row=>row.id===route.caseId)
    if (!current) {
      const closed=counts.closures?.find(row=>row.caseId===route.caseId)
      if (closed) {closureId.value=closed.id;return}
      const loss=await api.postedLoss(props.location.id,route.position,{observationId:route.id},'',1)
      if (!active) return
      if (!loss.complete) {error.value='Incomplete stock-loss receipt.';return}
      if (loss.caseId!==route.caseId) throw new ContractFailure()
      lossId.value=loss.postingId
      return
    }
    openingId.value=current.openingObservationId
    latestId.value=current.latestObservationId
    if (latestId.value!==route.id) {recountRequired.value=false;stale.value=true;return}
    const latest=await api.adjustmentReview(props.location.id,route.position,route.caseId)
    if (!active) return
    if (!latest.complete) {error.value='Incomplete closure review.';return}
    const position=counts.positions[0], witness=counts.observations[0]
    if (!position || !position.shortageOpen || !witness || latest.receipt.deliveryId!==position.deliveryId || latest.purchaseId!==position.purchaseId ||
        latest.latestObservation.id!==route.id || latest.latestObservation.countedQuantity!==witness.countedQuantity ||
        latest.purchaseRevision!==position.purchaseRevision || latest.ledgerQuantity!==position.ledgerQuantity ||
        latest.offerBackedQuantity!==position.offerBackedQuantity || latest.location.code!==counts.location.code ||
        latest.location.revision!==counts.location.revision || latest.location.code!==props.location.code ||
        latest.location.revision!==props.location.revision || (review.value && review.value.purchaseRevision!==latest.purchaseRevision)) {
      stale.value=true
      return
    }
    if (latest.quarantinedQuantity===undefined) throw new ContractFailure()
    recountRequired.value=false
    review.value=latest
  } catch (failure) {
    if (!active) return
    if (requiresFreshCount(failure)) {
      review.value=undefined
      stale.value=false
      recountRequired.value=true
      bin.value='';reason.value='';acknowledged.value=false
      return
    }
    error.value=failure instanceof ApiFailure && failure.status===404 ? 'No matching closure or open case was found for this bin and position.' : explain(failure)
    emit('failure',failure)
  } finally {if(active) loading.value=false}
},{immediate:true})
function restart() {
  bin.value='';reason.value='';acknowledged.value=false;stale.value=false;review.value=undefined;feedback.value='';retry.value++
}
async function submit() {
  const verified=review.value,issuingRoute=window.location.hash
  if (!allowed.value || !verified) return
  sending.value=true
  try {
    const result=await props.controls.dispatch({kind:'shortage-closure',workspaceId:props.controls.workspaceId,body:{
      commandId:crypto.randomUUID(),purchaseId:verified.purchaseId,expectedRevision:verified.purchaseRevision,
      positionId:route.position,locationId:props.location.id,confirmedLocationCode:bin.value,
      caseId:route.caseId,expectedObservationId:verified.latestObservation.id,reason:reason.value.trim()
    }})
    if (!dialog.value?.isConnected || issuingRoute!==window.location.hash) return
    if (result) window.location.hash=closureLink(props.location.id,route.position,route.caseId,'receipt',result.entityId)
    else if (props.controls.pending) emit('recover')
    else feedback.value=props.controls.feedback
  } finally {sending.value=false}
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="closure-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="closure-title" tabindex="-1" autofocus>{{ route.mode==='receipt' ? 'Shortage closure receipt' : 'Close shortage after exact recount' }}</h2>
      <button class="close" aria-label="Close shortage details" :disabled="sending" @click="emit('close')">×</button></header>
    <div class="closure-body">
      <p v-if="error || !connected" role="status" class="warning">{{ error || 'The workspace is offline.' }} {{ review || receipt ? 'Showing the last confirmed read.' : '' }}
        <button v-if="connected" :disabled="loading" @click="retry++">Retry closure read</button></p>
      <p v-else-if="loading || !workspaceReady" role="status">Reading the shortage and recount evidence…</p>
      <p v-if="recountRequired && !closureId && !lossId" class="warning" role="status">Stock moved after the last witnessed count. Recount this position before closing the shortage.
        <a :href="countPositionLink(location.id,route.position)">Record a new witnessed count</a></p>
      <p v-if="closureId">An operator has already closed this shortage after an exact recount.
        <a :href="closureLink(location.id,route.position,route.caseId,'receipt',closureId)">View shortage closure receipt</a></p>
      <p v-else-if="lossId">A stock loss was posted for this shortage.
        <a :href="lossReceiptLink(location.id,route.position,lossId)">View posted stock loss</a></p>
      <template v-else-if="route.mode==='review'">
        <p v-if="stale" class="warning" role="alert">The count, bin or purchase changed. Review the current evidence before closing this shortage.
          <a v-if="latestId && latestId!==route.id" :href="countReceiptLink(location.id,latestId)">Review latest witnessed count</a>
          <button v-else :disabled="loading || !connected" @click="restart">Review updated quantities</button></p>
        <template v-if="review">
          <p><strong>{{ review.receipt.deliveryReference }} · {{ review.location.code }}</strong><small>Position {{ route.position }}</small><small>Shortage {{ route.caseId }} · Purchase revision {{ review.purchaseRevision }}</small></p>
          <p v-if="exact">The later witnessed count matches the ledger. The shortage remains open until you confirm.</p>
          <p v-else-if="review.latestObservation.unidentifiedSurplusQuantity" class="warning">The recount has a surplus. Closure requires an exact count.</p>
          <p v-else class="warning">Closure requires a later witnessed count matching the current ledger.</p>
          <p v-if="!commitmentsFit" class="warning">Recorded commitments exceed the ledger quantity. Review the position before closing the shortage.</p>
          <dl class="details" aria-label="Evidence for closure">
            <dt>Ledger quantity</dt><dd>{{ review.ledgerQuantity }}</dd><dt>Latest witnessed count</dt><dd>{{ review.latestObservation.countedQuantity }}</dd>
            <dt>In quarantine</dt><dd>{{ review.quarantinedQuantity }}</dd><dt>Held for builds</dt><dd>{{ review.protectedProjectQuantity }}</dd>
            <dt>Backing offers</dt><dd>{{ review.offerBackedQuantity }}</dd><dt>Original delivery cost</dt><dd>{{ money(review.receipt.itemCostMinor,review.receipt.currency) }}</dd></dl>
          <nav aria-label="Count evidence"><a :href="countReceiptLink(location.id,openingId)">Opening count receipt</a><a :href="countReceiptLink(location.id,route.id)">Witnessed recount receipt</a></nav>
          <form @submit.prevent="submit"><fieldset :disabled="!ready">
            <label>Confirm physical bin code<input v-model="bin" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
            <small v-if="bin && !bins.pending(bin) && !bins.matches(review.location,bin)" class="warning">Enter the code on the registered bin.</small>
            <label>Reason for closure<input v-model="reason" required maxlength="200" /></label>
            <label class="check"><input type="checkbox" v-model="acknowledged" /> I reviewed this exact recount and confirm closure without posting a stock loss.</label>
          </fieldset>
          <p v-if="feedback" class="warning" role="alert">{{ feedback }}</p>
          <button class="primary" :disabled="!allowed">Confirm shortage closure</button></form>
        </template>
      </template>
      <template v-else-if="receipt">
        <p><strong>Shortage closed after exact recount</strong><small>{{ time(receipt.recordedAt) }} · Confirmed bin {{ receipt.confirmedLocationCode }} · {{ receipt.receipt.deliveryReference }}</small></p>
        <p>{{ receipt.reason }}</p>
        <dl class="details" aria-label="Stock at closure"><dt>Ledger quantity</dt><dd>{{ receipt.ledgerQuantity }}</dd><dt>Opening witnessed count</dt><dd>{{ receipt.openingObservation.countedQuantity }}</dd>
          <dt>Exact witnessed recount</dt><dd>{{ receipt.recountObservation.countedQuantity }}</dd><dt>In quarantine</dt><dd>{{ receipt.quarantinedQuantity }}</dd>
          <dt>Held for builds</dt><dd>{{ receipt.heldQuantity }}</dd><dt>Backing offers</dt><dd>{{ receipt.offerBackedQuantity }}</dd>
          <dt>Original delivery cost</dt><dd>{{ money(receipt.receipt.itemCostMinor,receipt.receipt.currency) }}</dd></dl>
        <p>The closure recorded zero stock loss. Quantities, commitments and cost were preserved.</p>
        <nav aria-label="Closure receipt navigation"><a :href="countReceiptLink(location.id,receipt.openingObservation.id)">Opening count receipt</a>
          <a :href="countReceiptLink(location.id,receipt.recountObservation.id)">Witnessed recount receipt</a>
          <a :href="countPositionLink(location.id,route.position)">Position count history</a>
          <a :href="'#inventory/'+route.position">Position timeline</a><a :href="'#inventory/lot/'+receipt.receipt.deliveryId">Receipt lot</a></nav>
        <details><summary>Receipt identifiers</summary><small>Receipt {{ receipt.id }}</small><small>Shortage {{ receipt.caseId }}</small>
          <small>Position {{ receipt.positionId }}</small><small>Command {{ receipt.commandId }}</small><small>Correlation {{ receipt.correlationId }}</small></details>
      </template>
    </div>
  </dialog>
</template>
<style scoped>
.closure-body, form, fieldset, fieldset label {display:grid;gap:10px}
.closure-body {padding:16px;overflow-wrap:anywhere}.closure-body :is(p,dl){margin:0}
.closure-body small {display:block;color:var(--muted)}.closure-body a{color:#355340}
fieldset {padding:0;border:0;min-width:0}fieldset .check {display:flex;gap:8px;align-items:baseline}.check input{width:16px;flex:0 0 auto}
nav {grid-area:auto;display:flex;gap:4px 12px;flex-wrap:wrap;padding:0;border:0}
nav a {display:inline-block;padding:4px 0;color:#355340;text-decoration:underline;text-underline-offset:2px}
nav a:hover {background:transparent;color:#203526}
form button{justify-self:end}
</style>
