<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { api, explain } from './api'
import type { StockPage, StockQuery, TransferControls, Workspace } from './api'
import { label, time } from './presentation'
import PositionInspector from './PositionInspector.vue'
import ReceiptLotInspector from './ReceiptLotInspector.vue'
import RecountPreview from './RecountPreview.vue'
import RecountWork from './RecountWork.vue'

const props = defineProps<{ workspace: Workspace; connected: boolean; controls: TransferControls; csrf: string }>()
const emit = defineEmits<{ failure: [reason: unknown] }>()
const search = ref<HTMLInputElement>()
const host = ref<HTMLElement>()
const inspector = ref<HTMLElement>()
const compact = ref(true)
const term = ref('')
const type = ref<StockQuery['itemType']>()
const bin = ref('')
const criteria = shallowRef<StockQuery>({})
const starts = ref<string[]>([''])
const after = computed(() => starts.value[starts.value.length - 1])
const result = shallowRef<StockPage | null>(null)
const error = ref('')
const loading = ref(false)
const lotRoute = () => location.hash.startsWith('#inventory/lot/') ? location.hash.slice('#inventory/lot/'.length).toLowerCase() : ''
const recountRoute = () => {
  const hash = location.hash
  if (hash === '#inventory/recounts') return 'queue'
  if (hash.startsWith('#inventory/recounts/')) return hash.slice('#inventory/recounts/'.length)
  return ''
}
const recountView = ref(recountRoute())
const checked = ref(new Set<string>())
const outsidePage = computed(() => checked.value.size - (result.value?.rows.filter(r => checked.value.has(r.layer.placementId)).length ?? 0))
const preview = shallowRef<{ selected: string[]; filter: StockQuery }>()
const previewButton = ref<HTMLButtonElement>()
function openPreview() {
  if (!props.controls.enabled || !result.value?.complete || lastRead.value) return
  preview.value = { selected:[...checked.value], filter:{...criteria.value} }
}
async function closePreview() {
  preview.value = undefined
  await nextTick()
  previewButton.value?.focus()
}
const routeId = () => !recountRoute() && !location.hash.startsWith('#inventory/lot/') && location.hash.startsWith('#inventory/')
  ? location.hash.slice('#inventory/'.length).toLowerCase() : ''
const selected = ref(routeId())
const selectedLot = ref(lotRoute())
const inspectorOpen = computed(() => Boolean(selected.value || selectedLot.value))
const historyView = ref<'all' | 'transfers'>('all')
const previousPosition = ref<string>(history.state?.timelineTo === selected.value ? history.state.timelineFrom : '')
const lotFrom = ref<string>(history.state?.lotTo === selectedLot.value ? history.state.lotFrom : '')
const positionFromLot = ref<string>(history.state?.lotPosition === selected.value ? history.state.returnLot : '')
const filters = computed(() => Boolean(criteria.value.q || criteria.value.itemType || criteria.value.locationId))
const totalsKnown = computed(() => result.value?.complete === true)
const lastRead = computed(() => !props.connected || loading.value || Boolean(error.value))
const itemTypes: NonNullable<StockQuery['itemType']>[] = ['part','set','minifigure','book','gear','instruction','original-box','catalog']
let observer: ResizeObserver | undefined

function apply() {
  criteria.value = { q: term.value.trim(), itemType: type.value, locationId: bin.value }
  starts.value = ['']
}
function clear() {
  term.value = ''; type.value = undefined; bin.value = ''
  apply()
  search.value?.focus()
}
let scope = ''
watch(() => [criteria.value, after.value, props.workspace.asOf, props.connected] as const,
  async ([query, cursor, , connected], _, onCleanup) => {
    let active = true
    onCleanup(() => { active = false })
    const key = JSON.stringify([query, cursor])
    if (scope !== key) result.value = null
    scope = key
    error.value = ''; loading.value = connected
    if (!connected) return
    try {
      const data = await api.positions({ ...query, after: cursor || undefined, limit: 25 })
      if (active) result.value = data
    } catch (reason) {
      if (active) { error.value = explain(reason); emit('failure', reason) }
    } finally { if (active) loading.value = false }
  }, { immediate: true })

