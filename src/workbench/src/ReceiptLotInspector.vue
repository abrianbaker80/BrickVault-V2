<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, explain } from './api'
import type { ReceiptLot, TimelineEvent } from './api'
import { countReceiptLink, lossReceiptLink, quarantineLink } from './countLinks'
import { time } from './presentation'
import { money } from './receiving'

const props = defineProps<{ id: string; asOf: string; connected: boolean }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string] }>()
const validId = computed(() => /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(props.id))
const page = shallowRef<ReceiptLot | null>(null)
type Cursors = { positions: string[]; events: string[] }
const first = (): Cursors => ({ positions: [''], events: [''] })
const requested = shallowRef<Cursors>(first())
const confirmed = shallowRef<Cursors>(first())
const attempt = ref(0)
const loading = ref(false)
const missing = ref(false)
const partial = ref(false)
const stale = ref(false)
const error = ref('')
const ready = computed(() => props.connected && !loading.value && !error.value && !partial.value)
watch(() => [props.asOf, props.connected, requested.value, attempt.value] as const,
  async ([, connected, target], _, onCleanup) => {
    let current = true
    onCleanup(() => { current = false })
    loading.value = connected && validId.value
    missing.value = false; partial.value = false; stale.value = false; error.value = ''
    if (!loading.value) return
    try {
      const result = await api.receiptLot(props.id, target.positions.at(-1) ?? '', target.events.at(-1) ?? '')
      if (!current) return
      if (!result.complete) { partial.value = true; return }
      page.value = result
      confirmed.value = target
    } catch (reason) {
      if (!current) return
      if (reason instanceof ApiFailure) {
        missing.value = !page.value && reason.status === 404 && reason.detail?.code === 'unknown_receipt_lot'
        stale.value = reason.status === 409 && reason.detail?.code === 'stale_lot_page'
      }
      if (!missing.value) error.value = explain(reason)
      emit('failure', reason)
    } finally { if (current) loading.value = false }
  }, { immediate: true })

