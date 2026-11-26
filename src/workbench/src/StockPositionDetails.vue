<script setup lang="ts">
import { computed } from 'vue'
import type { StockPosition } from './api'
import { partOutLink, countPositionLink, draftBackingLink, quarantineLink } from './countLinks'
import { money } from './receiving'
import { label, time } from './presentation'

const props = defineProps<{ record: StockPosition; asOf: string }>()
const emit = defineEmits<{ inspect: [id: string]; lot: [id: string] }>()
const layer = computed(() => props.record.layer)
</script>
<template>
  <div class="position-details">
    <small class="history-time">Position read {{ time(asOf) }}</small>
    <div class="identity"><h3>{{ layer.currentCatalog.name }}</h3><p>{{ label(layer.currentCatalog.itemType) }} · {{ layer.currentCatalog.itemNumber }}{{ layer.currentCatalog.variant ? ' · ' + layer.currentCatalog.variant : '' }}</p>
      <small>{{ layer.currentCatalog.colorState === 'known' ? 'Color ' + layer.currentCatalog.colorId : label(layer.currentCatalog.colorState) + ' color' }} · {{ label(layer.currentCatalog.condition) }} · {{ label(layer.currentCatalog.completeness) }}</small></div>
    <p v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="warning" role="status">Recorded at catalog revision {{ layer.catalogRevision }}. Item details show current revision {{ layer.currentCatalog.revision }}.</p>
    <p v-if="layer.quantity === 0" class="held-note">No units remain in this position.</p>
    <h3>Stock position</h3><dl class="details">
      <dt>Bin</dt><dd>{{ record.location.code }} · {{ record.location.name }}</dd>
      <dt>Physical on hand</dt><dd>{{ layer.quantity }}</dd><dt>Backed by offers</dt><dd>{{ layer.backedQuantity }}</dd>
      <template v-if="layer.quarantinedQuantity"><dt>Quarantined in bin</dt><dd>{{ layer.quarantinedQuantity }}</dd></template>
      <dt>Unbacked</dt><dd>{{ layer.unbackedQuantity }}</dd><dt>Available to back</dt><dd>{{ layer.freeToBackQuantity }}</dd>
    </dl>
    <p><a :href="partOutLink(layer.placementId)">{{ layer.quantity ? 'Record a manual part-out' : 'Inspect part-out history' }}</a></p>
    <p><a :href="countPositionLink(record.location.id, layer.placementId)">Inspect counts and stock-loss receipts</a></p>
    <p><a :href="quarantineLink(record.location.id,layer.placementId)">Inspect position quarantine</a></p>
    <p><a :href="draftBackingLink(record.location.id,layer.placementId)">Review draft offer backing at this position</a></p>
    <h3>Receipt provenance</h3><dl class="details">
      <dt>Purchase</dt><dd>{{ layer.purchaseReference }}</dd><dt>Delivery</dt><dd>{{ layer.deliveryReference }}</dd>
      <dt>Whole delivery</dt><dd>{{ layer.receiptQuantity }} units</dd><dt>Whole-delivery item cost</dt>
      <dd>{{ money(layer.receiptItemCostMinor, layer.currency) }}<template v-if="layer.receiptItemCostMinor === null"> ({{ layer.currency }})</template></dd>
      <dt>Original bin</dt><dd>{{ layer.locationCodeAtPutaway }}</dd>
      <dt>Recorded catalog revision</dt><dd>{{ layer.catalogRevision }}</dd>
    </dl>
    <p><a :href="'#inventory/lot/' + layer.deliveryId" @click.exact.prevent="emit('lot', layer.deliveryId)">Inspect receipt lot</a></p>
    <details class="ids"><summary>Receipt and catalog IDs</summary><dl class="details">
      <dt>Purchase ID</dt><dd>{{ layer.purchaseId }}</dd><dt>Delivery ID</dt><dd>{{ layer.deliveryId }}</dd>
      <dt>Catalog ID</dt><dd>{{ layer.catalogId }}</dd><dt>Receipt correlation ID</dt><dd>{{ layer.receiptCorrelationId }}</dd>
      <dt>Original placement ID</dt><dd>{{ record.originPlacementId }}
        <button v-if="record.originPlacementId !== layer.placementId" @click="emit('inspect', record.originPlacementId)">Inspect original</button></dd>
    </dl></details>
  </div>
</template>
<style scoped>
.position-details { overflow-wrap: anywhere }
.position-details h3, .position-details p, .identity, .ids, .history-time { margin: 0; padding: 8px 16px }
.position-details h3 { font-size: 15px }
.position-details p, .identity small, .history-time, .ids { font-size: 13px }
.position-details .warning, .held-note { margin: 5px 12px; padding: 8px }
.history-time { display: block }
.identity small, .history-time { color: var(--muted) }
.identity { padding: 12px 16px; background: var(--soft) }
.identity h3, .identity p { padding: 0 }
.position-details .details { padding: 9px 16px 14px }
.ids { border-top: 1px solid var(--line); border-bottom: 1px solid var(--line) }
.ids summary { cursor: pointer; font-weight: 600 }
.ids .details { padding-inline: 0; margin-top: 8px }
.details button { display: block; margin-top: 6px }
</style>
