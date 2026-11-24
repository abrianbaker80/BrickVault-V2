<script setup lang="ts">
import { nextTick, ref, shallowRef, watch } from 'vue'
import { api, explain, type StockLoss } from './api'
import { countReceiptLink } from './countLinks'
import { money } from './receiving'
import { time } from './presentation'

const props = defineProps<{bin:string; position:string; posting:string; asOf:string; connected:boolean}>()
const emit = defineEmits<{failure:[reason:unknown]}>()
const page = shallowRef<StockLoss>()
const confirmed = shallowRef<string[]>([''])
const requested = shallowRef<string[]>([''])
const heading = ref<HTMLElement>()
const retry = ref(0)
const loading = ref(false)
const error = ref('')
watch(()=>[props.connected,props.asOf,requested.value,retry.value] as const,async ([online,,cursors],_,cleanup)=>{
  let active=true
  cleanup(()=>{active=false})
  loading.value=online
  if(!online) return
  error.value=''
  try {
    const result = await api.postedLoss(props.bin,props.position,{postingId:props.posting},cursors.at(-1))
    if(!active) return
    if(!result.complete) {error.value='Incomplete posting receipt.';return}
    const moved = confirmed.value.length !== cursors.length
    page.value=result
    confirmed.value=cursors
    if(moved) {await nextTick(); if(active) heading.value?.focus()}
  } catch(reason) {
    if(active) {error.value=explain(reason);emit('failure',reason)}
  } finally {if(active) loading.value=false}
},{immediate:true})
</script>
<template>
  <div class="posting">
    <p v-if="error || !connected" role="status" class="warning">{{ error || 'The workspace is offline.' }}
      {{ page ? 'Showing the last confirmed posting read.' : 'Posting receipt unavailable.' }}
      <button v-if="connected" @click="retry++">Retry posting receipt</button></p>
    <p v-else-if="loading" role="status">Reading stock-loss receipt…</p>
    <template v-if="page">
      <p><strong>{{ page.lostQuantity }} units of stock loss posted</strong> · {{ time(page.postedAt) }}
        <small>{{ page.receipt.deliveryReference }} · Confirmed bin {{ page.confirmedLocationCode }}</small></p>
      <p>{{ page.reason }}</p>
      <dl class="details"><dt>Ledger before posting</dt><dd>{{ page.ledgerQuantityBefore }}</dd><dt>Witnessed count</dt><dd>{{ page.reviewedObservation.countedQuantity }}</dd>
        <dt>Ledger after posting</dt><dd>{{ page.ledgerQuantityAfter }}</dd><dt>Protected build holds</dt><dd>{{ page.protectedProjectQuantity }}</dd>
        <dt>Offer backing before</dt><dd>{{ page.offerBackedQuantityBefore }}</dd><dt>Offer backing after</dt><dd>{{ page.offerBackedQuantityAfter }}</dd>
        <dt>Original delivery cost</dt><dd>{{ money(page.receipt.itemCostMinor,page.receipt.currency) }}</dd>
        <dt>Loss cost on this posting</dt><dd>{{ money(page.allocatedItemCostMinor,page.receipt.currency) }}</dd>
        <dt>Remaining delivery cost</dt><dd>{{ money(page.remainingDeliveryItemCostMinor,page.receipt.currency) }}</dd></dl>
      <a :href="countReceiptLink(bin,page.reviewedObservation.id)">Witnessed count used for this posting</a>
      <h3 ref="heading" tabindex="-1">Posted offer reductions</h3>
      <ul v-if="page.reductions.length" class="receipt-lines" aria-label="Posted offer reductions">
        <li v-for="r in page.reductions" :key="r.offerId"><strong>{{ r.quantity }} reduced · {{ r.remainingBackedQuantity }} of {{ r.priorBackedQuantity }} still backed here</strong>
          <p>{{ money(r.unitPriceMinor,r.currency) }} per unit</p><small>Offer {{ r.offerId }}</small></li></ul>
      <p v-else-if="confirmed.length === 1">No offer backing was reduced.</p>
      <nav v-if="confirmed.length > 1 || page.nextAfter" aria-label="Posted reduction pages"><button :disabled="loading || !connected || confirmed.length < 2" @click="requested=confirmed.slice(0,-1)">Previous reductions</button>
        <button :disabled="loading || !connected || !page.nextAfter" @click="page.nextAfter && (requested=[...confirmed,page.nextAfter])">More reductions</button></nav>
      <details><summary>Posting identifiers</summary><small>Posting {{ page.postingId }}</small><small>Position {{ page.positionId }}</small><small>Shortage {{ page.caseId }}</small>
        <small>Command {{ page.commandId }}</small><small>Correlation ID {{ page.correlationId }}</small><small>Purchase revision {{ page.revision }}</small></details>
    </template>
  </div>
</template>
<style scoped>
.posting { display:grid; gap:12px; padding:16px; overflow-wrap:anywhere }
.posting :is(p,h3,.details,.receipt-lines) { margin:0; padding:0 }
.posting small { display:block; color:var(--muted) }
.posting a { color:#355340 } nav {display:flex; flex-wrap:wrap; gap:8px} summary {cursor:pointer}
</style>
