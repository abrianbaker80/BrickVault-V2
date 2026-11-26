<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain, type PartOutCommand, type PartOutReview, type TransferControls, type Workspace } from './api'
import { partOutLink } from './countLinks'
import { itemLabel, money, parseMoney } from './receiving'
import { time } from './presentation'
import PartOutReceipt from './PartOutReceipt.vue'

type Output = {id:string; catalog:string; revision:number; bin:string; binRevision:number; code:string; quantity:string;
  disposition:''|'stock'|'retained'; basis:''|'known'|'unassigned'; amount:string}
type Residual = {id:string; disposition:''|'discarded'|'lost'; description:string; quantity:string}
const props=defineProps<{source:string;receiptId?:string;workspace:Workspace;connected:boolean;controls:TransferControls}>()
const emit=defineEmits<{failure:[reason:unknown];refresh:[]}>()
const page=shallowRef<PartOutReview>()
const requested=shallowRef<string[]>(['']),confirmed=shallowRef<string[]>([''])
const loading=ref(false),error=ref(''),stale=ref(false),retry=ref(0),sending=ref(false),feedback=ref('')
const input=ref(''),code=ref(''),reason=ref(''),acknowledged=ref(false),residualCost=ref('')
const residualMode=ref<''|'none'|'record'>('')
const makeOutput=():Output=>({id:crypto.randomUUID(),catalog:'',revision:0,bin:'',binRevision:0,code:'',quantity:'',disposition:'',basis:'',amount:''})
const outputs=ref<Output[]>([makeOutput()]),physical=ref<Residual[]>([])
const historyHeading=ref<HTMLElement>()
watch([input,code,outputs,physical,residualMode,residualCost],()=>{ acknowledged.value=false },{deep:true,flush:'sync'})
const integer=(s:string)=>/^\d{1,7}$/.test(s) && Number(s)>0 && Number(s)<=1_000_000
const dirty=computed(()=>!!(input.value || code.value || reason.value || residualMode.value || acknowledged.value ||
  outputs.value.length!==1 || outputs.value.some(o=>o.catalog || o.bin || o.quantity || o.amount || o.disposition)))
const current=computed(()=>page.value?.position.layer.currentCatalog)
const catalog=(id:string)=>props.workspace.catalog.find(c=>c.id===id)
const location=(id:string)=>props.workspace.locations.find(l=>l.id===id)
const changedOutput=computed(()=>outputs.value.some(o=>(o.catalog && catalog(o.catalog)?.revision!==o.revision) ||
  (o.bin && location(o.bin)?.revision!==o.binRevision)))
const live=computed(()=>props.connected && props.workspace.complete && !loading.value && !error.value && !stale.value && !changedOutput.value)
const editable=computed(()=>live.value && props.controls.enabled && !sending.value)
const selected=computed(()=>page.value?.selectionAvailable && integer(input.value) && Number(input.value)===page.value.requestedQuantity)
const known=computed(()=>selected.value && page.value!.sourceItemCostMinor!==null)
function amount(text:string) {
  if (!page.value || !text.trim()) return undefined
  try { return parseMoney(text,page.value.currency) } catch { return undefined }
}
function outputCost(o:Output) {return !known.value || o.basis==='unassigned' ? null : amount(o.amount)}
const assigned=computed(()=>outputs.value.reduce((total,o)=>total+(outputCost(o) ?? 0),0))
const balanced=computed(()=>!known.value || (amount(residualCost.value)!==undefined && page.value!.sourceItemCostMinor===assigned.value+amount(residualCost.value)!))
const repeatsInput=computed(()=>{
  const inputCatalogId=page.value?.position.layer.catalogId
  if (!inputCatalogId || !integer(input.value)) return false
  const matching=outputs.value.filter(o=>o.catalog===inputCatalogId && integer(o.quantity))
  return matching.reduce((total,o)=>total+Number(o.quantity),0)>Number(input.value)
})
const validOutputs=computed(()=>!repeatsInput.value && outputs.value.length>0 && outputs.value.every(o=>(o.disposition==='retained' || catalog(o.catalog)?.integrationEligible) && o.revision===catalog(o.catalog)?.revision &&
  location(o.bin)?.code===o.code && integer(o.quantity) && !!o.disposition && (!known.value || (o.basis && outputCost(o)!==undefined))) &&
  outputs.value.reduce((n,o)=>n+Number(o.quantity),0)<=1_000_000)
