<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { Location, TransferControls, TransferPosition } from './api'
import { itemLabel, money } from './receiving'

const props = defineProps<{ source: Location; locations: Location[]; original: TransferPosition;
  current?: TransferPosition; reserved: number; ready: boolean; controls: TransferControls }>()
const emit = defineEmits<{ close: []; recover: [] }>()
const dialog = ref<HTMLDialogElement>()
const destinationId = ref('')
const destination = computed(() => props.locations.find(l => l.id === destinationId.value))
const sourceCode = ref('')
const destinationCode = ref('')
const count = ref<number | ''>('')
const reason = ref('')
const moveAll = ref(false)
const quantities = ref<Record<string, number | ''>>({})
const issue = ref('')
const sending = ref(false)
const attempted = ref(false)
const line = props.original.layer
const sourceAtOpen = props.source.code
const restricted = computed(() => props.reserved + (line.quarantinedQuantity ?? 0))
const movable = computed(() => line.quantity - restricted.value)
const movableUnbacked = computed(() => line.unbackedQuantity - restricted.value)
const stale = computed(() => !props.current || props.current.purchaseRevision !== props.original.purchaseRevision ||
  props.source.code !== sourceAtOpen || props.current.layer.currentCatalog.revision !== line.currentCatalog.revision)
const backing = computed(() => props.original.backing.map(offer => ({
  offerId: offer.offerId, quantity: Number(quantities.value[offer.offerId] || 0)
})).filter(row => row.quantity > 0))
const movedBacking = computed(() => moveAll.value ? line.backedQuantity : backing.value.reduce((total, row) => total + row.quantity, 0))
const available = computed(() => movable.value > 0 && props.ready && props.controls.enabled && !stale.value && !sending.value)
watch(() => destination.value?.code, () => { destinationCode.value = ''; issue.value = '' })
watch([sourceCode, destinationCode, count, reason, moveAll, movedBacking], () => { issue.value = ''; attempted.value = false })
onMounted(() => dialog.value?.showModal())

async function submit() {
  if (!available.value || !destination.value || !count.value) return
  issue.value = ''
  attempted.value = true
  if (sourceCode.value !== props.source.code || destinationCode.value !== destination.value.code) {
    issue.value = 'The entered codes must match both physical bin labels.'
    return
  }
  if (moveAll.value && count.value !== line.quantity) {
    issue.value = 'Recount the whole position. The count moved must equal ' + line.quantity + ' units.'
    return
  }
  const unbacked = count.value - movedBacking.value
  if (unbacked < 0 || unbacked > movableUnbacked.value) {
    issue.value = 'Choose backing so the moved count is covered by the selected offer units and available unbacked units.'
    return
  }
  if (!reason.value.trim()) { issue.value = 'Enter a reason for the transfer.'; return }
  sending.value = true
  try {
    const receipt = await props.controls.dispatch({ kind: 'transfer', workspaceId: props.controls.workspaceId, body: {
      commandId: crypto.randomUUID(), expectedRevision: props.original.purchaseRevision, purchaseId: line.purchaseId,
      sourcePlacementId: line.placementId, sourceLocationId: props.source.id, confirmedSourceLocationCode: sourceCode.value,
      destinationLocationId: destination.value.id, confirmedDestinationLocationCode: destinationCode.value,
      quantity: count.value, reason: reason.value.trim(),
      ...(moveAll.value ? { moveAllBacking: true, backing: [] } : { backing: backing.value })
    } })
    if (receipt) emit('close')
    else if (props.controls.pending) emit('recover')
  } finally { sending.value = false }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="transfer-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="transfer-title">Transfer stock</h2>
      <button class="close" aria-label="Close transfer" :disabled="sending" @click="emit('close')">×</button></header>
    <form class="form-body" @submit.prevent="submit">
      <div><strong>{{ line.purchaseReference }} · {{ line.deliveryReference }}</strong>
        <p class="field-hint">{{ itemLabel([line.currentCatalog], line.catalogId) }}</p>
        <p>{{ line.quantity }} in {{ sourceAtOpen }} · {{ line.unbackedQuantity }} unbacked</p>
        <p v-if="restricted" class="field-hint">{{ reserved }} reserved for builds · <template v-if="line.quarantinedQuantity">{{ line.quarantinedQuantity }} quarantined · </template>{{ movable }} can move, including {{ movableUnbacked }} without offer backing.</p></div>
      <p v-if="line.catalogRevision !== line.currentCatalog.revision || !line.currentCatalog.integrationEligible" class="warning">
        The layer keeps its recorded catalog revision {{ line.catalogRevision }}. The current catalog revision is {{ line.currentCatalog.revision }}.
        The selected offer quantities stay backed when they move.
      </p>
      <p v-if="stale" class="warning" role="status">This layer or its source changed. Close this form and review the latest bin stock before starting another transfer.</p>
      <p v-else-if="!ready || !controls.enabled" role="status">{{ sending ? 'Recording transfer…' : 'Waiting for a complete, connected bin and register read.' }}</p>
      <fieldset class="fields" :disabled="!available">
        <label class="span">Destination bin<select v-model="destinationId" required autofocus>
          <option disabled value="">Choose a different bin</option>
          <option v-for="bin in locations.filter(l => l.id !== source.id)" :key="bin.id" :value="bin.id">{{ bin.code }} · {{ bin.name }}</option>
        </select></label>
        <label>Confirm source bin code<input v-model="sourceCode" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
        <label>Confirm destination bin code<input v-model="destinationCode" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
        <label>Count moved<input v-model="count" type="number" inputmode="numeric" min="1" :max="movable" required /></label>
        <label class="span">Reason<input v-model="reason" required maxlength="200" /></label>
      </fieldset>
      <label v-if="original.backing.length" class="whole-position">
        <input v-model="moveAll" type="checkbox" :disabled="!available || restricted > 0" />
        <span>Move the whole position with all offer backing
          <small>{{ line.quantity }} units in this position · {{ line.backedQuantity }} backed across {{ original.backing.length }} {{ original.backing.length === 1 ? 'offer' : 'offers' }}</small>
        </span>
      </label>
      <fieldset v-if="original.backing.length && !moveAll" class="backing-list" :disabled="!available">
        <legend>Offer backing to move</legend>
        <p class="field-hint">Enter units for each offer that follows this stock. A blank count leaves that offer’s backing in the source bin.</p>
        <label v-for="offer in original.backing" :key="offer.offerId">
          <span>{{ money(offer.unitPriceMinor, offer.currency) }} per unit · {{ offer.quantity }} backed<small>Offer {{ offer.offerId }}</small></span>
          <input v-model="quantities[offer.offerId]" :aria-label="'Move backed units for offer ' + offer.offerId"
            type="number" inputmode="numeric" min="0" :max="offer.quantity" />
        </label>
      </fieldset>
      <p v-if="count" class="preview" aria-live="polite">{{ count }} units to move · {{ movedBacking }} backed</p>
      <p v-if="issue || (attempted && controls.feedback)" role="alert" class="warning">{{ issue || controls.feedback }}</p>
      <div class="actions"><button type="button" :disabled="sending" @click="emit('close')">Cancel</button>
        <button class="primary" :disabled="!available">Confirm transfer</button></div>
    </form>
  </dialog>
</template>
<style scoped>
.form-body .whole-position { display: flex; align-items: flex-start; gap: 8px }
.whole-position input[type=checkbox] { width: 17px; min-height: auto; flex: none; margin-top: 3px }
.whole-position small { display: block; font-weight: normal }
.field-hint, .backing-list span { overflow-wrap: anywhere }
</style>
