<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { BinTransferSnapshot, Location, TransferControls, TransferPosition } from './api'
import TransferForm from './TransferForm.vue'
import { itemLabel, money } from './receiving'
import { time } from './presentation'

const props = defineProps<{ location: Location; asOf: string; connected: boolean; locations: Location[]; transfers: TransferControls }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string]; recover: [] }>()
const stock = shallowRef<BinTransferSnapshot | null>(null)
const loading = ref(false)
const error = ref('')
const ready = computed(() => props.connected && !error.value && !loading.value && stock.value?.complete === true)
const selected = shallowRef<TransferPosition>()
const heading = ref<HTMLElement>()
let trigger: HTMLButtonElement | undefined
function openTransfer(position: TransferPosition, event: MouseEvent) {
  if (!ready.value || !props.transfers.enabled) return
  trigger = event.currentTarget as HTMLButtonElement
  selected.value = position
}
async function closeTransfer() {
  selected.value = undefined
  await nextTick()
  if (trigger?.isConnected && !trigger.disabled) trigger.focus()
  else heading.value?.focus()
}
const arrivals = computed(() => new Map(stock.value?.movements.map(move => [move.destinationPlacementId, move])))

watch(() => [props.location.id, props.location.revision, props.asOf, props.connected] as const,
  async ([id, revision, , connected], _, onCleanup) => {
    let current = true
    onCleanup(() => { current = false })
    if (stock.value?.location.id !== id) stock.value = null
    error.value = ''
    loading.value = connected
    if (!connected) return
    try {
      const result = await api.binTransfers(id)
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
    <header class="panel-head"><h3 id="bin-stock-title" ref="heading" tabindex="-1">Stock in this bin</h3></header>
    <p v-if="error || !connected" class="warning" role="status">
      {{ error || 'The workspace is disconnected.' }} {{ stock ? 'Showing the last bin read.' : 'Bin stock is unavailable.' }}
    </p>
    <p v-else-if="loading" role="status">{{ stock ? 'Refreshing bin stock…' : 'Reading bin stock…' }}</p>
    <template v-if="stock">
      <p class="bin-read">Read {{ time(stock.asOf) }}</p>
      <p v-if="!stock.complete" class="warning" role="status">Partial bin read. Quantities cover the returned layers.</p>
      <p v-if="!stock.positions.length && stock.complete" class="bin-read">
        {{ error || !connected || loading ? 'The last complete read contained no stock.' : 'No stock recorded in this bin.' }}
      </p>
      <template v-if="stock.positions.length">
        <dl class="details">
          <dt>On hand</dt><dd>{{ stock.physicalOnHand }}</dd>
          <dt>Backed by offers</dt><dd>{{ stock.backedQuantity }}</dd>
          <dt>Unbacked</dt><dd>{{ stock.unbackedQuantity }}</dd>
          <dt>Available to back</dt><dd>{{ stock.freeToBackQuantity }}</dd>
        </dl>
        <ul class="receipt-lines" aria-label="Receipt layers in this bin">
          <li v-for="({ layer, locationCodeAtPlacement }, index) in stock.positions" :key="layer.placementId">
            <div class="receipt-line-head"><strong>{{ layer.purchaseReference }} · {{ layer.deliveryReference }}</strong><span>{{ layer.quantity }} in bin</span></div>
            <p><strong v-if="layer.catalogRevision !== layer.currentCatalog.revision">Current catalog item · </strong>{{ itemLabel([layer.currentCatalog], layer.catalogId) }}</p>
            <p v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="warning">Placed at catalog revision {{ layer.catalogRevision }}. Item details show current revision {{ layer.currentCatalog.revision }}.</p>
            <p v-if="!layer.currentCatalog.integrationEligible" class="warning">The current catalog item is ineligible for offer backing.</p>
            <small v-if="arrivals.has(layer.placementId)">Transferred from {{ arrivals.get(layer.placementId)?.sourceLocationCode }} · {{ time(arrivals.get(layer.placementId)!.occurredAt) }}</small>
            <small v-if="locationCodeAtPlacement !== stock.location.code && locationCodeAtPlacement !== layer.locationCodeAtPutaway">Bin code at arrival {{ locationCodeAtPlacement }}</small>
            <small v-if="layer.locationCodeAtPutaway !== stock.location.code">Bin code at putaway {{ layer.locationCodeAtPutaway }}</small>
            <small>{{ layer.backedQuantity }} backed · {{ layer.unbackedQuantity }} unbacked · {{ layer.freeToBackQuantity }} available to back</small>
            <small>Whole delivery of {{ layer.receiptQuantity }} · Item cost {{ money(layer.receiptItemCostMinor, layer.currency) }}<template v-if="layer.receiptItemCostMinor === null"> ({{ layer.currency }})</template></small>
            <details><summary>Placement details</summary><small>Placement {{ layer.placementId }}</small>
              <small>Receipt correlation ID {{ layer.receiptCorrelationId }}</small></details>
            <button type="button" class="move-stock" :disabled="!ready || !transfers.enabled || locations.length < 2"
              :aria-label="'Transfer stock from ' + layer.purchaseReference + ' · ' + layer.deliveryReference"
              @click="openTransfer(stock.positions[index], $event)">Transfer stock</button>
          </li>
        </ul>
      </template>
      <section v-if="stock.movements.length" aria-label="Transfer history">
        <header class="panel-head"><h3>Transfer history</h3></header>
        <ul class="receipt-lines" aria-label="Movements involving this bin">
          <li v-for="move in stock.movements" :key="move.id">
            <div class="receipt-line-head"><strong>{{ move.sourceLocationCode }} → {{ move.destinationLocationCode }}</strong><span>{{ move.quantity }} moved</span></div>
            <p>{{ move.purchaseReference }} · {{ move.deliveryReference }}</p>
            <p>{{ move.reason }}</p><small>{{ time(move.occurredAt) }}</small>
            <details><summary>Movement receipt</summary>
              <small>Catalog {{ move.catalogId }} · recorded revision {{ move.catalogRevision }}</small>
              <small v-if="!move.backing.length">No offer backing moved</small>
              <small v-for="offer in move.backing" :key="offer.offerId">{{ offer.quantity }} backed · Offer {{ offer.offerId }}</small>
              <small>Correlation ID {{ move.correlationId }}</small>
            </details>
            <button v-if="locations.some(l => l.id === (location.id === move.sourceLocationId ? move.destinationLocationId : move.sourceLocationId))"
              class="move-stock" @click="emit('inspect', location.id === move.sourceLocationId ? move.destinationLocationId : move.sourceLocationId)">
              Inspect {{ location.id === move.sourceLocationId ? 'destination' : 'source' }} bin
            </button>
          </li>
        </ul>
      </section>
    </template>
    <TransferForm v-if="selected" :source="location" :locations="locations" :original="selected"
      :current="stock?.positions.find(p => p.layer.placementId === selected?.layer.placementId)" :ready="ready" :controls="transfers"
      @close="closeTransfer" @recover="emit('recover')" />
  </section>
</template>
<style scoped>
.bin-stock .panel-head { border-top: 1px solid var(--line); padding-bottom: 0 }
.bin-stock h3 { margin: 0; font-size: 16px }
.bin-read, .bin-stock > [role="status"] { margin: 10px 16px; font-size: 13px }
.bin-read { color: var(--muted) }
.bin-stock .receipt-line-head { flex-wrap: wrap }
.move-stock, details { margin-top: 8px }
details { overflow-wrap: anywhere }
summary { cursor: pointer }
</style>