function inspect(id: string) {
  selected.value = id
  previousPosition.value = ''
  selectedLot.value = ''
  positionFromLot.value = ''
  history.replaceState({ ...history.state, timelineFrom: '', timelineTo: '', lotPosition: '', returnLot: '' }, '', '#inventory/' + id)
}
function inspectRelated(id: string) {
  const from = selected.value
  history.pushState({ ...history.state, timelineFrom: from, timelineTo: id }, '', '#inventory/' + id)
  previousPosition.value = from
  selected.value = id
}
function inspectLot(id: string) {
  history.pushState({ ...history.state, lotFrom: selected.value, lotTo: id }, '', '#inventory/lot/' + id)
  routeChanged()
}
function inspectFromLot(id: string) {
  history.pushState({ ...history.state, timelineFrom: '', timelineTo: '', lotPosition: id, returnLot: selectedLot.value },
    '', '#inventory/' + id)
  routeChanged()
}
async function close() {
  const id = selected.value || lotFrom.value
  selected.value = ''
  selectedLot.value = ''
  history.replaceState(history.state, '', '#inventory')
  await nextTick()
  const button = host.value?.querySelector<HTMLButtonElement>('[data-position="' + CSS.escape(id) + '"]')
  if (button) button.focus()
  else search.value?.focus()
}
function routeChanged() {
  recountView.value = recountRoute()
  preview.value = undefined
  selected.value = routeId()
  selectedLot.value = lotRoute()
  previousPosition.value = history.state?.timelineTo === selected.value ? history.state.timelineFrom : ''
  lotFrom.value = history.state?.lotTo === selectedLot.value ? history.state.lotFrom : ''
  positionFromLot.value = history.state?.lotPosition === selected.value ? history.state.returnLot : ''
}
function keyboard(event: KeyboardEvent) {
  if (event.key === '/' && !event.ctrlKey && !event.altKey && !event.metaKey &&
      !(event.target as HTMLElement).closest('input,textarea,select,dialog,[contenteditable=true]') &&
      !document.querySelector('dialog[open]')) {
    event.preventDefault(); search.value?.focus()
  }
}
watch([inspector, selected, selectedLot], async () => {
  await nextTick()
  if (inspector.value instanceof HTMLDialogElement && !inspector.value.open) inspector.value.showModal()
  else inspector.value?.querySelector<HTMLButtonElement>('button')?.focus()
}, { flush: 'post' })
watch(host, element => {
  observer?.disconnect()
  if (!element) return
  compact.value = element.clientWidth < 920
  observer = new ResizeObserver(([entry]) => { compact.value = entry.contentRect.width < 920 })
  observer.observe(element)
}, { flush: 'post' })
onMounted(() => {
  window.addEventListener('hashchange', routeChanged)
  window.addEventListener('keydown', keyboard)
})
onBeforeUnmount(() => {
  observer?.disconnect()
  window.removeEventListener('hashchange', routeChanged)
  window.removeEventListener('keydown', keyboard)
})
</script>
<template>
  <RecountWork v-if="recountView" :key="recountView" :route="recountView" :as-of="workspace.asOf" :state="connected ? 'ready' : 'unavailable'" @failure="emit('failure',$event)" />
  <template v-else>
  <div class="metrics inventory-metrics" role="group" :aria-label="lastRead ? 'Last received matching totals' : 'Matching inventory totals'">
    <div><span>Matching positions</span><strong>{{ totalsKnown ? result?.matchingPositions : '—' }}</strong></div>
    <div><span>Units in matching positions</span><strong>{{ totalsKnown ? result?.matchingUnits : '—' }}</strong></div>
  </div>
  <div ref="host" class="register-workspace" :class="{ docked: inspectorOpen && !compact }">
    <section class="panel inventory-register" role="region" aria-label="Stock register">
      <header class="panel-head"><h2>Stock register</h2>
        <span v-if="result" class="count">Stock read {{ time(result.asOf) }}</span></header>
      <form class="inventory-filters" aria-label="Inventory search" @submit.prevent="apply">
        <label class="search"><span class="lens" aria-hidden="true"></span>
          <input ref="search" v-model="term" type="search" maxlength="160" aria-label="Search stock positions"
            placeholder="Item, bin, purchase, delivery or ID" /><kbd aria-hidden="true">/</kbd></label>
        <label>Item type<select v-model="type" @change="apply"><option :value="undefined">All types</option>
          <option v-for="t in itemTypes" :key="t" :value="t">{{ label(t) }}</option></select></label>
        <label>Bin<select v-model="bin" @change="apply"><option value="">All bins</option>
          <option v-if="bin && !workspace.locations.some(loc => loc.id === bin)" :value="bin">Previously selected bin</option>
          <option v-for="loc in workspace.locations" :key="loc.id" :value="loc.id">{{ loc.code }} · {{ loc.name }}</option></select></label>
        <button type="submit">Search</button><button v-if="filters" type="button" @click="clear">Clear filters</button>
      </form>
      <p v-if="term.trim() !== (criteria.q || '')" class="inventory-note">Submit the search to update these results.</p>
      <p v-if="error || !connected" role="status" class="warning inventory-note">
        {{ error || 'The workspace is disconnected.' }} {{ result ? 'Showing the last stock read.' : 'Stock totals are unavailable.' }}
      </p>
      <p v-else-if="loading" role="status" class="inventory-note">{{ result ? 'Refreshing stock positions…' : 'Reading stock positions…' }}</p>
      <p v-if="result && !result.complete" role="status" class="warning inventory-note">Partial stock read. Matching totals are unavailable.</p>
      <div class="recount-actions">
        <a href="#inventory/recounts">Recount queue</a>
        <button ref="previewButton" :disabled="lastRead || !result?.complete || !controls.enabled || (!checked.size && !result.matchingPositions)" @click="openPreview">Preview recount</button>
        <span v-if="checked.size" aria-live="polite">{{ checked.size }} selected · {{ outsidePage }} outside this page</span>
        <button v-if="checked.size" @click="checked.clear()">Clear selection</button>
      </div>
      <div v-if="result?.rows.length" class="table-wrap">
        <table class="position-table"><caption class="sr">Stock positions</caption>
          <thead><tr><th scope="col">Catalog item</th><th scope="col" class="bin-col">Bin</th><th scope="col" class="receipt-col">Receipt</th><th scope="col">Stock</th></tr></thead>
          <tbody><tr v-for="{ layer, location } in result.rows" :key="layer.placementId" :class="{ inspecting: layer.placementId === selected }">
            <td><label class="recount-check"><input type="checkbox" :checked="checked.has(layer.placementId)" :disabled="lastRead || !result.complete"
              :aria-label="'Select position '+layer.placementId+' for recount · '+layer.currentCatalog.name+' in '+location.code" @change="checked.has(layer.placementId) ? checked.delete(layer.placementId) : checked.add(layer.placementId)" /></label>
              <button class="row-link" :data-position="layer.placementId"
              :aria-label="'Inspect ' + layer.currentCatalog.name + ' in ' + location.code + ', position ' + layer.placementId" :aria-current="layer.placementId === selected || undefined"
              @click="inspect(layer.placementId)"><strong>{{ layer.currentCatalog.name }}</strong>
              <small>{{ label(layer.currentCatalog.itemType) }} · {{ layer.currentCatalog.itemNumber }}{{ layer.currentCatalog.variant ? ' · ' + layer.currentCatalog.variant : '' }}</small></button>
              <small class="bin-short">Bin {{ location.code }}</small>
              <small class="receipt-short">{{ layer.purchaseReference }} · {{ layer.deliveryReference }}</small>
              <small v-if="layer.catalogRevision !== layer.currentCatalog.revision" class="identity-note">Catalog revision changed</small></td>
            <td class="bin-col">{{ location.code }}</td><td class="receipt-col">{{ layer.purchaseReference }}<small>{{ layer.deliveryReference }}</small></td>
            <td><strong>{{ layer.quantity }} on hand</strong><small>{{ layer.backedQuantity }} backed</small></td>
          </tr></tbody></table>
      </div>
      <div v-else-if="result && !lastRead && result.complete" class="empty">
        <strong>{{ after && result.matchingPositions ? 'No positions remain on this page.' : filters ? 'No matching stock positions.' : 'No current stock positions.' }}</strong>
      </div>
      <div v-if="result || after" class="inventory-paging">
        <span class="count" aria-live="polite">{{ result ? result.rows.length + ' shown' : 'Page unavailable' }} · Page {{ starts.length }}</span>
        <button :disabled="starts.length === 1 || !connected || loading" @click="starts = starts.slice(0, -1)">Previous</button>
        <button :disabled="!connected || loading || !!error || !result?.complete || !result.nextAfter" @click="result?.nextAfter && starts.push(result.nextAfter)">Next</button>
      </div>
    </section>
    <component v-if="inspectorOpen" :is="compact ? 'dialog' : 'aside'" ref="inspector" class="panel inspector"
      aria-labelledby="position-title" @cancel.prevent="close" @keydown.esc.prevent="close">
      <header class="panel-head"><h2 id="position-title">{{ selectedLot ? 'Receipt lot inspector' : 'Position inspector' }}</h2>
        <button class="close" :aria-label="selectedLot ? 'Close receipt lot inspector' : 'Close position inspector'" @click="close">×</button></header>
      <template v-if="selectedLot">
        <p v-if="lotFrom" class="previous-position"><a :href="'#inventory/' + lotFrom">Return to opening position</a></p>
        <ReceiptLotInspector :key="selectedLot" :id="selectedLot" :as-of="workspace.asOf" :connected="connected"
          @failure="emit('failure', $event)" @inspect="inspectFromLot" />
      </template>
      <template v-else>
        <p class="previous-position" v-if="previousPosition"><a :href="'#inventory/' + previousPosition">Return to previous position timeline</a></p>
        <p class="previous-position" v-if="positionFromLot"><a :href="'#inventory/lot/' + positionFromLot">Return to receipt lot</a></p>
        <PositionInspector :history-view="historyView" @view="historyView = $event" @related="inspectRelated" :key="selected" :id="selected" :as-of="workspace.asOf" :connected="connected" @failure="emit('failure', $event)" @inspect="inspect" @lot="inspectLot" />
      </template>
    </component>
  </div>
  <RecountPreview v-if="preview" :selected="preview.selected" :filter="preview.filter" :csrf="csrf" :controls="controls" :connected="connected"
    @close="closePreview" @failure="emit('failure',$event)" />
  </template>
