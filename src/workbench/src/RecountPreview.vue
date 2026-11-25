<script setup lang="ts">
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { RecountPreviewPage, RecountPreviewRequest, StockQuery, TransferControls } from './api'
import RecountTarget from './RecountTarget.vue'

const props = defineProps<{ selected: string[]; filter: StockQuery; csrf: string; controls: TransferControls; connected: boolean }>()
const emit = defineEmits<{ close: []; failure: [reason: unknown] }>()
const dialog = ref<HTMLDialogElement>()
const heading = ref<HTMLElement>()
const retry = ref<HTMLButtonElement>()
const mode = ref<'selected' | 'matching'>(props.selected.length ? 'selected' : 'matching')
const hasFilters = Boolean(props.filter.q || props.filter.itemType || props.filter.locationId)
function scope(): RecountPreviewRequest {
  const matched = mode.value === 'matching'
  return {previewId:crypto.randomUUID(), scope:mode.value, positionIds:matched ? [] : [...props.selected],
    q:matched ? props.filter.q || null : null, itemType:matched ? props.filter.itemType || null : null,
    locationId:matched ? props.filter.locationId || null : null}
}
const input = shallowRef(scope())
const page = shallowRef<RecountPreviewPage>()
const confirmed = shallowRef<string[]>([''])
const requested = shallowRef<string[]>([''])
const attempt = ref(0)
const error = ref('')
const busy = ref(false)
const submitting = ref(false)
const feedback = ref('')
const partial = ref(false)
const writable = computed(() => props.controls.enabled && props.connected && !busy.value && !error.value &&
  !partial.value && !submitting.value && !!page.value?.complete && page.value.total > 0)
watch(mode, () => {
  input.value = scope()
  confirmed.value = requested.value = ['']
  page.value = undefined
  feedback.value = ''
})
watch(() => [input.value, requested.value, attempt.value, props.connected] as const, async ([body,cursors,,online],_,cleanup) => {
  let active = true
  cleanup(() => { active=false })
  busy.value=online
  error.value=''
  if (!online) return
  try {
    const data = page.value?.previewId === body.previewId
      ? await api.recountPreview(body,cursors.at(-1)!) : await api.freezeRecount(body,props.controls.workspaceId,props.csrf)
    if (!active) return
    if (data.nextAfter && cursors.includes(data.nextAfter)) throw new ContractFailure()
    partial.value=!data.complete
    if (!data.complete) return
    const moved = cursors.at(-1) !== confirmed.value.at(-1)
    page.value=data
    confirmed.value=cursors
    if (moved) { await nextTick(); if (active) heading.value?.focus() }
  } catch (reason) {
    if (!active) return
    error.value=explain(reason)
    emit('failure',reason)
  } finally {
    if (active) {
      busy.value=false
      if (error.value || partial.value) {
        await nextTick()
        if (active) retry.value?.focus()
      }
    }
  }
}, { immediate:true })
onMounted(() => dialog.value?.showModal())
async function confirm() {
  if (!writable.value || !page.value) return
  submitting.value=true
  try {
    const saved = await props.controls.dispatch({kind:'recount',workspaceId:props.controls.workspaceId,
      body:{commandId:crypto.randomUUID(),previewId:page.value.previewId}})
    await nextTick()
    if (saved) {
      window.location.hash='inventory/recounts/'+saved.entityId
      emit('close')
    } else if (props.controls.pending) emit('close')
    else feedback.value=props.controls.feedback
  } finally { submitting.value=false }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="preview-title" @cancel.prevent="!submitting && emit('close')">
    <header class="panel-head"><h2 id="preview-title">Preview recount</h2><button class="close" aria-label="Close recount preview" :disabled="submitting" @click="emit('close')">×</button></header>
    <div class="form-body">
      <label>Action scope<select v-model="mode" :disabled="submitting"><option value="selected" :disabled="!selected.length">Selected positions</option>
        <option value="matching">{{ hasFilters ? 'All matching positions' : 'Entire active inventory' }}</option></select></label>
      <p v-if="mode === 'matching' && hasFilters">{{ [filter.q,filter.itemType,filter.locationId ? 'Selected bin' : ''].filter(Boolean).join(' · ') }} · Includes every matching page.</p>
      <p v-if="error || partial || !connected" role="status" class="warning">{{ error || (partial ? 'Partial preview.' : 'The workspace is disconnected.') }}
        A complete preview is required to queue a recount.</p>
      <p v-if="busy" role="status">Reading frozen positions…</p>
      <button v-if="connected && (error || partial)" ref="retry" :disabled="busy" @click="attempt++">Retry preview read</button>
      <template v-if="page">
        <h3 ref="heading" tabindex="-1">{{ page.total }} {{ page.total === 1 ? 'frozen position' : 'frozen positions' }}</h3>
        <p>Confirmation queues a witnessed count for each available, unchanged position. Stock quantities stay the same.</p>
        <ul class="receipt-lines" aria-label="Frozen targets" tabindex="0"><li v-for="target in page.rows" :key="target.positionId"><RecountTarget :target="target" /></li></ul>
        <div v-if="confirmed.length>1 || page.nextAfter" class="pages">
          <span>Preview page {{ confirmed.length }}</span>
          <button :disabled="busy || !connected || confirmed.length===1" @click="requested=confirmed.slice(0,-1)">Previous targets</button>
          <button :disabled="busy || !connected || partial || !!error || !page.nextAfter" @click="page.nextAfter && (requested=[...confirmed,page.nextAfter])">Next targets</button>
        </div>
      </template>
      <p v-if="feedback" role="alert" class="warning">{{ feedback }}</p>
      <div class="actions"><button :disabled="submitting" @click="emit('close')">Cancel</button><button class="primary" :disabled="!writable" @click="confirm">Queue recount</button></div>
    </div>
  </dialog>
</template>
<style scoped>
dialog { max-width: min(550px, calc(100% - 24px)) }
.form-body h3, .form-body p { margin: 4px 0 }
.receipt-lines { max-height: 40vh; overflow: auto; padding: 0 }
.pages { display: flex; flex-wrap: wrap; align-items: center; gap: 6px }
.pages span { font-size: 13px; margin-right: auto }
</style>
