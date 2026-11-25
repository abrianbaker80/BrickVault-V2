<script setup lang="ts">
import { ref } from 'vue'
import { api, ApiFailure } from './api'
import type { PositionTimelinePage, TimelineEvent } from './api'
import StockPositionDetails from './StockPositionDetails.vue'
import { closureLink, countReceiptLink, lossReceiptLink, draftBackingLink, quarantineLink } from './countLinks'
import { time } from './presentation'
import { usePositionPage, type PositionReadState } from './usePositionPage'

const props = defineProps<{ id: string; asOf: string; state: PositionReadState }>()
const emit = defineEmits<{ failure: [reason: unknown]; related: [id: string]; inspect: [id: string]; lot: [id: string] }>()
const missing = ref(false)
const { page, confirmed, requested, attempt, loading, partial, error, heading, retry } =
  usePositionPage<PositionTimelinePage>(props, (after, previous) => {
    missing.value = false
    return api.positionTimeline(props.id, after, previous?.complete ? previous.position : undefined)
  }, reason => {
    missing.value = reason instanceof ApiFailure && reason.status === 404 && reason.detail?.code === 'unknown_position'
    emit('failure', reason)
  })

const titles: Record<TimelineEvent['kind'], string> = {
  'putaway': 'Put away', 'transfer-in': 'Transferred in', 'transfer-out': 'Transferred out',
  'count': 'Count witnessed', 'hold': 'Build hold placed', 'hold-release': 'Build hold released',
  'quarantine': 'Quarantined in bin', 'quarantine-release': 'Released after inspection',
  'offer-withdrawal': 'Draft offer backing withdrawn',
  'shortage-closure': 'Shortage closed after exact recount', 'stock-loss': 'Stock loss posted', 'offer-backing': 'Offer backing added',
}
function signed(value: number) { return value > 0 ? '+' + value : String(value) }
</script>
<template>
  <p v-if="missing && !page" role="status" class="balance-read">No stock position was found for this ID.</p>
  <p v-else-if="loading || error || partial || state !== 'ready'" role="status" class="balance-read">
    {{ page?.complete ? 'Showing balances and activity from the last confirmed timeline read.' :
      loading ? 'Reading position balances and activity…' : 'Confirmed position balances are unavailable.' }}
  </p>
  <StockPositionDetails v-if="page?.complete" :record="page.position" :as-of="page.asOf" @inspect="emit('inspect', $event)" @lot="emit('lot', $event)" />
  <section class="timeline" role="region" aria-label="Position timeline">
    <h3 ref="heading" tabindex="-1">Position timeline</h3>
    <p v-if="error || state === 'unavailable'" class="warning" role="status">
      {{ error || 'The position could not be verified.' }} {{ page?.complete ? 'Showing the last confirmed timeline page.' : 'A complete timeline is unavailable.' }}
    </p>
    <p v-else-if="loading || state === 'loading'" role="status">Reading position activity…</p>
    <p v-if="partial" class="warning" role="status">Partial timeline read. {{ page?.complete ? 'Showing the last confirmed timeline page.' : 'A complete position and activity read is required.' }}</p>
    <button ref="retry" v-if="(error || partial) && state === 'ready'" class="retry" @click="attempt++">Retry timeline page {{ requested.length }}</button>
    <template v-if="page?.complete">
      <small class="stamp">Timeline read {{ time(page.asOf) }}</small>
      <p v-if="page.hasUndatedEvents" class="undated">Some events have no recorded time. Events follow the purchase revision, newest first.</p>
      <p v-if="!page.events.length && page.complete && !partial && !loading && !error && state === 'ready'" class="muted">
        {{ confirmed.length > 1 ? 'No older events on this page.' : 'No events recorded for this position.' }}</p>
      <ol v-if="page.events.length" class="events" aria-label="Stock activity, newest revision first">
        <li v-for="event in page.events" :key="event.kind + '/' + event.id">
          <div class="event-head"><strong>{{ titles[event.kind] }}</strong><small>Revision {{ event.purchaseRevision }} · <time v-if="event.occurredAt" :datetime="event.occurredAt">{{ time(event.occurredAt) }}</time><span v-else>Time not recorded</span></small></div>
          <p v-if="event.kind === 'count'">{{ event.quantity }} observed · {{ event.ledgerQuantityAtWitness }} in ledger at count</p>
          <p v-if="event.physicalDelta || event.reservedDelta || event.offerBackedDelta" class="changes">
            <span v-if="event.physicalDelta">{{ signed(event.physicalDelta) }} on hand</span>
            <span v-if="event.reservedDelta">{{ signed(event.reservedDelta) }} reserved for builds</span>
            <span v-if="event.offerBackedDelta">{{ signed(event.offerBackedDelta) }} backed by offers</span>
          </p>
          <p v-if="event.kind === 'transfer-in'">From {{ event.relatedLocationCode }} to {{ event.locationCodeAtEvent }}</p>
          <p v-else-if="event.kind === 'transfer-out'">From {{ event.locationCodeAtEvent }} to {{ event.relatedLocationCode }}</p>
          <p v-else-if="event.locationCodeAtEvent">Recorded bin {{ event.locationCodeAtEvent }}</p>
          <p v-if="event.holdPurpose">Build · {{ event.holdPurpose }}</p>
          <p v-if="event.reason">{{ event.reason }}</p>
          <template v-if="event.kind === 'quarantine' || event.kind === 'quarantine-release'">
            <p>{{ event.quantity }} {{ event.kind === 'quarantine' ? 'made unavailable in this bin' : 'released from quarantine after inspection' }}</p>
            <a :href="quarantineLink(event.locationId, page.positionId, event.id)">View quarantine receipt</a>
          </template>
          <a v-if="event.kind === 'offer-withdrawal'" :href="draftBackingLink(event.locationId,page.positionId,'receipts',event.id)">View withdrawal receipt</a>
          <a v-if="event.kind === 'count' && event.observationId" :href="countReceiptLink(event.locationId, event.observationId)">View count receipt</a>
          <a v-if="event.kind==='shortage-closure' && event.caseId" :href="closureLink(event.locationId,page.positionId,event.caseId,'receipt',event.id)">View shortage closure receipt</a>
          <a v-if="event.kind === 'stock-loss' && event.postingId" :href="lossReceiptLink(event.locationId, page.positionId, event.postingId)">View posted loss receipt</a>
          <a v-if="event.relatedPositionId" :href="'#inventory/' + event.relatedPositionId" @click.exact.prevent="emit('related', event.relatedPositionId)">Inspect {{ event.kind === 'transfer-out' ? 'destination' : 'source' }} position</a>
          <details><summary>Event references</summary><dl>
            <template v-if="event.offerId"><dt>Offer ID</dt><dd>{{ event.offerId }}</dd></template>
            <template v-if="event.holdId"><dt>Build hold ID</dt><dd>{{ event.holdId }}</dd></template>
            <dt>Command ID</dt><dd>{{ event.commandId }}</dd><dt>Correlation ID</dt><dd>{{ event.correlationId }}</dd>
          </dl></details>
        </li>
      </ol>
      <div v-if="page.nextAfter || confirmed.length > 1" class="pages">
        <span aria-live="polite">{{ page.events.length }} shown · Timeline page {{ confirmed.length }}</span>
        <button :disabled="loading || state !== 'ready' || confirmed.length === 1" @click="requested = confirmed.slice(0,-1)">Newer events</button>
        <button :disabled="loading || partial || !!error || state !== 'ready' || !page.complete || !page.nextAfter" @click="page.nextAfter && (requested = [...confirmed, page.nextAfter])">Older events</button>
      </div>
    </template>
  </section>
</template>
<style scoped>
.balance-read { margin: 6px 16px; color: var(--muted); font-size: 13px }
.timeline { padding: 10px 16px 14px; overflow-wrap: anywhere }
.timeline h3, .timeline p { margin: 0; padding: 2px 0 }
.timeline h3 { font-size: 15px }
.timeline p, .stamp, .events, .pages { font-size: 13px }
.timeline .warning { margin-block: 6px }
.stamp { display: block; color: var(--muted); margin: 3px 0 10px }
.events { list-style: none; padding: 0; margin: 0 }
.events li { padding: 10px 0; border-top: 1px solid var(--line) }
.event-head, .changes, .pages { display: flex; flex-wrap: wrap; gap: 3px 12px }
.event-head { display: grid; gap: 2px }
.event-head small, .undated, .events details, .pages span { font-size: 12px; color: var(--muted) }
.events a { color: #355340; display: inline-block; margin-block: 5px; text-underline-offset: 2px }
.events details { margin-top: 4px }
.events summary { cursor: pointer }
.events dl { margin: 5px 0 0 }
.events dd { margin: 0 0 3px; color: var(--text) }
.pages { align-items: center; border-top: 1px solid var(--line); padding-top: 8px }
.pages span { flex-basis: 100% }
</style>