</template>
<style scoped>
.recount-actions { display:flex; flex-wrap:wrap; align-items:center; gap:8px; padding:4px 16px 12px; font-size:13px }
.recount-actions a { color:#355340 }
.recount-check { display:inline-block; vertical-align:top }
.recount-check input { width:16px; height:16px; margin:8px 4px 4px 0 }
.previous-position a { color: #355340 }
.previous-position { margin: 8px 16px; font-size: 13px }
.inventory-metrics { grid-template-columns: repeat(2,minmax(0,1fr)) }
.inventory-register { container-type: inline-size }
.inventory-filters, .inventory-paging { display: flex; flex-wrap: wrap; align-items: end; gap: 8px; padding: 0 16px 14px }
.inventory-filters .search { min-width: 160px; flex: 2 1 195px; min-height: 34px }
.inventory-filters label:not(.search) { display: grid; gap: 2px; font-size: 12px; color: var(--muted); min-width: 0 }
.inventory-filters select { max-width: 150px; min-height: 34px; padding: 3px 6px; border: 1px solid var(--line); background: var(--surface); border-radius: 6px }
.inventory-note { margin: 0 16px 12px; font-size: 13px }
.inventory-paging { align-items: center; padding: 10px 16px; border-top: 1px solid var(--line) }
.inventory-paging .count { margin-right: auto }
.position-table th:first-child { width: 35% }
.position-table td small { display: block; color: var(--muted); font-size: 12px }
.position-table .receipt-short, .position-table .bin-short { display: none }
.position-table .identity-note { color: #805417 }
.inspecting { background: var(--soft); box-shadow: inset 3px 0 #c99e21 }
@container (max-width: 600px) {
  .receipt-col { display: none }
  .position-table .receipt-short { display: block }
  .position-table th:first-child { width: 46% }
  .position-table td, .position-table th { padding: 8px }
}
@container (max-width: 420px) {
  .inventory-filters { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); align-items: stretch }
  .inventory-filters .search { grid-column: 1 / -1; min-width: 0 }
  .inventory-filters select { width: 100%; max-width: none }
  .position-table .bin-col { display: none }
  .position-table .bin-short { display: block }
  .position-table th:first-child { width: 70% }
}
</style>
