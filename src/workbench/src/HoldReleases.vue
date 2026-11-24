<script setup lang="ts">
import { ref, shallowRef, watch } from 'vue'
import { api, explain } from './api'
import { countReceiptLink } from './countLinks'
import { time } from './presentation'

const props = defineProps<{ bin: string; position: string; hold: string; connected: boolean; asOf: string }>()
const emit = defineEmits<{ failure: [reason: unknown] }>()
const requested = ref<string[]>([''])
const displayed = ref<string[]>([''])
const retry = ref(0)
const page = shallowRef<Awaited<ReturnType<typeof api.holdReleases>>>()
const error = ref('')
const loading = ref(false)
watch(() => [props.bin, props.position, props.hold, props.connected, props.asOf, requested.value, retry.value] as const,
  async ([bin, position, hold, connected, , cursors], _, clean) => {
    let active = true
    clean(() => { active = false })
    loading.value = connected
    if (!connected) return
    error.value = ''
    try {
      const result = await api.holdReleases(bin, position, hold, cursors.at(-1))
      if (active) { page.value = result; displayed.value = cursors }
    } catch (failure) {
      if (active) { error.value = explain(failure); emit('failure', failure) }
    } finally { if (active) loading.value = false }
  }, { immediate: true })
</script>
<template>
  <section aria-label="Release history">
    <p v-if="error || !connected" class="warning" role="status">{{ error || 'The workspace is offline.' }}
      {{ page ? 'Showing the last release read.' : 'Release history is unavailable.' }}</p>
    <p v-else-if="loading" role="status">Reading releases…</p>
    <p v-else-if="page && !page.complete" class="warning" role="status">Partial release history.</p>
    <button v-if="error && connected" @click="++retry">Retry release history</button>
    <ul v-if="page?.releases.length" class="receipt-lines">
      <li v-for="entry in page.releases" :key="entry.id"><strong>{{ entry.quantity }} released · {{ entry.reason }}</strong>
        <small>{{ time(entry.releasedAt) }}</small>
        <a v-if="entry.expectedObservationId" :href="countReceiptLink(bin, entry.expectedObservationId)">Witnessed count for this decision</a>
        <details><summary>Release receipt</summary><small>Command {{ entry.commandId }}</small><small>Correlation ID {{ entry.correlationId }}</small></details>
      </li>
    </ul>
    <small v-if="page">Release read {{ time(page.asOf) }}</small>
    <nav v-if="page && (displayed.length > 1 || page.nextAfter)" aria-label="Release history pages">
      <button :disabled="!connected || loading || displayed.length < 2" @click="requested = displayed.slice(0,-1)">Newer releases</button>
      <button :disabled="!connected || loading || !page.complete || !page.nextAfter" @click="page.nextAfter && (requested = [...displayed, page.nextAfter])">Older releases</button>
    </nav>
  </section>
</template>
<style scoped>
section { display: grid; gap: 6px; margin-top: 8px } li small { display: block }
</style>