const validResiduals=computed(()=>residualMode.value==='none' || (residualMode.value==='record' && physical.value.length>0 &&
  physical.value.every(r=>r.disposition && r.description.trim() && integer(r.quantity)) && physical.value.reduce((n,r)=>n+Number(r.quantity),0)<=1_000_000))
const allowed=computed(()=>editable.value && selected.value && !page.value?.hasOpenShortage && code.value===page.value?.position.location.code &&
  validOutputs.value && validResiduals.value && balanced.value && !!reason.value.trim() && acknowledged.value)
function selectCatalog(o:Output,event:Event) {
  o.catalog=(event.target as HTMLSelectElement).value; o.revision=catalog(o.catalog)?.revision ?? 0;acknowledged.value=false
}
function selectBin(o:Output,event:Event) {
  o.bin=(event.target as HTMLSelectElement).value;o.binRevision=location(o.bin)?.revision ?? 0;o.code='';acknowledged.value=false
}
function addResidual() {physical.value.push({id:crypto.randomUUID(),disposition:'',description:'',quantity:''})}
function changeResidualMode(mode:'none'|'record') {
  residualMode.value=mode
  if (mode==='none') physical.value=[]
  else if (!physical.value.length) addResidual()
}
function reset() {
  input.value='';code.value='';reason.value='';acknowledged.value=false;residualCost.value=''
  outputs.value=[makeOutput()];physical.value=[];residualMode.value='';feedback.value=''
  page.value=undefined;requested.value=[''];stale.value=false;emit('refresh');retry.value++
}
const previewQuantity=computed(()=>integer(input.value) ? Number(input.value) : 1)
watch(()=>[props.receiptId,props.workspace.asOf,props.workspace.complete,props.connected,previewQuantity.value,requested.value,retry.value] as const,
 async ([receipt,,complete,connected,quantity,pages],_,cleanup)=>{
  let active=true
  cleanup(()=>{active=false})
  loading.value=!receipt && complete && connected;error.value=''
  if(!loading.value) return
  try {
    const result=await api.partOutReview(props.source,quantity,pages.at(-1))
    if(!active) return
    if (!result.complete) {error.value='Incomplete part-out review.';return}
    if (result.nextAfter && pages.includes(result.nextAfter)) throw new ContractFailure()
    const previous=page.value,layer=result.position.layer,bin=result.position.location
    if (catalog(layer.catalogId)?.revision!==layer.currentCatalog.revision || location(bin.id)?.revision!==bin.revision) {
      if (dirty.value) stale.value=true
      else error.value='The catalog or bin register needs refreshing to match this position.'
      return
    }
    if (previous && dirty.value && (previous.purchaseRevision!==result.purchaseRevision ||
        previous.position.layer.currentCatalog.revision!==layer.currentCatalog.revision || previous.position.location.revision!==bin.revision)) {
      stale.value=true;return
    }
    const moved=pages.at(-1)!==confirmed.value.at(-1)
    page.value=result;confirmed.value=pages
    if(moved) {await nextTick();if(active) historyHeading.value?.focus()}
  } catch(failure) {if(active) {error.value=explain(failure);emit('failure',failure)}}
  finally {if(active) loading.value=false}
},{immediate:true})
async function save() {
  const reviewed=page.value,hash=window.location.hash
  if (!allowed.value || !reviewed || !current.value) return
  const body:PartOutCommand={commandId:crypto.randomUUID(),purchaseId:reviewed.position.layer.purchaseId,expectedRevision:reviewed.purchaseRevision,
    sourcePositionId:props.source,sourceLocationId:reviewed.position.location.id,confirmedSourceLocationCode:code.value,
    sourceCatalogId:current.value.id,sourceCatalogRevision:current.value.revision,quantity:Number(input.value),reason:reason.value.trim(),
    outputs:outputs.value.map(o=>({lineId:o.id,catalogId:o.catalog,catalogRevision:o.revision,locationId:o.bin,confirmedLocationCode:o.code,
      disposition:o.disposition as 'stock'|'retained',quantity:Number(o.quantity),itemCostMinor:outputCost(o) ?? null})),
    residualItemCostMinor:known.value ? amount(residualCost.value)! : null,
    physicalResiduals:physical.value.map(r=>({lineId:r.id,disposition:r.disposition as 'discarded'|'lost',description:r.description.trim(),quantity:Number(r.quantity)}))}
  sending.value=true
  try {
    const saved=await props.controls.dispatch({kind:'part-out',workspaceId:props.controls.workspaceId,body})
    if (hash!==window.location.hash) return
    if (saved) window.location.hash=partOutLink(props.source,saved.entityId)
    else if (!props.controls.pending) {feedback.value=props.controls.feedback;stale.value=true}
  } finally {sending.value=false}
}
</script>
<template>
  <PartOutReceipt v-if="receiptId" :source="source" :receipt-id="receiptId" :as-of="workspace.asOf" :connected="connected" @failure="emit('failure',$event)" />
  <section v-else class="part-out" aria-label="Manual part-out">
    <header class="panel panel-head"><h2>Manual part-out</h2><a :href="'#inventory/'+source">Input position</a></header>
    <p v-if="error || !connected || !workspace.complete" class="warning" role="status">{{ error || (!connected ? 'The workspace is offline.' : 'The workspace read is incomplete.') }} {{ page ? 'Showing the last confirmed position.' : '' }}
      <button v-if="connected && workspace.complete" :disabled="loading" @click="emit('refresh');retry++">Retry part-out review</button></p>
    <p v-if="stale || changedOutput" class="warning" role="alert">The stock, catalog identity or registered bin changed. Review the position and confirm the physical items again.
      <button :disabled="loading || !connected" @click="reset">Review updated stock</button></p>
    <p v-else-if="loading" role="status">Reading input stock and cost…</p>
    <template v-if="page && current">
      <div class="layout"><aside class="panel context" aria-label="Input receipt and commitments">
        <h3>Input receipt</h3><p><strong>{{ itemLabel([current],current.id) }}</strong><small>Current catalog revision {{ current.revision }} · Placed at revision {{ page.position.layer.catalogRevision }}</small></p>
        <p>{{ page.position.layer.purchaseReference }} · {{ page.position.layer.deliveryReference }}<small>Bin {{ page.position.location.code }} · {{ page.position.location.name }}</small></p>
        <p>Original input lot · {{ page.position.layer.receiptQuantity }} units · {{ money(page.position.layer.receiptItemCostMinor,page.currency) }} item cost</p>
        <a :href="'#inventory/lot/'+page.position.layer.deliveryId">Inspect input receipt lot</a>
        <dl class="details"><dt>On hand</dt><dd>{{ page.position.layer.quantity }}</dd><dt>Held for builds</dt><dd>{{ page.heldQuantity }}</dd>
          <dt>Backing offers</dt><dd>{{ page.position.layer.backedQuantity }}</dd><dt>Quarantined</dt><dd>{{ page.position.layer.quarantinedQuantity }}</dd>
          <dt>Available for part-out</dt><dd>{{ page.availableQuantity }}</dd></dl>
        <small>Purchase revision {{ page.purchaseRevision }} · Read {{ time(page.asOf) }}</small>
      </aside>
      <form class="panel form" @submit.prevent="save">
        <h3>Confirm the input</h3>
        <p v-if="page.hasOpenShortage" class="warning" role="status">An open shortage blocks part-out for this position.</p>
        <p v-else-if="!page.availableQuantity" class="warning" role="status">This position has no uncommitted stock available for part-out.</p>
        <fieldset :disabled="!connected || stale || changedOutput || sending || !workspace.complete || !controls.enabled || page.hasOpenShortage || !page.availableQuantity">
          <div class="pair"><label>Input units consumed<input v-model="input" inputmode="numeric" maxlength="7" required /></label>
            <label>Confirm input bin code<input v-model="code" maxlength="48" autocomplete="off" spellcheck="false" required /></label></div>
          <small v-if="code && code!==page.position.location.code" class="warning">Enter the code from the physical input bin.</small>
          <p v-if="input && !loading && !selected" class="warning">Enter a whole quantity within the available input stock.</p>
        </fieldset>
        <fieldset :disabled="!editable || !selected">
          <legend>Identified outputs</legend>
          <p>Confirm each actual item, its count and the bin where you placed it. Identify retained material and place it in quarantine.</p>
          <fieldset v-for="(o,index) in outputs" :key="o.id" class="line">
            <legend>Output {{ index+1 }}</legend>
            <label>Output item<select :value="o.catalog" @change="selectCatalog(o,$event)" required><option disabled value="">Choose registered catalog item</option>
              <option v-for="c in workspace.catalog" :key="c.id" :value="c.id">{{ itemLabel([c],c.id) }}{{ c.integrationEligible ? '' : ' · Quarantine only' }}</option></select></label>
            <small v-if="o.catalog" class="narrow-identity">{{ itemLabel(workspace.catalog,o.catalog) }} · Revision {{ o.revision }}</small>
            <small v-if="catalog(o.catalog) && !catalog(o.catalog)?.integrationEligible">Quarantine only · Mapping {{ catalog(o.catalog)?.mapping }} · License {{ catalog(o.catalog)?.license }} · Color {{ catalog(o.catalog)?.colorState }}</small>
            <div class="pair"><label>Output quantity<input v-model="o.quantity" inputmode="numeric" maxlength="7" required /></label>
              <label>Output disposition<select v-model="o.disposition" required><option disabled value="">Choose disposition</option><option value="stock">Accepted stock</option><option value="retained">Retain in quarantine</option></select></label></div>
            <p v-if="o.catalog && o.disposition==='stock' && !catalog(o.catalog)?.integrationEligible" class="warning" role="status">This item cannot enter accepted stock. Choose Retain in quarantine or confirm an eligible item.</p>
            <div class="pair"><label>Output bin<select :value="o.bin" @change="selectBin(o,$event)" required><option disabled value="">Choose registered bin</option>
              <option v-for="l in workspace.locations" :key="l.id" :value="l.id">{{ l.code }} · {{ l.name }}</option></select></label>
              <label>Confirm output bin code<input v-model="o.code" maxlength="48" autocomplete="off" spellcheck="false" required /></label></div>
            <small v-if="o.code && location(o.bin)?.code!==o.code" class="warning">The typed code must match this output bin.</small>
            <div v-if="known" class="pair"><label>Output cost basis<select v-model="o.basis" required><option disabled value="">Choose cost basis</option>
              <option value="known">Assign a known cost</option><option value="unassigned">Leave basis unassigned</option></select></label>
              <label v-if="o.basis==='known'">Assigned cost ({{ page.currency }})<input v-model="o.amount" inputmode="decimal" required /></label></div>
            <small v-else-if="selected">Input cost is unknown. This output retains unknown basis.</small>
            <button v-if="outputs.length>1" type="button" @click="outputs.splice(index,1)">Remove output {{ index+1 }}</button>
          </fieldset>
          <p v-if="repeatsInput" class="warning" role="status">Outputs matching the input item exceed the number of input units consumed. Check their identities and quantities across all bins.</p>
          <button type="button" @click="outputs.push(makeOutput())">Add another output</button>
        </fieldset>
        <fieldset :disabled="!editable || !selected"><legend>Physical remainder or loss</legend>
          <label>Discarded or lost material<select :value="residualMode" @change="changeResidualMode(($event.target as HTMLSelectElement).value as 'none'|'record')" required>
            <option disabled value="">Choose what happened</option><option value="none">None discarded or lost</option><option value="record">Record discarded or lost material</option></select></label>
          <fieldset v-for="(r,index) in physical" :key="r.id" class="line"><legend>Physical residual {{ index+1 }}</legend>
            <label>Material description<input v-model="r.description" maxlength="200" required /></label>
            <div class="pair"><label>Residual quantity<input v-model="r.quantity" inputmode="numeric" maxlength="7" required /></label>
              <label>Material disposition<select v-model="r.disposition" required><option disabled value="">Choose disposition</option><option value="discarded">Discarded</option><option value="lost">Lost</option></select></label></div>
            <button v-if="physical.length>1" type="button" @click="physical.splice(index,1)">Remove residual {{ index+1 }}</button></fieldset>
          <button v-if="residualMode==='record'" type="button" @click="addResidual">Add another residual</button>
        </fieldset>
        <fieldset :disabled="!editable || !selected"><legend>Reconcile cost</legend>
          <template v-if="selected"><dl class="details"><dt>Consumed input cost</dt><dd>{{ money(page.sourceItemCostMinor,page.currency) }}</dd><dt>Assigned to outputs</dt><dd>{{ money(known ? assigned : null,page.currency) }}</dd></dl>
            <label v-if="known">Unallocated residual ({{ page.currency }})<input v-model="residualCost" inputmode="decimal" required /><small>Enter zero when all input cost is assigned.</small></label>
            <small v-if="known && residualCost && !balanced" class="warning">Assigned output costs plus the unallocated residual must equal the consumed input cost.</small>
            <p v-if="!known">The input and unallocated residual have unknown cost.</p></template>
          <p v-else>Enter the input quantity to read its cost.</p>
          <label>Reason for part-out<input v-model="reason" maxlength="200" required /></label>
          <label class="check"><input type="checkbox" v-model="acknowledged" />I confirmed the physical input, each output identity, bin and count, and any discarded or lost material.</label>
        </fieldset>
        <p v-if="feedback" class="warning" role="alert">{{ feedback }}</p>
        <button class="primary" :disabled="!allowed">Record part-out</button>
      </form></div>
      <section class="panel history" aria-label="Input part-out history"><h3 ref="historyHeading" tabindex="-1">Part-outs from this position</h3>
        <p v-if="!page.partOuts.length && live">No part-outs on this page.</p>
        <div v-for="r in page.partOuts" :key="r.id"><a :href="partOutLink(source,r.id)">Part-out receipt {{ r.id }}</a>
          <small>{{ time(r.recordedAt) }} · {{ r.quantity }} consumed · {{ r.outputCount }} output lines · Input cost {{ money(r.sourceItemCostMinor,page.currency) }}</small></div>
        <div v-if="confirmed.length>1 || page.nextAfter" class="pager"><button :disabled="!live || confirmed.length<2" @click="requested=confirmed.slice(0,-1)">Newer part-outs</button>
          <button :disabled="!live || !page.nextAfter" @click="page.nextAfter && (requested=[...confirmed,page.nextAfter])">Older part-outs</button></div>
      </section>
    </template>
  </section>
