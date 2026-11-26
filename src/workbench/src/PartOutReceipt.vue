<script setup lang="ts">
import { nextTick, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain, type PartOutEvidence } from './api'
import { partOutLink, quarantineLink } from './countLinks'
import { itemLabel, money } from './receiving'
import { time } from './presentation'

const props=defineProps<{source:string;receiptId:string;asOf:string;connected:boolean}>()
const emit=defineEmits<{failure:[reason:unknown]}>()
const page=shallowRef<PartOutEvidence>()
const first=()=>({outputs:[''],residuals:['']})
const confirmed=shallowRef(first()),requested=shallowRef(first())
const loading=ref(false),error=ref(''),attempt=ref(0)
const outputHeading=ref<HTMLElement>(),residualHeading=ref<HTMLElement>()
function turn(key:'outputs'|'residuals',next?:string|null) {
  const previous=confirmed.value[key]
  requested.value={...confirmed.value,[key]:next ? [...previous,next] : previous.slice(0,-1)}
}
watch(()=>[props.asOf,props.connected,requested.value,attempt.value] as const,async ([,connected,target],_,cleanup)=>{
  let active=true
  cleanup(()=>{active=false})
  loading.value=connected; error.value=''
  if (!connected) return
  try {
    const result=await api.partOutReceipt(props.source,{partOutId:props.receiptId},target.outputs.at(-1),target.residuals.at(-1))
    if (!active) return
    if (!result.complete) {error.value='Incomplete part-out receipt.';return}
    if ((result.nextOutputAfter && target.outputs.includes(result.nextOutputAfter)) ||
        (result.nextResidualAfter && target.residuals.includes(result.nextResidualAfter))) throw new ContractFailure()
    const outputsMoved=target.outputs.at(-1)!==confirmed.value.outputs.at(-1),residualsMoved=target.residuals.at(-1)!==confirmed.value.residuals.at(-1)
    page.value=result;confirmed.value=target
    if (outputsMoved || residualsMoved) { await nextTick();if (active) (outputsMoved ? outputHeading.value : residualHeading.value)?.focus() }
  } catch (reason) {if (active) {error.value=explain(reason);emit('failure',reason)}}
  finally {if(active) loading.value=false}
},{immediate:true})
</script>
<template>
  <section class="panel receipt" aria-label="Part-out receipt">
    <header class="panel-head"><h2>Part-out receipt</h2><a :href="'#inventory/'+source">Input position</a></header>
    <div class="body"><p v-if="error || !connected" class="warning" role="status">{{ error || 'The workspace is offline.' }} {{ page ? 'Showing the last confirmed receipt.' : '' }}
      <button v-if="connected" :disabled="loading" @click="attempt++">Retry receipt read</button></p>
      <p v-else-if="loading" role="status">Reading the part-out receipt…</p>
      <template v-if="page">
        <p><strong>{{ itemLabel([page.recordedSourceCatalog],page.recordedSourceCatalog.id) }}</strong><small>{{ page.receipt.deliveryReference }} · {{ page.receipt.deliveryQuantity }} units originally recorded at {{ money(page.receipt.itemCostMinor,page.receipt.currency) }} · Confirmed input bin {{ page.confirmedSourceLocationCode }} · {{ time(page.recordedAt) }}</small></p>
        <dl class="details"><dt>Input units consumed</dt><dd>{{ page.quantity }}</dd><dt>Consumed input cost</dt><dd>{{ money(page.sourceItemCostMinor,page.receipt.currency) }}</dd>
          <dt>Assigned to outputs</dt><dd>{{ money(page.sourceItemCostMinor===null || page.residualItemCostMinor===null ? null : page.sourceItemCostMinor-page.residualItemCostMinor,page.receipt.currency) }}</dd>
          <dt>Unallocated cost</dt><dd>{{ money(page.residualItemCostMinor,page.receipt.currency) }}</dd></dl>
        <p>{{ page.reason }}</p>
        <section aria-label="Recorded outputs"><h3 ref="outputHeading" tabindex="-1">Recorded outputs</h3>
          <ul class="receipt-lines"><li v-for="o in page.outputs" :key="o.lineId"><strong>{{ o.quantity }} × {{ itemLabel([o.recordedCatalog],o.recordedCatalog.id) }}</strong>
            <small>{{ o.disposition==='retained' ? 'Retained in quarantine' : 'Accepted stock' }} · Confirmed bin {{ o.confirmedLocationCode }} · {{ o.itemCostMinor===null && page.sourceItemCostMinor!==null ? 'Cost basis unassigned' : 'Assigned cost '+money(o.itemCostMinor,page.receipt.currency) }}</small>
            <nav aria-label="Output evidence"><a :href="'#inventory/'+o.positionId">Output position</a><a :href="'#inventory/lot/'+o.deliveryId">Output lot</a>
              <a v-if="o.quarantineId" :href="quarantineLink(o.location.id,o.positionId,o.quarantineId)">Retained quarantine receipt</a></nav>
          </li></ul>
          <div v-if="confirmed.outputs.length>1 || page.nextOutputAfter" class="pager"><button :disabled="!connected || loading || confirmed.outputs.length===1" @click="turn('outputs')">Previous outputs</button>
            <button :disabled="!connected || loading || !!error || !page.nextOutputAfter" @click="turn('outputs',page.nextOutputAfter)">More outputs</button></div>
        </section>
        <section aria-label="Physical remainder and loss"><h3 ref="residualHeading" tabindex="-1">Physical remainder and loss</h3>
          <p v-if="!page.physicalResiduals.length && confirmed.residuals.length===1">No discarded or lost material was recorded.</p>
          <ul v-else class="receipt-lines"><li v-for="r in page.physicalResiduals" :key="r.lineId">{{ r.quantity }} × {{ r.description }} · {{ r.disposition==='lost' ? 'Lost' : 'Discarded' }} at {{ page.confirmedSourceLocationCode }}</li></ul>
          <div v-if="confirmed.residuals.length>1 || page.nextResidualAfter" class="pager"><button :disabled="!connected || loading || confirmed.residuals.length===1" @click="turn('residuals')">Previous residuals</button>
            <button :disabled="!connected || loading || !!error || !page.nextResidualAfter" @click="turn('residuals',page.nextResidualAfter)">More residuals</button></div>
        </section>
        <details><summary>Receipt identifiers</summary><small>Transformation {{ page.id }}</small><small>Command {{ page.commandId }}</small><small>Correlation {{ page.correlationId }}</small></details>
        <nav aria-label="Part-out navigation"><a :href="'#inventory/lot/'+page.receipt.deliveryId">Original input lot</a><a :href="partOutLink(source)">Part-out history for this input</a></nav>
      </template>
    </div>
  </section>
</template>
<style scoped>
.receipt {max-width:920px;overflow-wrap:anywhere}.body,.body section {display:grid;gap:10px}.body {padding:16px;gap:16px}.body :is(p,h3,dl,ul){margin:0}
.receipt a {color:#355340}.receipt small{display:block;color:var(--muted)}.receipt nav,.pager{grid-area:auto;display:flex;flex-wrap:wrap;gap:4px 12px;padding:0;border:0}
.receipt nav a{display:inline-block;padding:4px 0;text-decoration:underline}
</style>
