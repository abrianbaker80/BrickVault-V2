<script setup lang="ts">
import { computed, nextTick, onMounted, onBeforeUnmount, ref, shallowRef, watchPostEffect } from 'vue'
import { partImage } from '../art'
import {
  authoritative,
  batches,
  displayDialog,
  locations,
  inventoryMetrics,
  onHand,
  readModes,
  readText,
  registerInteractions,
  resultCount,
  scopes,
  sellable,
  statuses,
  stockStatus
} from './model'
import type { Controller, ReadMode, Scope } from './model'
import Detail from './Detail.vue'
const props = defineProps<{ api: Controller; host: HTMLElement }>()
const s = shallowRef(props.api.get())
const off = props.api.subscribe(() => {
  s.value = props.api.get()
})
let cleanup: (() => void) | undefined
onMounted(() => {
  cleanup = registerInteractions(props.host, props.api)
})
onBeforeUnmount(() => {
  off()
  cleanup?.()
})
const detailDialog = ref<HTMLDialogElement | null>(null)
const previewDialog = ref<HTMLDialogElement | null>(null)
const search = ref<HTMLInputElement | null>(null)
const inspected = computed(() => s.value.lots.find((l) => l.id === s.value.inspected))
const rows = computed(() => s.value.visible.map((id) => s.value.lots.find((l) => l.id === id)!))
const canAct = computed(() => authoritative(s.value))
const metrics = computed(() => inventoryMetrics(s.value))
const filtered = computed(() =>
  Boolean(s.value.query || s.value.location || s.value.condition || s.value.batch || s.value.status)
)
watchPostEffect(() => {
  displayDialog(detailDialog.value, Boolean(inspected.value && s.value.compact))
  displayDialog(previewDialog.value, Boolean(s.value.preview || s.value.receipt))
  if (s.value.receipt) previewDialog.value?.querySelector<HTMLButtonElement>('[data-receipt-done]')?.focus()
})
function value(event: Event) {
  return (event.target as HTMLInputElement).value
}
function clear() {
  props.api.filter({ query: '', location: '', condition: '', batch: '', status: '' })
  search.value?.focus()
}
async function inspect(id: string) {
  const origin = document.activeElement
  props.api.inspect(id)
  if (s.value.compact) return
  await nextTick()
  if (s.value.inspected === id && document.activeElement === origin)
    props.host.querySelector<HTMLButtonElement>('.iv-inspector .iv-icon-button')?.focus()
}
function closeInspector() {
  if (!s.value.compact)
    props.host.querySelector<HTMLButtonElement>('[data-inspect="' + s.value.inspected + '"]')?.focus()
  props.api.inspect(null)
}
</script>