</template>
<style scoped>
.part-out,.context,.form,.form fieldset,.form label,.history {display:grid;gap:10px;min-width:0}.part-out{gap:14px;overflow-wrap:anywhere}
.part-out :is(h3,p,dl){margin:0}.part-out small{display:block;color:var(--muted)}.part-out a{color:#355340}.context,.form,.history{padding:16px;align-content:start}
.layout{display:grid;grid-template-columns:minmax(210px,.72fr) minmax(0,1.4fr);gap:14px;align-items:start}.form{gap:18px}
.form fieldset{padding:0;border:0}.form fieldset>legend{font-weight:650;font-size:15px;margin-bottom:7px}.form fieldset.line{padding:10px;border:1px solid var(--line);border-radius:6px}
.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.form :is(input,select){min-width:0;width:100%;min-height:34px}
.form :is(input,select){color:var(--text);border:1px solid var(--line);border-radius:5px;padding:5px 7px;background:var(--surface)}
.form button{justify-self:start}.form button.primary{justify-self:end}.form .check{display:flex;align-items:baseline;gap:8px}.form .check input{width:16px;min-height:0;flex:none}.pager{display:flex;gap:8px;flex-wrap:wrap}
.part-out .narrow-identity{display:none}
@media(max-width:780px){.layout{grid-template-columns:minmax(0,1fr)}}@media(max-width:460px){.pair{grid-template-columns:minmax(0,1fr)}.part-out .narrow-identity{display:block}}
</style>
