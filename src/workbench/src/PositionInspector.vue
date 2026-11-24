<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { StockPage, StockPosition } from './api'
import PositionHistory from './PositionHistory.vue'
import { money } from './receiving'
import { label, time } from './presentation'

const props = defineProps<{ id: string; asOf: string; connected: boolean }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string] }>()
const record = shallowRef<StockPosition | null>(null)
const recordAsOf = ref('')
const read = shallowRef<StockPage | null>(null)
const rowError = ref('')
const reading = ref(false)
const validId = computed(() => /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(props.id))
const layer = computed(() => record.value?.layer)
const historyState = computed(() => !props.connected || rowError.value ? 'unavailable' :
  reading.value ? 'loading' : read.value?.rows.length ? 'ready' : 'unavailable')
watch(() => [props.id, props.asOf, props.connected] as const, async ([id, , connected], _, onCleanup) => {
  let active = true
  onCleanup(() => { active = false })
  rowError.value = ''
  reading.value = connected && validId.value
  if (!connected || !validId.value) return
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
    <p v-if="!validId" class="warning" role="status">This position link has an invalid ID.</p>
    <p v-else-if="rowError || !connected" class="warning" role="status">
      {{ rowError || 'The workspace is disconnected.' }} {{ record ? 'Showing the last position read.' : 'Position details are unavailable.' }}
    </p>
    <p v-else-if="reading" role="status">Reading selected position…</p>
    <p v-else-if="read && !read.complete" class="warning" role="status">Partial position read. {{ !read.rows.length ? 'The selected position was not returned.' : '' }} {{ !read.rows.length && record ? 'Showing the last position read.' : '' }}</p>
    <p v-else-if="read && !record" role="status">No stock position was found for this ID.</p>
    <template v-if="record && layer">
      <small class="history-time">Position read {{ time(recordAsOf) }}</small>
      <div class="identity"><h3>{{ layer.currentCatalog.name }}</h3><p>{{ label(layer.currentCatalog.itemType) }} · {{ layer.currentCatalog.itemNumber }}{{ layer.currentCatalog.variant ? ' · ' + layer.currentCatalog.variant : '' }}</p>
        <small>{{ layer.currentCatalog.colorState === 'known' ? 'Color ' + layer.currentCatalog.colorId : label(layer.currentCatalog.colorState) + ' color' }} · {{ label(layer.currentCatalog.condition) }} · {{ label(layer.currentCatalog.completeness) }}</small></div>
      <p v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="warning" role="status">Recorded at catalog revision {{ layer.catalogRevision }}. Item details show current revision {{ layer.currentCatalog.revision }}.</p>
      <p v-if="layer.quantity === 0" class="held-note">This position has been fully transferred.</p>
      <h3>Stock position</h3><dl class="details">
        <dt>Bin</dt><dd>{{ record.location.code }} · {{ record.location.name }}</dd>
        <dt>Physical on hand</dt><dd>{{ layer.quantity }}</dd><dt>Backed by offers</dt><dd>{{ layer.backedQuantity }}</dd>
        <dt>Unbacked</dt><dd>{{ layer.unbackedQuantity }}</dd><dt>Available to back</dt><dd>{{ layer.freeToBackQuantity }}</dd>
      </dl>
      <h3>Receipt provenance</h3><dl class="details">
        <dt>Purchase</dt><dd>{{ layer.purchaseReference }}</dd><dt>Delivery</dt><dd>{{ layer.deliveryReference }}</dd>
        <dt>Whole delivery</dt><dd>{{ layer.receiptQuantity }} units</dd><dt>Whole-delivery item cost</dt>
        <dd>{{ money(layer.receiptItemCostMinor, layer.currency) }}<template v-if="layer.receiptItemCostMinor === null"> ({{ layer.currency }})</template></dd>
        <dt>Original bin</dt><dd>{{ layer.locationCodeAtPutaway }}</dd>
        <dt>Recorded catalog revision</dt><dd>{{ layer.catalogRevision }}</dd>
      </dl>
      <details class="ids"><summary>Receipt and catalog IDs</summary><dl class="details">
        <dt>Purchase ID</dt><dd>{{ layer.purchaseId }}</dd><dt>Delivery ID</dt><dd>{{ layer.deliveryId }}</dd>
        <dt>Catalog ID</dt><dd>{{ layer.catalogId }}</dd><dt>Receipt correlation ID</dt><dd>{{ layer.receiptCorrelationId }}</dd>
        <dt>Original placement ID</dt><dd>{{ record.originPlacementId }}
          <button v-if="record.originPlacementId !== layer.placementId" @click="emit('inspect', record.originPlacementId)">Inspect original</button></dd>
      </dl></details>
      <PositionHistory :id="layer.placementId" :as-of="recordAsOf" :state="historyState"
        @failure="emit('failure', $event)" @inspect="emit('inspect', $event)" />
    </template>
  </div>
</template>
<style scoped>
.position-inspector { overflow-wrap: anywhere }
.position-inspector h3, .position-inspector p, .identity, .ids, .history-time { margin: 0; padding: 8px 16px }
.position-inspector h3 { font-size: 15px }
.position-inspector p, .identity small, .history-time, .ids { font-size: 13px }
.position-inspector .warning, .held-note { margin: 5px 12px; padding: 8px }
.position-id span, .history-time { display: block }
.position-id span, .identity small, .history-time { color: var(--muted) }
.identity { padding: 12px 16px; background: var(--soft) }
.identity h3, .identity p { padding: 0 }
.position-inspector .details { padding: 9px 16px 14px }
.ids { border-top: 1px solid var(--line); border-bottom: 1px solid var(--line) }
.ids summary { cursor: pointer; font-weight: 600 }
.ids .details { padding-inline: 0; margin-top: 8px }
.details button { display: block; margin-top: 6px }
</style>
