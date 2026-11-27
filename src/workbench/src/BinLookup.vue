<script setup lang="ts">
import { onBeforeUnmount,ref,shallowRef,watch } from 'vue'
import { api,explain } from './api'
import { capacityLabel } from './binCapacity'
import { registryLink,validLabel } from './binLabels'
const props=defineProps<{ready:boolean;asOf:string}>()
const emit=defineEmits<{failure:[reason:unknown]}>()
const raw=ref(''),busy=ref(false),error=ref('')
const result=shallowRef<Awaited<ReturnType<typeof api.lookupLabel>>>()
let revision=0,queued=false
const requested=ref('')
watch(raw,()=>{revision++;queued=false;requested.value='';busy.value=false;result.value=undefined;error.value=''},{flush:'sync'})
watch([()=>props.ready,()=>props.asOf],()=>{
  if(!props.ready || !requested.value || requested.value!==raw.value)return
  if(busy.value)queued=true
  else void find()
})
onBeforeUnmount(()=>{revision++;queued=false})
async function find() {
  if(!validLabel(raw.value) || !props.ready || busy.value) return
  const request=++revision
  busy.value=true;error.value='';requested.value=raw.value
  try {
    const match=await api.lookupLabel(raw.value)
    if(request!==revision) return
    if(!match.complete){error.value='The label lookup was incomplete. Scan again.';return}
    result.value=match
  } catch(e) {if(request===revision){error.value=explain(e);emit('failure',e)}}
  finally {if(request===revision){busy.value=false;if(queued){queued=false;void find()}}}
}
</script>
<template>
  <form class="scan" aria-label="Physical label lookup" @submit.prevent="find">
    <label>Scan or type physical label<input v-model="raw" maxlength="48" spellcheck="false" autocomplete="off" /></label>
    <button :disabled="!ready || busy || !validLabel(raw)">Find bin</button>
    <p v-if="busy || error" role="status">{{ error || 'Looking up the label…' }}</p>
    <p v-if="result" role="status"><span v-if="error || !ready || busy">Last confirmed match · </span>Matched {{ result.isCanonical ? 'canonical code' : 'alternate label' }} <strong>{{ result.matchedLabel }}</strong>
      · <a :href="registryLink(result.bin.location.id)">{{ result.bin.location.code }} · {{ result.bin.location.name }}</a> · {{ result.bin.archived ? 'Archived' : 'Active' }} · {{ capacityLabel(result.bin) }}</p>
  </form>
</template>
<style scoped>
.scan{display:flex;align-items:end;flex-wrap:wrap;gap:8px 12px;padding:0 16px 14px}.scan label{display:grid;gap:4px}.scan input{min-width:0;max-width:100%;padding:6px}.scan p{flex-basis:100%;margin:0;overflow-wrap:anywhere}.scan a{color:#355340}
</style>