function pageBy(list: keyof Cursors, cursor?: string) {
  const stack = confirmed.value[list]
  requested.value = { ...confirmed.value, [list]: cursor ? [...stack, cursor] : stack.slice(0, -1) }
}
const titles: Record<TimelineEvent['kind'], string> = {
  'putaway': 'Put away', 'transfer-in': 'Transfer within lot', 'transfer-out': 'Transfer within lot',
  'count': 'Count witnessed', 'hold': 'Build hold placed', 'hold-release': 'Build hold released',
  'quarantine': 'Quarantined in bin', 'quarantine-release': 'Released after inspection',
  'stock-loss': 'Stock loss posted', 'offer-backing': 'Offer backing added',
}
function signed(value: number) { return value > 0 ? '+' + value : String(value) }
</script>
<template>
  <div class="lot">
    <p v-if="!validId" class="warning" role="status">This receipt lot link has an invalid ID.</p>
    <p v-else-if="missing" role="status">No recorded delivery has this ID.</p>
    <p v-else-if="error || !connected || partial" class="warning" role="status">
      {{ partial ? 'Partial receipt lot read.' : error || 'The workspace is disconnected.' }}
      {{ page ? 'Showing the last confirmed receipt lot read.' : 'Confirmed lot quantities are unavailable.' }}
    </p>
    <p v-else-if="loading" role="status">{{ page ? 'Refreshing receipt lot…' : 'Reading receipt lot…' }}</p>
    <button v-if="stale && connected" @click="requested = first()">Reload lot from first pages</button>
    <button v-else-if="(error || partial) && connected" @click="attempt++">Retry receipt lot read</button>
    <template v-if="page">
      <header class="lot-heading">
        <h3>{{ page.deliveryReference }}</h3>
        <p>{{ page.purchaseReference }} · {{ page.source }}</p>
        <small>Lot read {{ time(page.asOf) }}</small>
      </header>
      <dl class="details">
        <dt>Originally received</dt><dd>{{ page.receivedQuantity }}</dd>
        <dt>Received but unplaced</dt><dd>{{ page.unplacedQuantity }}</dd>
        <dt>In positions</dt><dd>{{ page.positionedQuantity }}</dd>
        <dt>Posted loss</dt><dd>{{ page.lostQuantity }}</dd>
        <dt>Current on hand</dt><dd>{{ page.currentOnHandQuantity }}</dd>
        <template v-if="page.quarantinedQuantity"><dt>Quarantined in bins</dt><dd>{{ page.quarantinedQuantity }}</dd></template>
        <dt>Held for builds</dt><dd>{{ page.heldQuantity }}</dd>
        <dt>Backed by offers</dt><dd>{{ page.offerBackedQuantity }}</dd>
        <dt>Available to back</dt><dd>{{ page.freeToBackQuantity }}</dd>
        <dt>Whole-delivery item cost</dt><dd>{{ money(page.itemCostMinor, page.currency) }}<template v-if="page.itemCostMinor === null"> ({{ page.currency }})</template></dd>
        <template v-if="page.lostQuantity && page.itemCostMinor !== null">
          <dt>Item cost allocated to loss</dt><dd>{{ money(page.allocatedLostItemCostMinor, page.currency) }}</dd>
          <dt>Remaining item basis</dt><dd>{{ money(page.remainingItemCostMinor, page.currency) }}</dd>
        </template>
      </dl>
      <details><summary>Original receipt evidence</summary><dl class="details">
        <dt>Delivery ID</dt><dd>{{ page.deliveryId }}</dd>
        <dt>Purchase ID</dt><dd>{{ page.purchaseId }}</dd>
        <dt>Receipt revision</dt><dd>{{ page.receivedRevision }}</dd>
        <dt>Recorded receipt time</dt><dd>{{ page.receivedAt ? time(page.receivedAt) : 'Time not recorded' }}</dd>
        <dt>Receipt command ID</dt><dd>{{ page.receiptCommandId }}</dd>
        <dt>Receipt correlation ID</dt><dd>{{ page.receiptCorrelationId }}</dd>
        <template v-if="page.receiptCatalogId"><dt>Receipt catalog ID</dt><dd>{{ page.receiptCatalogId }} · Revision {{ page.receiptCatalogRevision }}</dd></template>
      </dl></details>
      <section aria-label="Receipt lot positions">
        <h3>Positions by bin</h3>
        <p v-if="!page.positions.length">No positions on this page.</p>
        <ul class="entries">
          <li v-for="{ layer, location } in page.positions" :key="layer.placementId">
            <strong>{{ location.code }} · {{ location.name }}</strong>
            <p>{{ layer.currentCatalog.name }} · {{ layer.quantity }} positioned · {{ layer.backedQuantity }} backed</p>
            <p v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="warning">
              Recorded at catalog revision {{ layer.catalogRevision }}. Item details show current revision {{ layer.currentCatalog.revision }}.
            </p>
            <a :href="'#inventory/' + layer.placementId" @click.exact.prevent="emit('inspect', layer.placementId)">Inspect position in {{ location.code }}</a>
          </li>
        </ul>
        <div class="paging">
          <span aria-live="polite">{{ page.positions.length }} of {{ page.positionCount }} positions · Page {{ confirmed.positions.length }}</span>
          <button :disabled="!ready || confirmed.positions.length <= 1" @click="pageBy('positions')">Previous positions</button>
          <button :disabled="!ready || !page.nextPositionAfter" @click="pageBy('positions', page.nextPositionAfter ?? undefined)">Next positions</button>
        </div>
      </section>
      <section aria-label="Receipt lot activity">
        <h3>Lot activity</h3>
        <p v-if="page.hasUndatedEvents">Some evidence has no recorded time. Activity follows purchase revision.</p>
        <p v-if="!page.events.length">No activity on this page.</p>
        <ol class="entries">
          <li v-for="{positionId, transferBackedQuantity, event} in page.events" :key="event.kind + '/' + event.id + '/' + positionId">
            <strong>{{ titles[event.kind] }}</strong>
            <p>Revision {{ event.purchaseRevision }} · {{ event.occurredAt ? time(event.occurredAt) : 'Time not recorded' }}</p>
            <template v-if="event.kind === 'transfer-out'">
              <p>{{ event.quantity }} moved from {{ event.locationCodeAtEvent }} to {{ event.relatedLocationCode }} · {{ transferBackedQuantity }} backed</p>
              <a :href="'#inventory/' + positionId" @click.exact.prevent="emit('inspect', positionId)">Inspect source position</a>
              <a v-if="event.relatedPositionId" :href="'#inventory/' + event.relatedPositionId" @click.exact.prevent="emit('inspect', event.relatedPositionId)">Inspect destination position</a>
            </template>
            <template v-else>
              <p v-if="event.physicalDelta">{{ signed(event.physicalDelta) }} positioned</p>
              <p v-if="event.reservedDelta">{{ signed(event.reservedDelta) }} reserved · {{ event.holdPurpose }}</p>
              <p v-if="event.offerBackedDelta">{{ signed(event.offerBackedDelta) }} backed</p>
              <p v-if="event.kind === 'count'">{{ event.quantity }} observed · {{ event.ledgerQuantityAtWitness }} recorded at count</p>
              <p v-if="event.locationCodeAtEvent">Recorded bin {{ event.locationCodeAtEvent }}</p>
              <template v-if="event.kind === 'quarantine' || event.kind === 'quarantine-release'">
            <p>{{ event.quantity }} {{ event.kind === 'quarantine' ? 'made unavailable in this bin' : 'released from quarantine after inspection' }}</p>
            <a :href="quarantineLink(event.locationId, positionId, event.id)">View quarantine receipt</a>
          </template>
          <a v-if="event.kind === 'count' && event.observationId" :href="countReceiptLink(event.locationId, event.observationId)">View count receipt</a>
              <a v-if="event.kind === 'stock-loss' && event.postingId" :href="lossReceiptLink(event.locationId, positionId, event.postingId)">View posted loss receipt</a>
              <a :href="'#inventory/' + positionId" @click.exact.prevent="emit('inspect', positionId)">Inspect event position</a>
            </template>
            <p v-if="event.reason">{{ event.reason }}</p>
            <details><summary>Event references</summary><dl class="details">
              <dt>Command ID</dt><dd>{{ event.commandId }}</dd><dt>Correlation ID</dt><dd>{{ event.correlationId }}</dd>
              <template v-if="event.holdId"><dt>Build hold ID</dt><dd>{{ event.holdId }}</dd></template>
              <template v-if="event.offerId"><dt>Offer ID</dt><dd>{{ event.offerId }}</dd></template>
            </dl></details>
          </li>
        </ol>
        <div class="paging">
          <span aria-live="polite">{{ page.events.length }} events shown · Page {{ confirmed.events.length }}</span>
          <button :disabled="!ready || confirmed.events.length <= 1" @click="pageBy('events')">Newer lot events</button>
          <button :disabled="!ready || !page.nextEventAfter" @click="pageBy('events', page.nextEventAfter ?? undefined)">Older lot events</button>
        </div>
      </section>
    </template>
  </div>
</template>
<style scoped>
.lot { padding: 10px 16px 16px; min-width: 0; overflow-wrap: anywhere; font-size: 13px }
.lot h3, .lot p { margin: 0; padding-block: 3px }
.lot h3 { font-size: 15px }
.lot-heading { margin-block: 4px 10px }
.lot-heading small, .paging span { color: var(--muted) }
.lot section, .lot > details { border-top: 1px solid var(--line); padding-top: 8px; margin-top: 10px }
.entries { list-style: none; padding: 0; margin: 0 }
.entries li { border-top: 1px solid var(--line); padding-block: 8px }
.entries li a { display: inline-block; margin: 4px 10px 4px 0; color: #355340 }
.paging { display: flex; flex-wrap: wrap; gap: 5px 8px; align-items: center }
.paging span { flex-basis: 100% }
.lot summary { cursor: pointer }
</style>
