<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import BinStock from './BinStock.vue'
import BuildHolds from './BuildHolds.vue'
import StockLossWorkspace from './StockLossWorkspace.vue'
import type { CountLink } from './countLinks'
import { label } from './presentation'
import type { Catalog, Location, TransferControls, Workspace } from './api'
const props = defineProps<{ page: 'catalog' | 'locations'; workspace: Workspace; connected: boolean; transfers: TransferControls; confirmedSourceId?: string; countLink?: CountLink }>()
const emit = defineEmits<{ failure: [reason: unknown] }>()
const query = ref('')
const search = ref<HTMLInputElement>()
const host = ref<HTMLElement>()
const detail = ref<HTMLElement>()
const compact = ref(true)
const inspectedId = ref<string | null>(null)
const current = computed(() => props.workspace[props.page].find(row => row.id === inspectedId.value))
const inspected = computed<Catalog | Location | undefined>(previous =>
  current.value || (!props.workspace.complete && previous?.id === inspectedId.value ? previous : undefined))
const title = computed(() => props.page === 'catalog' ? 'Catalog register' : 'Location register')
const matches = (values: string[]) => values.some(v => v.toLowerCase().includes(query.value.trim().toLowerCase()))
const catalog = computed(() => props.workspace.catalog.filter(r => matches([r.name, r.itemNumber, r.variant || '', r.itemType])))
const locations = computed(() => props.workspace.locations.filter(r => matches([r.name, r.code])))
const count = computed(() => props.page === 'catalog' ? catalog.value.length : locations.value.length)
let origin: HTMLButtonElement | undefined
let resize: ResizeObserver | undefined
watch(() => props.countLink?.binId, (id, previous) => {
  if (props.page !== 'locations') return
  if (id) inspectedId.value = id
  else if (previous) inspectedId.value = null
}, { immediate: true })
function inspect(row: Catalog | Location, event: MouseEvent) {
  origin = event.currentTarget as HTMLButtonElement
  if (props.countLink && props.page === 'locations') window.location.hash = 'locations/' + row.id
  inspectedId.value = row.id
}
watch(() => props.confirmedSourceId, id => {
  if (id && !inspectedId.value && props.page === 'locations') inspectedId.value = id
})
watch([inspectedId, compact, () => props.countLink?.holdPositionId || props.countLink?.lossPositionId], async () => {
  await nextTick()
  if (props.countLink?.holdPositionId || props.countLink?.lossPositionId) return
  if (detail.value instanceof HTMLDialogElement) {
    if (!detail.value.open) detail.value.showModal()
  } else detail.value?.querySelector<HTMLButtonElement>('button')?.focus()
}, { immediate: true })
function inspectBin(id: string) {
  if (props.countLink) window.location.hash = 'locations/' + id
  inspectedId.value = id
}
function close() {
  if (detail.value instanceof HTMLDialogElement) detail.value.close()
  inspectedId.value = null
  if (props.countLink && props.page === 'locations') window.location.hash = 'locations'
  if (origin?.isConnected) origin.focus()
  else search.value?.focus()
}
function keyboard(event: KeyboardEvent) {
  const field = event.target as HTMLElement
  if (event.key === '/' && !event.ctrlKey && !event.altKey && !event.metaKey &&
      !field.closest('input,select,textarea,dialog,[contenteditable=true]') && !document.querySelector('dialog[open]')) {
    event.preventDefault()
    search.value?.focus()
  }
}
onMounted(() => {
  window.addEventListener('keydown', keyboard)
  if (host.value) {
    compact.value = host.value.clientWidth < 920
    resize = new ResizeObserver(([entry]) => { compact.value = entry.contentRect.width < 920 })
    resize.observe(host.value)
  }
})
onBeforeUnmount(() => {
  resize?.disconnect()
  window.removeEventListener('keydown', keyboard)
})
</script>
<template>
  <div ref="host" class="register-workspace" :class="{ docked: inspected && !compact }">
  <section class="panel" :aria-label="title">
    <header class="panel-head"><h2>{{ title }}</h2><span class="count" aria-live="polite">{{ count }} {{ count === 1 ? 'record' : 'records' }}</span></header>
    <div class="toolbar"><label class="search">
      <span class="lens" aria-hidden="true"></span>
      <input ref="search" v-model="query" :aria-label="'Search ' + page" type="search" :placeholder="page === 'catalog' ? 'Search item numbers, names, variants…' : 'Search location codes or names…'" />
      <kbd aria-hidden="true">/</kbd>
    </label></div>
    <div v-if="count" class="table-wrap">
      <table v-if="page === 'catalog'">
        <caption class="sr">Catalog items</caption>
        <thead><tr><th scope="col">Item</th><th scope="col">Color</th><th scope="col">Integration</th></tr></thead>
        <tbody><tr v-for="row in catalog" :key="row.id">
          <td><button class="row-link" @click="inspect(row, $event)" :aria-label="'Inspect ' + row.name">
            <strong>{{ row.name }}</strong><small>{{ label(row.itemType) }} · {{ row.itemNumber }}{{ row.variant ? ' · ' + row.variant : '' }}</small>
          </button></td>
          <td>{{ row.colorState === 'known' ? 'ID ' + row.colorId : label(row.colorState) }}</td>
          <td><span class="tag" :class="{ held: !row.integrationEligible }">{{ row.integrationEligible ? 'Eligible' : 'Blocked' }}</span></td>
        </tr></tbody>
      </table>
      <table v-else>
        <caption class="sr">Storage locations</caption>
        <thead><tr><th scope="col">Location</th><th scope="col">Code</th><th scope="col">Revision</th></tr></thead>
        <tbody><tr v-for="row in locations" :key="row.id">
          <td><button class="row-link" @click="inspect(row, $event)" :aria-label="'Inspect ' + row.name"><strong>{{ row.name }}</strong></button></td>
          <td>{{ row.code }}</td><td>{{ row.revision }}</td>
        </tr></tbody>
      </table>
    </div>
    <div v-else class="empty">
      <strong>{{ !workspace.complete ? 'No records in the partial read' : query ? 'No matching records' : page === 'catalog' ? 'No catalog items' : 'No locations' }}</strong>
      <button v-if="query" @click="query = ''; search?.focus()">Clear search</button>
      <p v-else-if="workspace.complete">{{ page === 'catalog' ? 'Add the first item to the catalog register.' : 'Add a location to start the register.' }}</p>
    </div>
  </section>
  <p v-if="countLink && !inspected && page === 'locations'" class="warning" role="status">{{ workspace.complete && connected ? 'The linked bin was not found.' : 'The linked bin is unavailable.' }}</p>
  <component v-if="inspected" :is="compact ? 'dialog' : 'aside'" ref="detail" class="panel inspector"
    aria-labelledby="item-title" @cancel.prevent="close" @keydown.esc.prevent="close">
    <header class="panel-head"><h2 id="item-title">{{ inspected.name }}</h2><button class="close" aria-label="Close details" @click="close">×</button></header>
    <p v-if="!current" class="warning" role="status">The partial register omits this record. Showing its last received details.</p>
    <dl class="details">
      <template v-if="'itemType' in inspected">
        <dt>Item type</dt><dd>{{ label(inspected.itemType) }}</dd>
        <dt>Item number</dt><dd>{{ inspected.itemNumber }}</dd><dt>Variant</dt><dd>{{ inspected.variant || 'None recorded' }}</dd>
        <dt>Color</dt><dd>{{ inspected.colorState === 'known' ? 'ID ' + inspected.colorId : label(inspected.colorState) }}</dd>
        <dt>Condition</dt><dd>{{ label(inspected.condition) }}</dd><dt>Completeness</dt><dd>{{ label(inspected.completeness) }}</dd>
        <dt>Catalog source</dt><dd>{{ inspected.source }}</dd><dt>Source license</dt><dd>{{ label(inspected.license) }}</dd>
        <dt>Catalog mapping</dt><dd>{{ label(inspected.mapping) }}</dd><dt>Integration</dt><dd>{{ inspected.integrationEligible ? 'Eligible' : 'Blocked' }}</dd>
      </template>
      <template v-else><dt>Location code</dt><dd>{{ inspected.code }}</dd></template>
      <dt>Record ID</dt><dd>{{ inspected.id }}</dd><dt>Revision</dt><dd>{{ inspected.revision }}</dd>
    </dl>
    <BinStock v-if="'code' in inspected" :key="inspected.id" :location="inspected" :as-of="workspace.asOf"
      :connected="connected" :locations="workspace.locations" :catalog="workspace.catalog" :transfers="transfers" :count-link="countLink"
      @inspect="inspectBin" @recover="close" @failure="emit('failure', $event)" />
  </component>
  <StockLossWorkspace v-if="inspected && 'code' in inspected && countLink?.binId === inspected.id && countLink.lossPositionId"
    :key="countLink.binId + '/' + countLink.lossPositionId + '/' + (countLink.lossReceiptId ? 'posting/' + countLink.lossReceiptId : 'case/' + countLink.lossCaseId)" :location="inspected" :position="countLink.lossPositionId"
    :catalog="workspace.catalog" :case-id="countLink.lossCaseId" :posting="countLink.lossReceiptId" :as-of="workspace.asOf" :connected="connected" :controls="transfers"
    @failure="emit('failure', $event)" @recover="close" @close="inspectBin(inspected.id)" />
  <BuildHolds v-if="inspected && 'code' in inspected && countLink?.binId === inspected.id && countLink.holdPositionId"
    :key="countLink.binId + '/' + countLink.holdPositionId" :location="inspected" :position-id="countLink.holdPositionId"
    :as-of="workspace.asOf" :connected="connected" :controls="transfers"
    @failure="emit('failure', $event)" @recover="close" @close="inspectBin(inspected.id)" />
  </div>
</template>
