<script setup lang="ts">
import { api, ContractFailure } from './api'
import { usePositionPage } from './usePositionPage'
import { time } from './presentation'

const props = defineProps<{ id: string; asOf: string; state: 'ready' | 'loading' | 'unavailable' }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string] }>()
const { page: history, confirmed, requested, attempt, loading, partial, error, heading, retry } =
  usePositionPage(props, async after => {
    const page = await api.positionMovements(props.id, after)
    if (page.positionId !== props.id || page.rows.some(m => m.sourcePlacementId !== props.id && m.destinationPlacementId !== props.id))
      throw new ContractFailure()
    return page
  }, reason => emit('failure', reason))
</script>
<template>
  <section class="position-history" role="region" aria-label="Position movement history">
    <h3 ref="heading" tabindex="-1">Movement history</h3>
    <p v-if="error || state === 'unavailable'" class="warning" role="status">
      {{ error || 'Movement history could not be refreshed.' }}
      {{ history ? 'Showing the last movement read.' : 'Movement history is unavailable.' }}
    </p>
    <button ref="retry" v-if="(error || partial) && state === 'ready'" class="retry" @click="attempt++">Retry movement page {{ requested.length }}</button>
    <p v-if="loading || state === 'loading'" role="status">Reading movement history…</p>
    <p v-if="partial" class="warning" role="status">Partial movement history.</p>
    <p v-else-if="history && !history.rows.length && !loading && !error && state === 'ready'" class="muted">
      {{ confirmed.length === 1 ? 'No movements recorded for this position.' : 'No older movements on this page.' }}
    </p>
    <ul v-if="history?.rows.length" class="receipt-lines" aria-label="Position movements">
      <li v-for="m in history.rows" :key="m.id"><strong>{{ m.sourceLocationCode }} → {{ m.destinationLocationCode }}</strong>
        <span>{{ m.quantity }} moved · {{ m.reason }}</span><small>{{ time(m.occurredAt) }}</small>
        <button @click="emit('inspect', m.sourcePlacementId === id ? m.destinationPlacementId : m.sourcePlacementId)">Inspect {{ m.sourcePlacementId === id ? 'destination' : 'source' }} position</button>
      </li>
    </ul>
    <template v-if="history">
      <small class="history-time">Movement read {{ time(history.asOf) }}</small>
      <div v-if="history.nextAfter || confirmed.length > 1" class="history-pages">
        <span class="count" aria-live="polite">{{ history.rows.length }} shown · Movement page {{ confirmed.length }}</span>
        <button :disabled="loading || state !== 'ready' || confirmed.length === 1" @click="requested = confirmed.slice(0, -1)">Newer movements</button>
        <button :disabled="loading || state !== 'ready' || partial || !!error || !history.complete || !history.nextAfter" @click="history.nextAfter && (requested = [...confirmed, history.nextAfter])">Older movements</button>
      </div>
    </template>
  </section>
</template>
<style scoped>
.position-history h3, .position-history p, .history-time { margin: 0; padding: 8px 16px }
.position-history h3 { font-size: 15px }
.position-history p, .history-time { font-size: 13px }
.position-history .warning, .retry { margin: 5px 12px; padding: 8px }
.receipt-lines span, .history-time { display: block }
.history-time, .muted { color: var(--muted) }
.receipt-lines button { display: block; margin-top: 6px }
.history-pages { display: flex; flex-wrap: wrap; gap: 6px; padding: 0 12px 12px }
.history-pages .count { flex-basis: 100% }
</style>
