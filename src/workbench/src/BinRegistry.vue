<script setup lang="ts">
import {computed,nextTick,onScopeDispose,ref,shallowRef,watch} from 'vue'
import {api,ContractFailure,explain,type RegistrySnapshot,type RegistryEvidence,type TransferControls} from './api'
import {capacityLink,capacityLabel} from './binCapacity'
import {registryLink,validLabel} from './binLabels'
import {useBinRegistry} from './useBinRegistry'
import {time} from './presentation'
const props=defineProps<{id:string;eventId?:string;asOf:string;connected:boolean;ready:boolean;controls:TransferControls}>()
const emit=defineEmits<{failure:[e:unknown]}>()
const first=()=>({labels:[''],events:['']})
const requested=shallowRef(first()),confirmed=shallowRef(first()),snapshot=shallowRef<RegistrySnapshot>(),receipt=shallowRef<RegistryEvidence>()
const loading=ref(false),error=ref(''),attempt=ref(0),kind=ref<''|'label'|'archive'>('')
const raw=ref(''),reason=ref(''),ack=ref(false),sending=ref(false),issue=ref(''),stale=ref(false)
const frozen=shallowRef<RegistrySnapshot['bin']>()
const bins=useBinRegistry(()=>[kind.value==='archive' ? raw.value : ''])
const labelsHeading=ref<HTMLElement>(),eventsHeading=ref<HTMLElement>(),entry=ref<HTMLInputElement>()
const names:Record<string,string>={registered:'Bin registered','canonical-renamed':'Canonical code changed','location-updated':'Bin updated','label-added':'Alternate label registered',archived:'Bin archived'}
const available=computed(()=>props.ready && !loading.value && !error.value && !stale.value && bins.confirmed.value && props.controls.enabled && !sending.value)
const canSave=computed(()=>available.value && frozen.value && !frozen.value.archived && (kind.value==='label' ? validLabel(raw.value) :
  kind.value==='archive' && bins.matches(frozen.value.location,raw.value) && !!reason.value.trim() && ack.value))
