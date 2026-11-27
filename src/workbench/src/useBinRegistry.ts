import { computed, inject, onScopeDispose, provide, ref, shallowRef, watch, type InjectionKey } from 'vue'
import { api, ApiFailure, ContractFailure, explain, type Location, type RegistryRow, type Workspace } from './api'
import { sameLabel, validLabel } from './binLabels'

function registry(workspaceId:()=>string|undefined,workspace:()=>Workspace|null,active:()=>boolean,online:()=>boolean,onFailure:(e:unknown)=>void) {
  const rows=shallowRef(new Map<string,RegistryRow>())
  const confirmed=ref(false),error=ref(''),attempt=ref(0)
  const aliases=ref(new Map<string,string|null>())
  const requests=new Map<string,Promise<void>>()
  const wanted=computed(()=>active() && online() && workspace()?.complete && !!workspaceId())
  let epoch=0,identity:string|undefined
  let reading:Map<string,number>|undefined
  onScopeDispose(()=>{reading=undefined;epoch++})
  watch([workspaceId,()=>workspace()?.asOf,wanted,attempt],async([id,,enabled])=>{
    if (id!==identity) {identity=id;epoch++;reading=undefined;requests.clear();aliases.value.clear();rows.value=new Map()}
    else for(const [label,owner] of aliases.value) {if(owner===null)aliases.value.delete(label)}
    if (!enabled) {reading=undefined;confirmed.value=false;error.value='';return}
    const locations=workspace()?.locations || []
    const inFlight=reading
    if (inFlight && locations.length===inFlight.size && locations.every(l=>inFlight.get(l.id)===l.revision)) return
    const expected=new Map(locations.map(l=>[l.id,l.revision]))
    reading=expected;confirmed.value=false;error.value=''
    try {
      const result=new Map<string,RegistryRow>(),cursors=new Set<string>()
      let after=''
      do {
        const page=await api.registryPage(after)
        if (reading!==expected) return
        if (!page.complete) throw new Error('The bin registry read is incomplete.')
        for (const row of page.rows) {
          if (result.has(row.location.id)) throw new ContractFailure()
          result.set(row.location.id,row)
        }
        after=page.nextAfter || ''
        if (after && cursors.has(after)) throw new ContractFailure()
        cursors.add(after)
      } while (after)
      const latest=workspace()?.locations || []
      if (latest.length!==result.size || latest.some(l=>result.get(l.id)?.location.revision!==l.revision))
        throw new Error('The location register changed. Refresh the workspace to verify destinations.')
      rows.value=result;confirmed.value=true
    } catch(e) {if(reading===expected){error.value=e instanceof Error ? e.message : explain(e);onFailure(e)}}
    finally {if(reading===expected)reading=undefined}
  },{immediate:true})
  const canonicals=computed(()=>new Map((workspace()?.locations || []).map(l=>[l.code.toLowerCase(),l.id])))
  const destinations=computed(()=>(workspace()?.locations || []).filter(l=>rows.value.get(l.id)?.archived===false))
  function isActive(id:string) {return confirmed.value && rows.value.get(id)?.archived===false}
  function owner(raw:string) {
    if (!validLabel(raw)) return null
    return canonicals.value.get(raw.toLowerCase()) ?? aliases.value.get(raw.toLowerCase())
  }
  function matches(bin:Location|undefined,raw:string) {
    return !!bin && (sameLabel(raw,bin.code) || owner(raw)===bin.id)
  }
  function pending(raw:string) {return validLabel(raw) && owner(raw)===undefined && !error.value}
  async function resolve(raw:string) {
    if (!wanted.value || !pending(raw)) return
    const key=raw.toLowerCase(),generation=epoch
    if(requests.has(key)) return requests.get(key)
    const request=(async()=>{
      try {
        const page=await api.lookupLabel(raw)
        if(generation!==epoch) return
        if(!page.complete) throw new Error('The physical-label lookup was incomplete.')
        aliases.value.set(key,page.bin.location.id)
      } catch(e) {
        if(generation!==epoch) return
        if(e instanceof ApiFailure && e.status===404 && e.detail?.code==='unknown_location_label') aliases.value.set(key,null)
        else {error.value=e instanceof Error ? e.message : explain(e);onFailure(e)}
      } finally {if(epoch===generation) requests.delete(key)}
    })()
    requests.set(key,request)
    return request
  }
  return {rows,confirmed,error,attempt,destinations,isActive,matches,pending,resolve}
}
const key:InjectionKey<ReturnType<typeof registry>>=Symbol('bin registry')
export function provideBinRegistry(...args:Parameters<typeof registry>) {
  const result=registry(...args)
  provide(key,result)
  return result
}
export function useBinRegistry(raw?:()=>string[]) {
  const bins=inject(key)
  if(!bins) throw new Error('The physical bin registry needs a workspace.')
  if(raw) watch([raw,bins.confirmed,bins.attempt],([labels],_,cleanup)=>{
    const timer=window.setTimeout(()=>{for(const label of new Set(labels)) void bins.resolve(label)},120)
    cleanup(()=>window.clearTimeout(timer))
  },{immediate:true})
  return bins
}
