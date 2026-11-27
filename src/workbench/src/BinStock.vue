<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch, watchEffect } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { BinTransferSnapshot, Catalog, Location, TransferControls, TransferPosition } from './api'
import TransferForm from './TransferForm.vue'
import CountWorkspace from './CountWorkspace.vue'
import { countPositionLink, holdPositionLink, draftBackingLink, quarantineLink, type CountLink } from './countLinks'
import { itemLabel, money } from './receiving'
import { time } from './presentation'

const props = defineProps<{ location: Location; asOf: string; connected: boolean; workspaceLoading: boolean; locations: Location[]; catalog: Catalog[]; transfers: TransferControls; countLink?: CountLink }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string]; recover: [] }>()
const stock = shallowRef<BinTransferSnapshot | null>(null)
const loading = ref(false)
const error = ref('')
const shortageRead = ref<'loading' | 'current' | 'partial' | 'failed'>('loading')
const retryShortages = ref(0)
const readingShortages = ref(false)
const confirmedBin = computed(() => props.connected && !error.value && stock.value?.complete === true && shortageRead.value === 'current')
const ready = computed(() => confirmedBin.value && !loading.value && !readingShortages.value)
const inspectingCount = computed(() => props.countLink?.binId === props.location.id && Boolean(props.countLink.positionId || props.countLink.receiptId))
const selected = shallowRef<TransferPosition>()
const selectedReserved = ref(0)
const heading = ref<HTMLElement>()
const fenced = ref(new Set<string>())
function closeCount() {
  window.location.hash = 'locations/' + props.location.id
}
watch(() => props.countLink, async (link, previous) => {
  if (link?.binId === props.location.id && !link.positionId && !link.receiptId && !link.holdPositionId && !link.draftPositionId && !link.quarantinePositionId && !link.recovery && !link.closure && (previous?.recovery || previous?.closure || previous?.positionId || previous?.receiptId || previous?.holdPositionId || previous?.draftPositionId || previous?.quarantinePositionId)) {
    await nextTick()
    heading.value?.focus()
  }
})
function recoverCount() {
  window.location.hash = 'locations'
  emit('recover')
}
let trigger: HTMLButtonElement | undefined
type TransferCheck = {
  workspaceId: string; bin: Location; position: TransferPosition
  holds?: Awaited<ReturnType<typeof api.positionHolds>>
}
const opening = shallowRef<TransferCheck>()
const checking = computed(() => Boolean(opening.value))
const transferError = ref('')
const changedSource = 'Refresh this bin to verify the current position before transferring stock.'
onBeforeUnmount(() => { opening.value = undefined })
watch(() => props.countLink, () => { opening.value = undefined })

function sameSource(original: TransferPosition, current: TransferPosition) {
  const before = original.layer, now = current.layer
  if (original.purchaseRevision !== current.purchaseRevision || before.purchaseId !== now.purchaseId ||
      before.deliveryId !== now.deliveryId || before.catalogId !== now.catalogId || before.catalogRevision !== now.catalogRevision ||
      before.currentCatalog.id !== now.currentCatalog.id || now.currentCatalog.id !== now.catalogId ||
      before.currentCatalog.revision !== now.currentCatalog.revision || before.quantity !== now.quantity ||
      before.unbackedQuantity !== now.unbackedQuantity || now.unbackedQuantity !== now.quantity - now.backedQuantity ||
      before.backedQuantity !== now.backedQuantity || before.quarantinedQuantity !== now.quarantinedQuantity ||
      original.backing.length !== current.backing.length) return false
  const backing = new Map(current.backing.map(row => [row.offerId, row]))
  return original.backing.every(row => {
    const offer = backing.get(row.offerId)
    return offer?.quantity === row.quantity && offer.currency === row.currency && offer.unitPriceMinor === row.unitPriceMinor
  })
}