// Register keys this panel by workspace, bin and event. Ignore replies once that scope closes.
let generation=0
onScopeDispose(()=>{generation++})
async function read(){
  const request=++generation,target=requested.value
  loading.value=props.connected;error.value=''
  if(!props.connected)return
  try {
    if(props.eventId){
      const result=await api.registryReceipt(props.id,{eventId:props.eventId})
      if(request!==generation)return
      if(!result.complete)throw new Error('The bin registry receipt was incomplete.')
      receipt.value=result
    } else {
      const result=await api.binRegistry(props.id,target.labels.at(-1),target.events.at(-1))
      if(request!==generation)return
      if(!result.complete)throw new Error('The bin registry detail was incomplete.')
      if((result.nextLabelAfter && target.labels.includes(result.nextLabelAfter))||
         (result.nextEventAfter && target.events.includes(result.nextEventAfter)))throw new ContractFailure()
      const movedLabels=target.labels.at(-1)!==confirmed.value.labels.at(-1),movedEvents=target.events.at(-1)!==confirmed.value.events.at(-1)
      const old=frozen.value,now=result.bin
      if(old && (old.location.revision!==now.location.revision || (kind.value==='archive' &&
        (old.physicalQuantity!==now.physicalQuantity || old.positionCount!==now.positionCount))))stale.value=true
      snapshot.value=result;confirmed.value=target
      if(movedLabels || movedEvents){await nextTick();if(request===generation)(movedLabels?labelsHeading:eventsHeading).value?.focus()}
    }
  } catch(e){if(request===generation){error.value=e instanceof Error ? e.message : explain(e);emit('failure',e)}}
  finally{if(request===generation)loading.value=false}
}
watch([()=>props.connected,requested,attempt],read,{immediate:true})
watch(()=>props.asOf,()=>{
  if(props.connected && !props.eventId && !loading.value)void read()
})
function page(key:'labels'|'events',next?:string|null){
  const stack=confirmed.value[key]
  requested.value={...confirmed.value,[key]:next ? [...stack,next] : stack.slice(0,-1)}
}
async function edit(mode:''|'label'|'archive'){
  kind.value=mode;frozen.value=mode ? snapshot.value?.bin : undefined
  raw.value='';reason.value='';ack.value=false;stale.value=false;issue.value=''
  if(mode){await nextTick();entry.value?.focus()}
}
async function save(){
  const old=frozen.value,route=window.location.hash
  if(!old || !canSave.value)return
  sending.value=true;issue.value=''
  const base={commandId:crypto.randomUUID(),locationId:props.id,expectedRevision:old.location.revision},workspaceId=props.controls.workspaceId
  try{
    const saved=kind.value==='label' ? await props.controls.dispatch({kind:'location-label',workspaceId,body:{...base,label:raw.value}}) :
      await props.controls.dispatch({kind:'location-archive',workspaceId,body:{...base,confirmedLabel:raw.value,reason:reason.value.trim(),expectedPhysicalQuantity:old.physicalQuantity,expectedPositionCount:old.positionCount}})
    if(window.location.hash!==route)return
    if(saved)window.location.hash=registryLink(props.id,saved.entityId)
    else if(!props.controls.pending){issue.value=props.controls.feedback;stale.value=true;attempt.value++}
  }finally{sending.value=false}
}
</script>
<template>
  <section class="panel registry" aria-label="Bin label registry">
    <header class="panel-head"><h2>{{ eventId ? 'Bin registry receipt' : 'Bin labels and archive' }}</h2><a :href="'#locations/'+id">Inspect bin stock</a></header>
    <div class="body"><p v-if="error || !connected" class="warning" role="status">{{ error || 'The workspace is offline.' }} {{ snapshot || receipt ? 'Showing the last confirmed read.' : '' }}
      <button v-if="connected" :disabled="loading" @click="attempt++">Retry registry read</button></p>
    <p v-else-if="loading" role="status">Reading the bin registry…</p>
    <template v-if="receipt">
      <h3>{{ names[receipt.event.kind] }}</h3><p>{{ receipt.event.name }} · Canonical code <strong>{{ receipt.event.canonicalCode }}</strong></p>
      <dl class="details"><template v-if="receipt.event.label"><dt>Recorded physical label</dt><dd>{{ receipt.event.label }}</dd></template>
        <template v-if="receipt.event.previousCode"><dt>Previous canonical code</dt><dd>{{ receipt.event.previousCode }}</dd></template>
        <template v-if="receipt.event.kind==='archived'"><dt>Units at archive</dt><dd>{{ receipt.event.physicalQuantity }}</dd><dt>Occupied positions at archive</dt><dd>{{ receipt.event.positionCount }}</dd><dt>Reason</dt><dd>{{ receipt.event.reason }}</dd></template>
        <dt>Recorded</dt><dd>{{ time(receipt.event.recordedAt) }}</dd><dt>Revision</dt><dd>{{ receipt.event.revision }}</dd><dt>Bin ID</dt><dd>{{ id }}</dd><dt>Receipt ID</dt><dd>{{ receipt.event.id }}</dd></dl>
      <a :href="registryLink(id)">Current bin labels and archive state</a>
    </template>
    <template v-else-if="snapshot">
      <h3>{{ snapshot.bin.location.code }} · {{ snapshot.bin.location.name }}</h3>
      <p><strong>{{ snapshot.bin.archived ? 'Archived' : 'Active' }}</strong> · {{ snapshot.bin.physicalQuantity }} physical units · {{ snapshot.bin.positionCount }} occupied stock positions</p>
      <p>{{ capacityLabel(snapshot.bin) }} · <a :href="capacityLink(id)">Manage piece limit</a></p>
      <p v-if="snapshot.bin.archived">Existing stock can be counted or moved out. This bin is closed to incoming stock. <a v-if="snapshot.bin.archiveEventId" :href="registryLink(id,snapshot.bin.archiveEventId)">Archive receipt</a></p>
      <section aria-label="Registered physical labels"><h3 ref="labelsHeading" tabindex="-1">Registered physical labels</h3><ul><li v-for="l in snapshot.labels" :key="l.label"><strong>{{ l.label }}</strong> · {{ l.isCanonical ? 'Canonical code' : 'Alternate label' }} · Revision {{ l.claimedRevision }}</li></ul>
        <div class="actions" v-if="confirmed.labels.length>1 || snapshot.nextLabelAfter"><button :disabled="loading || !connected || confirmed.labels.length<2" @click="page('labels')">Previous labels</button>
          <button :disabled="!snapshot.nextLabelAfter || loading || !connected || !!error" @click="page('labels',snapshot.nextLabelAfter)">More labels</button></div></section>
      <div v-if="!snapshot.bin.archived && !kind" class="actions"><button :disabled="!available" @click="edit('label')">Register alternate label</button>
        <button :disabled="!available" @click="edit('archive')">Review archive</button></div>
      <form v-if="kind && frozen" class="decision" @submit.prevent="save"><h3>{{ kind==='label' ? 'Register alternate label' : 'Archive this bin' }}</h3>
        <label>{{ kind==='label' ? 'New physical label' : 'Confirm physical bin label' }}<input ref="entry" v-model="raw" maxlength="48" spellcheck="false" autocomplete="off" required /></label>
        <template v-if="kind==='archive'"><p><strong>{{ frozen.physicalQuantity }} units across {{ frozen.positionCount }} occupied positions will stay in this bin.</strong> Archiving closes this bin to incoming stock. Counting and moving existing stock out remain available.</p>
          <label>Reason for archiving<input v-model="reason" maxlength="200" required /></label>
          <label class="check"><input v-model="ack" type="checkbox" />I confirmed the bin label and remaining stock.</label></template>
        <p v-if="issue || stale" class="warning" role="alert">{{ issue || 'The bin or its occupancy changed. Review it again.' }}</p>
        <div class="actions"><button type="button" :disabled="sending" @click="edit('')">Cancel</button><button v-if="stale && !snapshot.bin.archived" type="button" :disabled="loading || !!error || !ready" @click="edit(kind)">Review updated bin</button>
          <button class="primary" :disabled="!canSave">{{ kind==='label' ? 'Save alternate label' : 'Confirm archive' }}</button></div>
      </form>
      <section aria-label="Bin history"><h3 ref="eventsHeading" tabindex="-1">Bin history</h3><ul><li v-for="event in snapshot.events" :key="event.id">
        <a :href="registryLink(id,event.id)">{{ names[event.kind] }} · Revision {{ event.revision }}</a> · {{ time(event.recordedAt) }}</li></ul>
        <div class="actions" v-if="confirmed.events.length>1 || snapshot.nextEventAfter"><button :disabled="loading || !connected || confirmed.events.length<2" @click="page('events')">Newer bin events</button>
          <button :disabled="!snapshot.nextEventAfter || loading || !connected || !!error" @click="page('events',snapshot.nextEventAfter)">Older bin events</button></div></section>
      <small>Bin ID {{ id }} · Revision {{ snapshot.bin.location.revision }} · Read {{ time(snapshot.asOf) }}</small>
    </template></div>
  </section>
</template>
<style scoped>
.registry{max-width:900px;min-width:0;overflow-wrap:anywhere}.registry a{color:#355340}.body,.body section,.decision,.decision label{display:grid;gap:8px}.body{padding:16px;gap:16px}.body :is(h3,p,dl,ul){margin:0}.body ul{padding-left:20px}.actions{display:flex;gap:8px;flex-wrap:wrap}.decision{border:1px solid var(--line);border-radius:6px;padding:12px}.decision input{min-width:0;max-width:100%;padding:6px}.decision .check{display:flex;align-items:center;gap:8px}
</style>
