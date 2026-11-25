<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { StockPage, StockPosition } from './api'
import StockPositionDetails from './StockPositionDetails.vue'
import PositionHistory from './PositionHistory.vue'
import PositionTimeline from './PositionTimeline.vue'

const props = defineProps<{ id: string; asOf: string; connected: boolean; historyView: 'all' | 'transfers' }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string]; related: [id: string]; view: [value: 'all' | 'transfers'] }>()
const record = shallowRef<StockPosition | null>(null)
const recordAsOf = ref('')
const read = shallowRef<StockPage | null>(null)
const rowError = ref('')
const reading = ref(false)
const validId = computed(() => /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(props.id))
const historyState = computed(() => !props.connected || rowError.value ? 'unavailable' :
  reading.value ? 'loading' : read.value?.complete && read.value.rows.length ? 'ready' : 'unavailable')
watch(() => [props.id, props.asOf, props.connected, props.historyView] as const, async ([id, , connected, view], _, onCleanup) => {
  let active = true
  onCleanup(() => { active = false })
  rowError.value = ''
  reading.value = connected && validId.value && view === 'transfers'
  if (!reading.value) return
  try {
    const found = await api.positions({ positionId: id, limit: 1 })
    if (!active) return
    if (found.rows.some(r => r.layer.placementId !== id.toLowerCase())) throw new ContractFailure()
    read.value = found
    const row = found.rows[0]
    if (!row) {
      if (found.complete) { record.value = null; recordAsOf.value = '' }
      return
    }
    record.value = row
    recordAsOf.value = found.asOf
  } catch (reason) {
    if (active) { rowError.value = explain(reason); emit('failure', reason) }
  } finally {
    if (active) reading.value = false
  }
}, { immediate: true })
</script>
<template>
  <div class="position-inspector">
    <p class="position-id"><span>Position ID</span>{{ id }}</p>
    <div v-if="validId" class="history-filter" role="group" aria-label="Position history filter">
      <button :aria-pressed="historyView === 'all'" @click="emit('view', 'all')">All activity</button>
      <button :aria-pressed="historyView === 'transfers'" @click="emit('view', 'transfers')">Transfers only</button>
    </div>
    <p v-if="!validId" class="warning" role="status">This position link has an invalid ID.</p>
    <PositionTimeline v-else-if="historyView === 'all'" :id="id" :as-of="asOf" :state="connected ? 'ready' : 'unavailable'"
      @failure="emit('failure', $event)" @related="emit('related', $event)" @inspect="emit('inspect', $event)" />
    <template v-else>
      <p v-if="rowError || !connected" class="warning" role="status">
        {{ rowError || 'The workspace is disconnected.' }} {{ record ? 'Showing the last position read.' : 'Position details are unavailable.' }}
      </p>
      <p v-else-if="reading" role="status">Reading selected position…</p>
      <p v-else-if="read && !read.complete" class="warning" role="status">Partial position read. {{ !read.rows.length ? 'The selected position was not returned.' : '' }} {{ !read.rows.length && record ? 'Showing the last position read.' : '' }}</p>
      <p v-else-if="read && !record" role="status">No stock position was found for this ID.</p>
      <template v-if="record">
        <StockPositionDetails :record="record" :as-of="recordAsOf" @inspect="emit('inspect', $event)" />
        <PositionHistory :id="id" :as-of="recordAsOf" :state="historyState" @failure="emit('failure', $event)" @inspect="emit('inspect', $event)" />
      </template>
    </template>
  </div>
</template>
<style scoped>
.position-inspector { overflow-wrap: anywhere }
.position-inspector > p { margin: 0; padding: 8px 16px; font-size: 13px }
.position-inspector > .warning { margin: 5px 12px; padding: 8px }
.position-id span { display: block; color: var(--muted) }
.history-filter { display: flex; flex-wrap: wrap; gap: 4px; padding: 6px 16px 10px }
.history-filter [aria-pressed="true"] { background: var(--soft); box-shadow: inset 0 -2px #6f5320; font-weight: 650 }
</style>