watchEffect(() => {
  const request = opening.value
  if (!request) return
  const {position, bin, holds} = request
  if (!props.connected || props.transfers.pending || props.transfers.workspaceId !== request.workspaceId ||
      props.location.id !== bin.id || props.location.revision !== bin.revision ||
      (!props.workspaceLoading && !props.transfers.enabled)) {
    opening.value = undefined
    return
  }
  if (error.value || (!loading.value && !stock.value?.complete) ||
      (!readingShortages.value && (shortageRead.value === 'partial' || shortageRead.value === 'failed'))) {
    transferError.value = changedSource
    opening.value = undefined
    return
  }
  if (props.workspaceLoading || loading.value || readingShortages.value) return
  const layer = position.layer
  const current = stock.value?.positions.find(row => row.layer.placementId === layer.placementId)
  // A newer scoped shortage blocks movement immediately, even when the bin read predates the count.
  if (ready.value && current && holds?.complete && holds.shortageId &&
      holds.purchaseId === layer.purchaseId && current.layer.purchaseId === layer.purchaseId &&
      holds.purchaseRevision >= position.purchaseRevision && holds.purchaseRevision >= current.purchaseRevision) {
    fenced.value.add(layer.placementId)
    opening.value = undefined
    return
  }
  if (!ready.value || !current || stock.value?.location.revision !== bin.revision || stock.value.location.code !== bin.code || !sameSource(position, current)) {
    transferError.value = changedSource
    opening.value = undefined
    return
  }
  if (fenced.value.has(layer.placementId)) { opening.value = undefined; return }
  if (!holds) return
  opening.value = undefined
  if (!holds.complete || holds.location.revision !== bin.revision || holds.location.code !== bin.code || holds.purchaseId !== layer.purchaseId ||
      holds.purchaseRevision !== position.purchaseRevision || holds.catalogRevision !== layer.catalogRevision ||
      holds.currentCatalog.id !== layer.catalogId || holds.currentCatalog.revision !== layer.currentCatalog.revision ||
      holds.ledgerQuantity !== layer.quantity || holds.offerBackedQuantity !== layer.backedQuantity ||
      holds.quarantinedQuantity === undefined || holds.quarantinedQuantity !== layer.quarantinedQuantity ||
      holds.freeQuantity !== holds.ledgerQuantity - holds.reservedQuantity - holds.quarantinedQuantity ||
      holds.freeQuantity < holds.offerBackedQuantity) {
    transferError.value = changedSource
    return
  }
  selectedReserved.value = holds.reservedQuantity
  selected.value = current
}, {flush: 'post'})
async function openTransfer(position: TransferPosition, event: MouseEvent) {
  if (!ready.value || !props.transfers.enabled || checking.value) return
  trigger = event.currentTarget as HTMLButtonElement
  const request: TransferCheck = {workspaceId: props.transfers.workspaceId, bin: props.location, position}
  opening.value = request
  transferError.value = ''
  try {
    const holds = await api.positionHolds(request.bin.id, position.layer.placementId)
    if (opening.value === request) opening.value = {...request, holds}
  } catch (failure) {
    if (opening.value !== request) return
    opening.value = undefined
    transferError.value = explain(failure)
    emit('failure', failure)
  }
}
async function closeTransfer() {
  selected.value = undefined
  await nextTick()
  if (trigger?.isConnected && !trigger.disabled) trigger.focus()
  else heading.value?.focus()
}
const arrivals = computed(() => new Map(stock.value?.movements.map(move => [move.destinationPlacementId, move])))

const binScope = () => [props.location.id, props.location.revision, props.asOf, props.connected,
  inspectingCount.value || Boolean(props.countLink?.recovery || props.countLink?.closure || props.countLink?.holdPositionId || props.countLink?.lossPositionId || props.countLink?.quarantinePositionId || props.countLink?.draftPositionId)] as const

watch([binScope, retryShortages], async ([[id, revision, , connected, inspecting]], _, onCleanup) => {
  let current = true
  onCleanup(() => { current = false })
  readingShortages.value = connected && !inspecting
  if (!readingShortages.value) {
    shortageRead.value = 'loading'
    return
  }
  try {
    // Open cases cover the whole bin independently of the observation history page.
    const result = await api.binCounts(id, { limit: '1' })
    if (!current) return
    if (result.location.revision < revision) throw new ContractFailure()
    if (!result.complete) {
      shortageRead.value = 'partial'
      return
    }
    fenced.value = new Set(result.shortages.map(shortage => shortage.positionId))
    shortageRead.value = 'current'
  } catch (failure) {
    if (!current) return
    shortageRead.value = 'failed'
    emit('failure', failure)
  } finally { if (current) readingShortages.value = false }
}, { immediate: true })

