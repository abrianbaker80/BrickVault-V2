<script setup lang="ts">
import { nextTick, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain } from './api'
import type { PositionMovementPage } from './api'
import { time } from './presentation'

const props = defineProps<{ id: string; asOf: string; state: 'ready' | 'loading' | 'unavailable' }>()
const emit = defineEmits<{ failure: [reason: unknown]; inspect: [id: string] }>()
const history = shallowRef<PositionMovementPage | null>(null)
const confirmed = shallowRef<string[]>([''])
const requested = shallowRef<string[]>([''])
const attempt = ref(0)
const loading = ref(false)
const error = ref('')
const heading = ref<HTMLElement>()
const retry = ref<HTMLButtonElement>()

watch(() => [props.id, props.asOf, props.state, requested.value, attempt.value] as const,
  async ([id, , state, cursors], _, onCleanup) => {
    let active = true
    onCleanup(() => { active = false })
    error.value = ''
    loading.value = state === 'ready'
    if (!loading.value) return
    const pageChanged = history.value && (cursors.length !== confirmed.value.length ||
      cursors[cursors.length - 1] !== confirmed.value[confirmed.value.length - 1])
    try {
      const page = await api.positionMovements(id, cursors[cursors.length - 1])
      if (!active) return
      if (page.positionId !== id || page.rows.some(m => m.sourcePlacementId !== id && m.destinationPlacementId !== id))
        throw new ContractFailure()
      history.value = page
      confirmed.value = cursors
      if (pageChanged) { await nextTick(); if (active) heading.value?.focus() }
    } catch (reason) {
      if (active) {
        error.value = explain(reason); emit('failure', reason)
        if (pageChanged) { await nextTick(); if (active) retry.value?.focus() }
      }
    } finally { if (active) loading.value = false }
  }, { immediate: true })
</script>
<template>
  <section class="position-history" role="region" aria-label="Position movement history">
    <h3 ref="heading" tabindex="-1">Movement history</h3>
    <p v-if="error || state === 'unavailable'" class="warning" role="status">
      {{ error || 'Movement history could not be refreshed.' }}
      {{ history ? 'Showing the last movement read.' : 'Movement history is unavailable.' }}
    </p>
    <button ref="retry" v-if="error && state === 'ready'" class="retry" @click="attempt++">Retry movement page {{ requested.length }}</button>
    <p v-if="loading || state === 'loading'" role="status">Reading movement history…</p>
    <p v-if="history && !history.complete" class="warning" role="status">Partial movement history.</p>
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
        <button :disabled="loading || state !== 'ready' || !history.complete || !history.nextAfter" @click="history.nextAfter && (requested = [...confirmed, history.nextAfter])">Older movements</button>
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
