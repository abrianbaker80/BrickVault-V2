<script setup lang="ts">
import { ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { BinStockSnapshot, Location } from './api'
import { itemLabel, money } from './receiving'
import { time } from './presentation'

const props = defineProps<{ location: Location; asOf: string; connected: boolean }>()
const emit = defineEmits<{ failure: [reason: unknown] }>()
const stock = shallowRef<BinStockSnapshot | null>(null)
const loading = ref(false)
const error = ref('')

watch(() => [props.location.id, props.location.revision, props.asOf, props.connected] as const,
  async ([id, revision, , connected], _, onCleanup) => {
    let current = true
    onCleanup(() => { current = false })
    if (stock.value?.location.id !== id) stock.value = null
    error.value = ''
    loading.value = connected
    if (!connected) return
    try {
      const result = await api.binStock(id)
      if (!current) return
      if (result.location.id !== id || result.location.revision < revision) throw new ContractFailure()
      stock.value = result
    } catch (reason) {
      if (!current) return
      error.value = explain(reason)
      emit('failure', reason)
    } finally {
      if (current) loading.value = false
    }
  }, { immediate: true })
</script>

<template>
  <section class="bin-stock" role="region" aria-labelledby="bin-stock-title">
    <header class="panel-head"><h3 id="bin-stock-title">Stock in this bin</h3></header>
    <p v-if="error || !connected" class="warning" role="status">
      {{ error || 'The workspace is disconnected.' }} {{ stock ? 'Showing the last bin read.' : 'Bin stock is unavailable.' }}
    </p>
    <p v-else-if="loading" role="status">{{ stock ? 'Refreshing bin stock…' : 'Reading bin stock…' }}</p>
    <template v-if="stock">
      <p class="bin-read">Read {{ time(stock.asOf) }}</p>
      <p v-if="!stock.complete" class="warning" role="status">Partial bin read. Quantities cover the returned layers.</p>
      <p v-if="!stock.layers.length && stock.complete" class="bin-read">
        {{ error || !connected || loading ? 'The last complete read contained no stock.' : 'No stock recorded in this bin.' }}
      </p>
      <template v-if="stock.layers.length">
        <dl class="details">
          <dt>On hand</dt><dd>{{ stock.physicalOnHand }}</dd>
          <dt>Backed by offers</dt><dd>{{ stock.backedQuantity }}</dd>
          <dt>Unbacked</dt><dd>{{ stock.unbackedQuantity }}</dd>
          <dt>Available to back</dt><dd>{{ stock.freeToBackQuantity }}</dd>
        </dl>
        <ul class="receipt-lines" aria-label="Receipt layers in this bin">
          <li v-for="layer in stock.layers" :key="layer.placementId">
            <div class="receipt-line-head"><strong>{{ layer.purchaseReference }} · {{ layer.deliveryReference }}</strong><span>{{ layer.quantity }} in bin</span></div>
            <p><strong v-if="layer.catalogRevision !== layer.currentCatalog.revision">Current catalog item · </strong>{{ itemLabel([layer.currentCatalog], layer.catalogId) }}</p>
            <p v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="warning">Placed at catalog revision {{ layer.catalogRevision }}. Item details show current revision {{ layer.currentCatalog.revision }}.</p>
            <p v-if="!layer.currentCatalog.integrationEligible" class="warning">The current catalog item is ineligible for offer backing.</p>
            <small v-if="layer.locationCodeAtPutaway !== stock.location.code">Bin code at putaway {{ layer.locationCodeAtPutaway }}</small>
            <small>{{ layer.backedQuantity }} backed · {{ layer.unbackedQuantity }} unbacked · {{ layer.freeToBackQuantity }} available to back</small>
            <small>Whole delivery of {{ layer.receiptQuantity }} · Item cost {{ money(layer.receiptItemCostMinor, layer.currency) }}<template v-if="layer.receiptItemCostMinor === null"> ({{ layer.currency }})</template></small>
          </li>
        </ul>
      </template>
    </template>
  </section>
</template>
<style scoped>
.bin-stock .panel-head { border-top: 1px solid var(--line); padding-bottom: 0 }
.bin-stock h3 { margin: 0; font-size: 16px }
.bin-read, .bin-stock > [role="status"] { margin: 10px 16px; font-size: 13px }
.bin-read { color: var(--muted) }
.bin-stock .receipt-line-head { flex-wrap: wrap }
</style>