watch(binScope,
  async ([id, revision, , connected, countOnly], _, onCleanup) => {
    let current = true
    onCleanup(() => { current = false })
    if (stock.value?.location.id !== id) stock.value = null
    error.value = ''
    loading.value = connected && !countOnly
    if (!loading.value) return
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
    <p v-if="connected && (shortageRead === 'partial' || shortageRead === 'failed')" class="warning" role="status">
      {{ shortageRead === 'partial' ? 'Partial shortage read.' : 'Open shortages could not be verified.' }} Transfers require a complete shortage read.
      <button type="button" @click="retryShortages++">Retry shortage read</button>
    </p>
    <p v-if="checking" role="status">Verifying the transfer source… <button type="button" @click="opening = undefined">Cancel transfer preparation</button></p>
    <p v-if="transferError" class="warning" role="alert">{{ transferError }}</p>
    <template v-if="stock && !inspectingCount">
      <p class="bin-read">Read {{ time(stock.asOf) }}</p>
      <p v-if="!stock.complete" class="warning" role="status">Partial bin read. Quantities cover the returned layers.</p>
      <p v-if="!stock.positions.length && stock.complete" class="bin-read">
        {{ error || !connected || loading ? 'The last complete read contained no stock.' : 'No stock recorded in this bin.' }}
      </p>
      <template v-if="stock.positions.length">
        <dl class="details">
          <dt>On hand</dt><dd>{{ stock.physicalOnHand }}</dd>
          <dt>Backed by offers</dt><dd>{{ stock.backedQuantity }}</dd>
          <template v-if="stock.positions.some(p => p.layer.quarantinedQuantity)"><dt>Quarantined in bin</dt><dd>{{ stock.positions.reduce((sum,p) => sum+(p.layer.quarantinedQuantity ?? 0),0) }}</dd></template>
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
            <small v-if="layer.quarantinedQuantity">{{ layer.quarantinedQuantity }} quarantined in this bin</small>
            <small>Whole delivery of {{ layer.receiptQuantity }} · Item cost {{ money(layer.receiptItemCostMinor, layer.currency) }}<template v-if="layer.receiptItemCostMinor === null"> ({{ layer.currency }})</template></small>
            <details><summary>Placement details</summary><small>Placement {{ layer.placementId }}</small>
              <small>Receipt correlation ID {{ layer.receiptCorrelationId }}</small></details>
            <button type="button" class="move-stock" :disabled="!ready || !transfers.enabled || locations.length < 2 || checking || fenced.has(layer.placementId)"
              :aria-label="'Transfer stock from ' + layer.purchaseReference + ' · ' + layer.deliveryReference"
              @click="openTransfer(stock.positions[index], $event)">Transfer stock</button>
            <a class="count-link" :href="countPositionLink(location.id, layer.placementId)" :aria-label="'Count stock for ' + layer.purchaseReference + ' · ' + layer.deliveryReference">Count stock</a>
            <a class="count-link" :href="holdPositionLink(location.id, layer.placementId)" :aria-label="'Build holds for ' + layer.purchaseReference + ' · ' + layer.deliveryReference + ' · Position ID ' + layer.placementId">Build holds</a>
            <a class="count-link" :href="quarantineLink(location.id,layer.placementId)" :aria-label="'Quarantine for ' + layer.purchaseReference + ' · ' + layer.deliveryReference + ' · Position ID ' + layer.placementId">Quarantine</a>
            <a class="count-link" :href="draftBackingLink(location.id,layer.placementId)" :aria-label="'Draft offer backing for ' + layer.purchaseReference + ' · ' + layer.deliveryReference + ' · Position ' + layer.placementId">Draft offer backing</a>
            <small v-if="fenced.has(layer.placementId)" class="warning">Open shortage · transfers and new offer backing are blocked.</small>
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
    <CountWorkspace v-if="inspectingCount && countLink"
      :key="countLink.positionId || countLink.receiptId" :location="location" :position-id="countLink.positionId" :receipt-id="countLink.receiptId"
      :as-of="asOf" :connected="connected" :controls="transfers" :catalog="catalog"
      @failure="emit('failure', $event)" @fenced="fenced.add($event)" @close="closeCount" @recover="recoverCount" />
    <TransferForm v-if="selected" :source="location" :reserved="selectedReserved" :locations="locations" :original="selected"
      :current="stock?.positions.find(p => p.layer.placementId === selected?.layer.placementId)" :ready="ready && !fenced.has(selected.layer.placementId)" :confirmed-bin="confirmedBin && !fenced.has(selected.layer.placementId)" :controls="transfers"
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
.count-link { display: inline-block; margin: 8px 0 0 10px; color: #355340; text-underline-offset: 2px }
details { overflow-wrap: anywhere }
summary { cursor: pointer }
</style>
