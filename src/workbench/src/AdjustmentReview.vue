<script setup lang="ts">
import { nextTick, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, explain } from './api'
import { countReceiptLink } from './countLinks'
import { time } from './presentation'
import { money } from './receiving'

const props = defineProps<{ bin: string; position: string; caseId: string; asOf: string; connected: boolean }>()
const emit = defineEmits<{ failure: [reason: unknown] }>()
const expanded = ref(false)
const page = shallowRef<Awaited<ReturnType<typeof api.adjustmentReview>>>()
const displayed = shallowRef<string[]>([''])
const requested = shallowRef<string[]>([''])
const retry = ref(0)
const loading = ref(false)
const error = ref('')
const partial = ref(false)
const stalePage = ref(false)
const offersHeading = ref<HTMLElement>()

watch(() => [expanded.value, props.bin, props.position, props.caseId, props.asOf, props.connected, requested.value, retry.value] as const,
  async ([open, bin, position, caseId, , online, cursors], _, cleanup) => {
    let current = true
    cleanup(() => { current = false })
    loading.value = open && online
    if (!loading.value) return
    error.value = ''
    stalePage.value = false
    try {
      const result = await api.adjustmentReview(bin, position, caseId, cursors.at(-1))
      if (!current) return
      partial.value = !result.complete
      if (partial.value) return
      const moved = displayed.value.length !== cursors.length
      page.value = result
      displayed.value = cursors
      if (moved) { await nextTick(); if (current) offersHeading.value?.focus() }
    } catch (reason) {
      if (!current) return
      stalePage.value = reason instanceof ApiFailure && reason.status === 409
      error.value = explain(reason)
      emit('failure', reason)
    } finally { if (current) loading.value = false }
  })
function retryRead() {
  if (stalePage.value) requested.value = ['']
  ++retry.value
}
</script>
<template>
  <details class="adjustment" @toggle="expanded = ($event.target as HTMLDetailsElement).open">
    <summary>Review stock adjustment</summary>
    <section v-if="expanded" aria-label="Stock adjustment review">
      <p v-if="!connected || error" class="warning" role="status">{{ error || 'Disconnected.' }} {{ page ? 'Showing the last confirmed review.' : 'Review unavailable.' }}</p>
      <p v-else-if="partial" class="warning" role="status">Incomplete adjustment review. {{ page ? 'Showing the last confirmed review.' : 'Quantities are awaiting confirmation.' }}</p>
      <p v-else-if="loading" role="status">Reading the latest count and commitments…</p>
      <button v-if="connected && (partial || error)" :disabled="loading" @click="retryRead">{{ stalePage ? 'Read current review' : 'Retry adjustment review' }}</button>
      <template v-if="page">
        <p><strong>{{ page.receipt.deliveryReference }} · {{ page.location.code }}</strong><small>Review read {{ time(page.asOf) }} · purchase revision {{ page.purchaseRevision }}</small></p>
        <a :href="countReceiptLink(bin, page.latestObservation.id)">Latest witnessed count</a>
        <dl class="details">
          <dt>Current ledger</dt><dd>{{ page.ledgerQuantity }}</dd>
          <dt>Latest observed count</dt><dd>{{ page.latestObservation.countedQuantity }}</dd>
          <dt>Loss under review</dt><dd>{{ page.candidateLossQuantity }}</dd>
          <dt>Remaining after proposed loss</dt><dd>{{ page.postLossLedgerQuantity }}</dd>
          <dt>Reserved for builds</dt><dd>{{ page.protectedProjectQuantity }}</dd>
          <dt>Capacity for offers after loss</dt><dd>{{ page.offerCapacityAfterLoss }}</dd>
          <dt>Currently backing offers</dt><dd>{{ page.offerBackedQuantity }}</dd>
          <dt>Whole delivery cost ({{ page.receipt.deliveryQuantity }} units)</dt>
          <dd>{{ money(page.receipt.itemCostMinor, page.receipt.currency) }}<template v-if="page.receipt.itemCostMinor === null"> ({{ page.receipt.currency }})</template></dd>
        </dl>
        <p v-if="page.protectedProjectShortfall" class="warning">Build reservations exceed the proposed stock by {{ page.protectedProjectShortfall }}. Review the named holds.</p>
        <p v-if="page.minimumOfferBackingReduction" class="warning">At least {{ page.minimumOfferBackingReduction }} units of offer backing need review.</p>
        <p v-if="!page.candidateLossQuantity">The latest count supports no stock loss.</p>
        <h3 ref="offersHeading" tabindex="-1">Unpublished offers ({{ page.offerCount }})</h3>
        <ul v-if="page.offers.length" class="receipt-lines">
          <li v-for="offer in page.offers" :key="offer.id"><strong>{{ offer.backedQuantity }} backed in this bin</strong>
            <p>{{ money(offer.unitPriceMinor, offer.currency) }} per unit</p><small>Offer {{ offer.id }}</small></li>
        </ul>
        <p v-else-if="!page.offerCount">No unpublished offers use this position.</p>
        <nav v-if="displayed.length > 1 || page.nextAfter" aria-label="Adjustment offer pages">
          <button :disabled="!connected || loading || displayed.length < 2 || stalePage" @click="requested = displayed.slice(0,-1)">Previous offers</button>
          <button :disabled="!connected || loading || partial || !!error || !page.nextAfter" @click="page.nextAfter && (requested = [...displayed, page.nextAfter])">More offers</button>
        </nav>
      </template>
    </section>
  </details>
</template>
<style scoped>
.adjustment section { display: grid; gap: 8px; padding: 12px 0 0; overflow-wrap: anywhere }
.adjustment summary, .adjustment button { cursor: pointer }
.adjustment small { display: block; color: var(--muted) }
.adjustment h3, .adjustment p, .adjustment dl { margin: 0 }
.adjustment nav { display: flex; gap: 8px; flex-wrap: wrap }
</style>