<template>
  <div class="iv-root">
    <div role="group" :aria-label="metrics.label">
      <p v-if="s.read !== 'Normal'" class="iv-metrics-note" role="status">{{ metrics.label }}</p>
      <div class="iv-metrics">
        <div v-for="m in metrics.rows" :key="m.label" class="iv-metric">
          <span>{{ m.label }}</span>
          <div>
            <strong>{{ m.display }}</strong> <small>{{ m.unit }}</small>
          </div>
        </div>
      </div>
    </div>
    <div class="iv-workspace" :class="{ 'has-inspector': inspected && !s.compact }">
      <section class="iv-register" aria-labelledby="iv-register-title">
        <div class="iv-register-head">
          <div class="iv-title-group">
            <h2 id="iv-register-title">Lot register</h2>
            <span class="iv-result-count" aria-live="polite">{{ resultCount(s) }}</span>
          </div>
          <button
            class="iv-action"
            :disabled="!canAct"
            @click="api.openPreview(s.selected.length ? 'Selected lots' : 'Matching lots')"
          >
            Preview recount
          </button>
        </div>
        <div class="iv-toolbar">
          <label class="iv-search"
            ><i class="iv-lens" aria-hidden="true" /><input
              ref="search"
              type="search"
              data-inventory-search=""
              aria-label="Search inventory"
              placeholder="Search lots, parts, bins…"
              :value="s.query"
              @input="api.filter({ query: value($event) })"
            /><kbd aria-hidden="true">/</kbd></label
          >
          <select
            aria-label="Filter condition"
            :value="s.condition"
            @change="api.filter({ condition: value($event) })"
          >
            <option value="">Condition</option>
            <option>New</option>
            <option>Used</option>
          </select>
          <select
            aria-label="Filter location"
            :value="s.location"
            @change="api.filter({ location: value($event) })"
          >
            <option value="">Location</option>
            <option v-for="loc in locations" :key="loc">{{ loc }}</option>
          </select>
          <select aria-label="Filter batch" :value="s.batch" @change="api.filter({ batch: value($event) })">
            <option value="">Batch</option>
            <option v-for="b in batches" :key="b.id">{{ b.id }}</option>
          </select>
          <select
            aria-label="Filter stock status"
            :value="s.status"
            @change="api.filter({ status: value($event) })"
          >
            <option value="">Status</option>
            <option v-for="status in statuses" :key="status">{{ status }}</option>
          </select>
          <button v-if="filtered" class="iv-text-button" @click="clear">Clear filters</button>
        </div>
        <div v-if="s.selected.length" class="iv-selection">
          <strong>{{ s.selected.length }} selected</strong
          ><span>{{ s.selected.filter((id) => !s.visible.includes(id)).length }} outside current results</span
          ><button class="iv-text-button" @click="api.clearSelection">Clear selection</button>
        </div>
        <div v-if="s.read !== 'Normal'" class="iv-read-banner" role="status">
          <span>{{ readText[s.read] }}</span
          ><button class="iv-text-button" @click="api.changeRead('Normal')">Retry read</button>
        </div>
        <div class="iv-table-shell">
          <table aria-label="Inventory lots" :aria-busy="s.pending || s.read === 'Loading'">
            <thead>
              <tr>
                <th scope="col" class="iv-check-col"><span class="iv-sr">Select</span></th>
                <th scope="col">Item / lot</th>
                <th scope="col" class="iv-location">Location</th>
                <th scope="col" class="iv-number iv-physical">On hand</th>
                <th scope="col" class="iv-number">Sellable</th>
                <th scope="col" class="iv-state-col">Status</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="lot in rows" :key="lot.id" :class="{ 'iv-inspected': s.inspected === lot.id }">
                <td class="iv-check-col">
                  <input
                    type="checkbox"
                    :aria-label="'Select ' + lot.id"
                    :checked="s.selected.includes(lot.id)"
                    :disabled="!canAct"
                    @change="api.toggle(lot.id)"
                  />
                </td>
                <td>
                  <button
                    class="iv-item"
                    :data-inspect="lot.id"
                    :disabled="s.pending"
                    @click="inspect(lot.id)"
                  >
                    <span class="iv-sr">Inspect</span
                    ><span class="iv-thumb"><img :src="partImage(lot.art)" alt="" /></span
                    ><span class="iv-item-name"
                      ><strong>{{ lot.name }}</strong
                      ><small>{{ lot.color }} · {{ lot.id }}</small></span
                    >
                  </button>
                </td>
                <td class="iv-location">
                  <span class="iv-bin">{{ lot.placements[0].bin }}</span
                  ><small v-if="lot.placements.length > 1" class="iv-more"
                    >+{{ lot.placements.length - 1 }}</small
                  >
                </td>
                <td class="iv-number iv-physical">{{ onHand(lot) }}</td>
                <td class="iv-number iv-free">{{ sellable(lot) }}</td>
                <td class="iv-state-col">
                  <span class="iv-badge" :class="stockStatus(lot)[1]"
                    ><i aria-hidden="true" />{{ stockStatus(lot)[0] }}</span
                  >
                </td>
              </tr>
            </tbody>
          </table>
          <div v-if="!rows.length && s.read === 'Normal' && !s.pending" class="iv-empty">
            <strong>No matching lots</strong
            ><button class="iv-text-button" @click="clear">Clear filters</button>
          </div>
        </div>
        <div class="iv-footer">
          <span role="status">{{
            s.pending ? 'Updating results…' : s.read === 'Normal' ? 'Snapshot · 09:42' : s.read + ' read'
          }}</span
          ><label
            >Read simulation
            <select
              :value="s.read"
              @change="api.changeRead(value($event) as ReadMode)"
              aria-label="Read simulation"
            >
              <option v-for="mode in readModes" :key="mode">{{ mode }}</option>
            </select></label
          >
        </div>
      </section>
      <aside
        v-if="!s.compact && inspected"
        class="iv-inspector"
        aria-label="Lot inspector"
        @keydown.esc="closeInspector"
      >
        <Detail :lot="inspected" @close="closeInspector" />
      </aside>
    </div>
    <dialog
      ref="detailDialog"
      class="iv-dialog iv-inspector-dialog"
      aria-label="Lot inspector"
      @cancel.prevent="closeInspector"
    >
      <Detail v-if="s.compact && inspected" :lot="inspected" @close="closeInspector" />
    </dialog>
    <dialog
      ref="previewDialog"
      class="iv-dialog iv-preview"
      aria-labelledby="iv-action-title"
      @cancel.prevent="api.closePreview()"
    >
      <div class="iv-panel-title">
        <h2 id="iv-action-title">{{ s.receipt ? 'Recount receipt' : 'Preview recount' }}</h2>
        <button class="iv-icon-button" aria-label="Close recount" @click="api.closePreview()">×</button>
      </div>
      <div v-if="s.preview" class="iv-dialog-body">
        <label class="iv-field"
          >Action scope<select :value="s.scope" @change="api.openPreview(value($event) as Scope)">
            <option v-for="scope in scopes" :key="scope">{{ scope }}</option>
          </select></label
        >
        <p>
          <strong>{{ s.preview.length }} {{ s.preview.length === 1 ? 'lot' : 'lots' }}</strong> in this
          preview. Queue a stock recount for these IDs. Quantities stay the same.
        </p>
        <ul class="iv-targets" aria-label="Frozen targets" tabindex="0">
          <li v-for="target in s.preview" :key="target.id">
            <b>{{ target.id }}</b
            ><span>{{ s.lots.find((l) => l.id === target.id)?.name }}</span
            ><small>rev {{ target.revision }}</small>
          </li>
        </ul>
        <button
          v-if="s.preview.length"
          class="iv-text-button"
          :disabled="s.simulated"
          @click="api.simulateChange()"
        >
          Simulate revision change
        </button>
        <p v-if="s.simulated" role="status">The first target changed after preview. It will be skipped.</p>
        <div class="iv-dialog-actions">
          <button class="iv-action" @click="api.closePreview()">Cancel</button
          ><button class="iv-primary" :disabled="!s.preview.length" @click="api.confirm()">
            Queue recount
          </button>
        </div>
      </div>
      <div v-if="s.receipt" class="iv-dialog-body">
        <p role="status">
          <strong
            >{{ s.receipt.queued.length }} queued · {{ s.receipt.changed.length }} changed ·
            {{ s.receipt.alreadyQueued.length }} already queued</strong
          >
        </p>
        <p v-if="s.receipt.queued.length">Queued: {{ s.receipt.queued.join(', ') }}</p>
        <p v-if="s.receipt.alreadyQueued.length">Already queued: {{ s.receipt.alreadyQueued.join(', ') }}</p>
        <p v-if="s.receipt.changed.length">
          Skipped: {{ s.receipt.changed.join(', ') }}. Open a new preview to review the latest revision.
        </p>
        <button class="iv-primary" data-receipt-done="" @click="api.closePreview()">Done</button>
      </div>
    </dialog>
  </div>
</template>
