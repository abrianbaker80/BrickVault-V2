<script setup lang="ts">
import {computed,nextTick,onScopeDispose,ref,shallowRef,watch} from 'vue'
import {api,ContractFailure,explain,type CapacityPage,type CapacityReceipt,type TransferControls} from './api'
import {capacityLabel,capacityLink,limitLabel} from './binCapacity'
import {registryLink} from './binLabels'
import {useBinRegistry} from './useBinRegistry'
import {time} from './presentation'

const props=defineProps<{id:string;eventId?:string;asOf:string;connected:boolean;ready:boolean;controls:TransferControls}>()
const bins=useBinRegistry()
const emit=defineEmits<{failure:[e:unknown]}>()
const page=shallowRef<CapacityPage>(),receipt=shallowRef<CapacityReceipt>()
const requested=shallowRef(['']),confirmed=shallowRef([''])
const busy=ref(false),error=ref(''),retry=ref(0),sending=ref(false),stale=ref(false)
const witness=shallowRef<CapacityPage['bin']>(),mode=ref<''|'set'|'clear'>(''),amount=ref(''),reason=ref(''),feedback=ref('')
const choice=ref<HTMLSelectElement>(),history=ref<HTMLElement>(),editButton=ref<HTMLButtonElement>()
let generation=0
onScopeDispose(()=>{generation++})
async function read(){
  const request=++generation,cursors=requested.value
  busy.value=props.connected;error.value=''
  if(!props.connected)return
  try{
    if(props.eventId){
      const result=await api.capacityReceipt(props.id,{eventId:props.eventId})
      if(request!==generation)return
      if(!result.complete)throw new Error('The piece limit receipt is incomplete.')
      receipt.value=result
    }else{
      const result=await api.capacity(props.id,cursors.at(-1))
      if(request!==generation)return
      if(!result.complete)throw new Error('The piece limit read is incomplete.')
      if(result.nextAfter && cursors.includes(result.nextAfter))throw new ContractFailure()
      if(witness.value && (result.bin.location.revision!==witness.value.location.revision ||
          result.bin.physicalQuantity!==witness.value.physicalQuantity || result.bin.positionCount!==witness.value.positionCount))stale.value=true
      const moved=!!page.value && confirmed.value.at(-1)!==cursors.at(-1)
      page.value=result;confirmed.value=cursors
      if(moved){await nextTick();if(request===generation)history.value?.focus()}
    }
  }catch(e){if(request===generation){error.value=e instanceof Error?e.message:explain(e);emit('failure',e)}}
  finally{if(request===generation)busy.value=false}
}
watch([()=>props.connected,requested,retry],read,{immediate:true})
watch(()=>props.asOf,()=>{if(props.connected && !props.eventId && !busy.value)void read()})
const value=computed(()=>mode.value==='clear' ? null : /^\d+$/.test(amount.value) && Number.isSafeInteger(Number(amount.value)) && Number(amount.value)>0 ? Number(amount.value) : undefined)
const canPrepare=computed(()=>!!page.value && props.connected && !error.value && !props.controls.pending && !sending.value)
const enabled=computed(()=>props.ready && !busy.value && !error.value && props.controls.enabled && !sending.value)
const canSave=computed(()=>!!witness.value && enabled.value && !stale.value && !!mode.value && value.value!==undefined && !!reason.value.trim())
async function edit(){
  witness.value=page.value?.bin;mode.value='';amount.value='';reason.value='';stale.value=false;feedback.value=''
  await nextTick();choice.value?.focus()
}
async function cancel(){witness.value=undefined;await nextTick();editButton.value?.focus()}
async function save(){
  const bin=witness.value,limit=value.value,route=window.location.hash
  if(!canSave.value || !bin || limit===undefined)return
  sending.value=true;feedback.value=''
  try{
    const saved=await props.controls.dispatch({kind:'location-capacity',workspaceId:props.controls.workspaceId,body:{commandId:crypto.randomUUID(),
      expectedRevision:bin.location.revision,locationId:bin.location.id,pieceLimit:limit,reason:reason.value.trim()}})
    if(window.location.hash!==route)return
    if(saved)window.location.hash=capacityLink(props.id,saved.entityId)
    else if(!props.controls.pending){feedback.value=props.controls.feedback;stale.value=true;retry.value++}
  }finally{sending.value=false}
}
</script>
<template>
  <section class="panel bin-capacity" aria-label="Bin piece limit">
    <header class="panel-head"><h2>{{ eventId?'Piece limit receipt':'Bin piece limit' }}</h2><a :href="'#locations/'+id">Inspect bin stock</a></header>
    <div class="body">
      <p v-if="error || !connected" class="warning" role="status">{{ error || 'The workspace is offline.' }} {{ page || receipt ? 'Showing the last confirmed read.' : '' }} <button v-if="connected" :disabled="busy" @click="retry++">Retry capacity read</button></p>
      <p v-else-if="busy" role="status">Reading bin capacity…</p>
      <template v-if="receipt"><h3>{{ limitLabel(receipt.event.pieceLimit) }}</h3>
        <dl class="details"><template v-if="bins.confirmed.value && bins.rows.value.has(id)"><dt>Current bin label</dt><dd>{{ bins.rows.value.get(id)?.location.code }}</dd></template><dt>Previous policy</dt><dd>{{ limitLabel(receipt.event.previousPieceLimit) }}</dd><dt>Stock at change</dt><dd>{{ receipt.event.physicalQuantity }} pieces across {{ receipt.event.positionCount }} positions</dd>
          <dt>Reason</dt><dd>{{ receipt.event.reason }}</dd><dt>Recorded</dt><dd>{{ time(receipt.event.recordedAt) }}</dd><dt>Revision</dt><dd>{{ receipt.event.revision }}</dd>
          <dt>Bin ID</dt><dd>{{ receipt.locationId }}</dd><dt>Receipt ID</dt><dd>{{ receipt.event.id }}</dd><dt>Correlation ID</dt><dd>{{ receipt.event.correlationId }}</dd></dl>
        <a :href="capacityLink(id)">Current bin capacity</a>
      </template>
      <template v-else-if="page"><h3>{{ page.bin.location.code }} · {{ page.bin.location.name }}</h3>
        <p>{{ capacityLabel(page.bin) }}</p>
        <p v-if="page.bin.archived" class="warning">This bin is archived and closed to incoming stock.</p>
        <p v-if="page.bin.capacity?.overCapacityQuantity" class="warning">Stock already here can be counted or moved out. Additional placements need space.</p>
        <a :href="registryLink(id)">Bin labels and archive</a>
        <button v-if="!witness" ref="editButton" :disabled="!canPrepare" @click="edit">Change piece limit</button>
        <form v-else class="decision" @submit.prevent="save"><label>New bin policy<select ref="choice" v-model="mode"><option value="" disabled>Choose a change</option><option value="set">Set a piece limit</option><option value="clear">Clear the piece limit</option></select></label>
          <label v-if="mode==='set'">Piece limit<input v-model="amount" type="text" inputmode="numeric" maxlength="16" required /></label>
          <p v-if="mode==='set' && amount && value===undefined" class="warning">Enter a positive whole-piece limit.</p>
          <p v-if="typeof value==='number' && value<witness.physicalQuantity" class="warning">The {{ witness.physicalQuantity }} pieces already here would be {{ witness.physicalQuantity-value }} above this limit.</p>
          <label>Reason for change<input v-model="reason" required maxlength="200" /></label>
          <p v-if="feedback || stale" role="alert" class="warning">{{ feedback || 'The bin or its occupancy changed. Review the current bin before saving.' }}</p>
          <div class="actions"><button type="button" :disabled="sending" @click="cancel">Cancel</button><button v-if="stale" type="button" :disabled="!enabled" @click="edit">Review updated bin</button>
            <button class="primary" :disabled="!canSave">Save piece limit</button></div>
        </form>
        <section aria-label="Piece limit history"><h3 ref="history" tabindex="-1">Piece limit history</h3>
          <ul v-if="page.events.length"><li v-for="event in page.events" :key="event.id"><a :href="capacityLink(id,event.id)">{{ limitLabel(event.pieceLimit) }} · Revision {{ event.revision }}</a> · {{ time(event.recordedAt) }}</li></ul>
          <p v-else>No recorded piece limit changes on this page.</p>
          <div class="actions" v-if="confirmed.length>1 || page.nextAfter"><button :disabled="busy || !connected || confirmed.length<2" @click="requested=confirmed.slice(0,-1)">Newer changes</button>
            <button :disabled="busy || !connected || !!error || !page.nextAfter" @click="page.nextAfter && (requested=[...confirmed,page.nextAfter])">Older changes</button></div>
        </section>
      </template>
    </div>
  </section>
</template>
<style scoped>
.bin-capacity{max-width:900px;min-width:0;overflow-wrap:anywhere}.bin-capacity a{color:#355340;text-underline-offset:2px}.body,.decision,.decision label{display:grid;gap:8px}.body{padding:16px;gap:16px;justify-items:start}
.body :is(h3,p,dl,ul){margin:0}.body ul{padding-left:20px}.decision{min-width:0;max-width:100%;border:1px solid var(--line);border-radius:6px;padding:12px}
.decision :is(input,select){min-width:0;max-width:100%;padding:6px}.actions{display:flex;flex-wrap:wrap;gap:8px}
</style>
