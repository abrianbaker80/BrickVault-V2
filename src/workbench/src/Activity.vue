<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef } from 'vue'
import type { Diagnostic, Workspace } from './api'
import { api, explain } from './api'
import { label, time } from './presentation'
const props = defineProps<{ workspace: Workspace; connected: boolean }>()
const emit = defineEmits<{ failure: [error: unknown] }>()
const correlation = ref('')
const diagnostic = shallowRef<Diagnostic | null>(null)
const issue = ref('')
const loading = ref(false)
let generation = 0
onBeforeUnmount(() => ++generation)
async function lookup(id: string) {
  if (!props.connected) return
  const current = ++generation
  correlation.value = id
  diagnostic.value = null
  issue.value = ''
  loading.value = true
  try {
    const result = await api.diagnostic(id)
    if (generation === current) diagnostic.value = result
  } catch (reason) {
    if (generation !== current) return
    emit('failure', reason)
    issue.value = explain(reason)
  } finally {
    if (generation === current) loading.value = false
  }
}
const names = computed(() => new Map([...props.workspace.locations, ...props.workspace.catalog].map(row => [row.id, row.name])))
const recordName = (id: string) => names.value.get(id) || id
const pageSize = 25
const anchors = ref<string[]>([])
const auditTitle = ref<HTMLElement>()
const start = computed(() => {
  const anchor = anchors.value.at(-1)
  return anchor ? props.workspace.audit.findIndex(row => row.commandId === anchor) : 0
})
const visibleRows = computed(() => start.value < 0 ? [] : props.workspace.audit.slice(start.value, start.value + pageSize))
const nextAnchor = computed(() => start.value < 0 ? undefined : props.workspace.audit[start.value + pageSize]?.commandId)
async function goToPage(direction: 'older' | 'newer' | 'newest') {
  if (direction === 'older' && nextAnchor.value) anchors.value.push(nextAnchor.value)
  if (direction === 'newer') anchors.value.pop()
  if (direction === 'newest') anchors.value = []
  await nextTick()
  auditTitle.value?.focus()
}
</script>
<template>
  <div class="activity-grid">
    <section class="panel" aria-labelledby="audit-title">
      <header class="panel-head"><h2 id="audit-title" ref="auditTitle" tabindex="-1">Audit history</h2><span class="count">{{ workspace.audit.length }} events</span></header>
      <ul v-if="visibleRows.length" class="audit">
        <li v-for="row in visibleRows" :key="row.commandId">
          <div><strong>{{ recordName(row.entityId) }}</strong><small>{{ label(row.kind) }} · revision {{ row.revision }} · {{ row.actor }}</small>
            <small>{{ time(row.occurredAt) }}</small></div>
          <button :disabled="!connected" :aria-label="'Inspect command for ' + recordName(row.entityId) + ' revision ' + row.revision" @click="lookup(row.correlationId)">Inspect</button>
        </li>
      </ul>
      <div v-else-if="start < 0" class="empty"><p>This history page is unavailable in the current read.</p>
        <button @click="goToPage('newest')">Back to newest commands</button></div>
      <div v-else class="empty"><strong>{{ connected ? 'No audit events' : 'Audit history unavailable' }}</strong><p v-if="connected">Saved changes appear here.</p></div>
      <nav v-if="visibleRows.length && (anchors.length || nextAnchor)" class="history-pages" aria-label="Audit history pages">
        <span>{{ start + 1 }}–{{ start + visibleRows.length }} of {{ workspace.audit.length }}</span>
        <button :disabled="!anchors.length || !connected" @click="goToPage('newer')">Newer commands</button>
        <button :disabled="!nextAnchor || !connected" @click="goToPage('older')">Older commands</button>
      </nav>
    </section>
    <div class="side-panels">
      <section class="panel" aria-labelledby="worker-title">
        <header class="panel-head"><h2 id="worker-title">Worker</h2><span class="tag" :class="{held: workspace.health.workerState !== 'healthy'}">{{ label(workspace.health.workerState) }}</span></header>
        <dl class="details">
          <dt>Last heartbeat</dt><dd>{{ workspace.health.lastHeartbeat ? time(workspace.health.lastHeartbeat) : 'None received' }}</dd>
          <dt>Pending jobs</dt><dd>{{ workspace.health.pendingJobs }}</dd><dt>Blocked jobs</dt><dd>{{ workspace.health.blockedJobs }}</dd>
          <dt>Completed jobs</dt><dd>{{ workspace.health.completedJobs }}</dd>
          <dt>API / schema</dt><dd>v{{ workspace.health.apiVersion }} / v{{ workspace.health.schemaVersion }}</dd>
        </dl>
      </section>
      <section class="panel" aria-labelledby="diagnostic-title">
        <header class="panel-head"><h2 id="diagnostic-title">Command diagnostic</h2></header>
        <form class="diagnostic" @submit.prevent="lookup(correlation.trim())">
          <label>Correlation ID<input v-model="correlation" required pattern="[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}" spellcheck="false" title="Enter a full UUID from a command receipt or audit event." /></label>
          <button :disabled="!connected || loading">{{ loading ? 'Checking…' : 'Look up command' }}</button>
        </form>
        <p v-if="issue" class="diagnostic-issue" role="alert">{{ issue }}</p>
        <dl v-if="diagnostic" class="details" aria-live="polite">
          <dt>Job state</dt><dd>{{ label(diagnostic.jobState) }}</dd><dt>Command ID</dt><dd>{{ diagnostic.commandId }}</dd>
          <dt>Correlation ID</dt><dd>{{ diagnostic.correlationId }}</dd><dt>Record ID</dt><dd>{{ diagnostic.entityId }}</dd>
          <dt>Revision</dt><dd>{{ diagnostic.revision }}</dd><dt>Payload version</dt><dd>{{ diagnostic.payloadVersion }}</dd>
          <dt>Processed</dt><dd>{{ diagnostic.processedAt ? time(diagnostic.processedAt) : 'Pending' }}</dd>
        </dl>
      </section>
    </div>
  </div>
</template>
<style scoped>
.history-pages { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; padding: 12px 17px; }
</style>
