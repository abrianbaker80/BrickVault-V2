<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { Dispatch, Purchase, Workspace } from './api'
import { itemLabel, parseMoney } from './receiving'

const props = defineProps<{ purchase: Purchase; workspace: Workspace; workspaceId: string; enabled: boolean; dispatch: Dispatch }>()
type Step = 'receive' | 'putaway' | 'offer'
const pendingLines = computed(() => props.purchase.deliveries.filter(d => d.intakeQuantity > 0))
const remaining = computed(() => props.purchase.orderedQuantity - props.purchase.balances.physicalOnHand)
const eligible = computed(() => props.workspace.catalog.filter(c => c.integrationEligible))
const placements = computed(() => props.purchase.deliveries.flatMap(d => d.placements.map(p => ({ ...p, delivery: d.reference }))))
const step = ref<Step>(pendingLines.value.length ? 'putaway' : remaining.value ? 'receive' : 'offer')
const baseRevision = ref(props.purchase.revision)
const stale = computed(() => props.purchase.revision !== baseRevision.value)
const issue = ref('')
const deliveryReference = ref('')
const counted = ref<number>()
const identity = ref('')
const receiptItem = ref('')
const basis = ref('')
const amount = ref('')
const deliveryId = ref(pendingLines.value[0]?.id || '')
const line = computed(() => pendingLines.value.find(d => d.id === deliveryId.value))
const identifiedItem = ref('')
const target = ref('')
const scanned = ref('')
const moved = ref<number>()
const offerItem = ref('')
const price = ref('')
const quantities = ref<Record<string, number | ''>>({})
const selectedItem = computed(() => line.value?.catalogId || identifiedItem.value)
const currentItem = computed(() => props.workspace.catalog.find(c => c.id === selectedItem.value))
const needsReconfirmation = computed(() => Boolean(line.value?.catalogId && currentItem.value &&
  currentItem.value.revision !== line.value.catalogRevision))
const confirmedRevision = ref<number | null>(null)
const verifiedIdentity = computed(() => currentItem.value?.integrationEligible &&
  ((line.value?.catalogId && !needsReconfirmation.value) || confirmedRevision.value === currentItem.value.revision))
const available = computed(() => placements.value.filter(p => p.freeToBackQuantity > 0 &&
  eligible.value.some(c => c.id === p.catalogId && c.revision === p.catalogRevision)))
const itemsToOffer = computed(() => eligible.value.filter(c => available.value.some(p => p.catalogId === c.id)))
const positions = computed(() => available.value.filter(p => p.catalogId === offerItem.value))
const backed = computed(() => positions.value.reduce((total, p) => total + Number(quantities.value[p.id] || 0), 0))
const canBack = computed(() => positions.value.reduce((sum, p) => sum + p.freeToBackQuantity, 0))
const location = computed(() => props.workspace.locations.find(l => l.id === target.value))
const label = (id: string) => itemLabel(props.workspace.catalog, id)
function clearBin() { scanned.value = ''; moved.value = undefined; issue.value = '' }
function clearPlacement() { identifiedItem.value = ''; confirmedRevision.value = null; target.value = ''; clearBin() }
watch(deliveryId, clearPlacement)
function identifyMixed(id: string) {
  identifiedItem.value = id
  confirmedRevision.value = currentItem.value?.revision ?? null
  clearBin()
}
watch(() => currentItem.value?.revision, revision => {
  if (confirmedRevision.value !== null && revision !== confirmedRevision.value) {
    confirmedRevision.value = null
    if (!line.value?.catalogId) { identifiedItem.value = ''; clearBin() }
  }
})
watch(target, clearBin)
watch(offerItem, () => { quantities.value = {}; issue.value = '' })
function change(next: Step) {
  step.value = next
  baseRevision.value = props.purchase.revision
  issue.value = ''
  clearPlacement()
  deliveryId.value = pendingLines.value[0]?.id || ''
  deliveryReference.value = ''; counted.value = undefined; identity.value = ''; receiptItem.value = ''; basis.value = ''; amount.value = ''
  offerItem.value = ''; price.value = ''; quantities.value = {}
}
async function save() {
  if (!props.enabled || stale.value) return
  issue.value = ''
  const common = { commandId: crypto.randomUUID(), purchaseId: props.purchase.id, expectedRevision: baseRevision.value }
  const workspaceId = props.workspaceId
  if (step.value === 'receive') {
    if (!counted.value || !identity.value || !basis.value) return
    let itemCostMinor: number | null = null
    if (basis.value === 'known') {
      try { itemCostMinor = parseMoney(amount.value, props.purchase.currency) }
      catch (error) { issue.value = (error as Error).message; return }
    }
    const result = await props.dispatch({ kind: 'delivery', workspaceId, body: { ...common,
      reference: deliveryReference.value.trim(), quantity: counted.value,
      catalogId: identity.value === 'identified' ? receiptItem.value : null, itemCostMinor } })
    if (result) { change('putaway'); deliveryId.value = result.entityId }
  } else if (step.value === 'putaway') {
    if (!line.value || !moved.value || !location.value || !verifiedIdentity.value) return
    if (scanned.value !== location.value.code) {
      issue.value = 'Bin code does not match the selected destination. Check the physical label before confirming.'
      return
    }
    const result = await props.dispatch({ kind: 'putaway', workspaceId, body: { ...common,
      deliveryId: line.value.id, catalogId: selectedItem.value, locationId: target.value,
      ...(confirmedRevision.value !== null ? { confirmedCatalogRevision: confirmedRevision.value } : {}),
      confirmedLocationCode: scanned.value, quantity: moved.value } })
    if (result) change(remaining.value ? 'receive' : pendingLines.value.length ? 'putaway' : 'offer')
  } else {
    if (!offerItem.value || !backed.value) { issue.value = 'Enter the quantity to back from at least one position.'; return }
    let unitPriceMinor: number
    try { unitPriceMinor = parseMoney(price.value, props.purchase.currency) }
    catch (error) { issue.value = (error as Error).message; return }
    if (unitPriceMinor <= 0) { issue.value = 'Enter a positive manual price.'; return }
    const backing = positions.value.filter(p => Number(quantities.value[p.id]) > 0)
      .map(p => ({ placementId: p.id, quantity: Number(quantities.value[p.id]) }))
    const result = await props.dispatch({ kind: 'offer', workspaceId, body: { ...common, catalogId: offerItem.value, unitPriceMinor, backing } })
    if (result) change('offer')
  }
}
</script>
<template>
  <section class="panel receiving-task" aria-label="Purchase workflow">
    <header class="panel-head"><div><h2>{{ purchase.reference }}</h2><small>{{ purchase.source }}</small></div>
      <span class="tag" :class="{held: pendingLines.length}">{{ pendingLines.length ? 'Awaiting putaway' : remaining ? 'Open purchase' : 'Stored' }}</span></header>
    <div class="receiving-steps" role="group" aria-label="Receiving actions">
      <button type="button" :aria-pressed="step === 'receive'" @click="change('receive')">Receive</button>
      <button type="button" :aria-pressed="step === 'putaway'" @click="change('putaway')">Put away</button>
      <button type="button" :aria-pressed="step === 'offer'" @click="change('offer')">Prepare offer</button>
    </div>
    <div v-if="stale" class="warning revision" role="status">The purchase changed. Review its current balances before recording another action.
      <button @click="change(step)">Review current balance</button></div>
    <form class="form-body" @submit.prevent="save">
      <template v-if="step === 'receive'">
        <h3>Receive a delivery</h3>
        <p class="field-hint">{{ remaining }} of {{ purchase.orderedQuantity }} ordered units still to receive.</p>
        <fieldset v-if="remaining" class="fields" :disabled="!enabled || stale">
          <label>Delivery reference<input v-model="deliveryReference" required maxlength="64" /></label>
          <label>Units received<input v-model="counted" type="number" inputmode="numeric" min="1" :max="remaining" required /></label>
          <label>Item identification<select v-model="identity" required><option disabled value="">Choose identification</option>
            <option value="identified">Identified catalog item</option><option value="mixed">Unidentified · quarantine</option></select></label>
          <label v-if="identity === 'identified'">Received catalog item<select v-model="receiptItem" required><option disabled value="">Choose item</option>
            <option v-for="c in workspace.catalog" :key="c.id" :value="c.id">{{ label(c.id) }}</option></select></label>
          <label>Item cost basis<select v-model="basis" required><option disabled value="">Choose basis</option><option value="known">Documented</option><option value="unknown">Unknown</option></select></label>
          <label v-if="basis === 'known'">Total item cost ({{ purchase.currency }})<input v-model="amount" inputmode="decimal" required /></label>
        </fieldset>
        <p v-if="remaining" class="field-hint">Received units stay unavailable until their destination and count are confirmed.</p>
        <button v-if="remaining" class="primary" :disabled="!enabled || stale">Receive delivery</button>
      </template>
      <template v-else-if="step === 'putaway'">
        <h3>Confirm putaway</h3>
        <p v-if="!pendingLines.length" class="field-hint">No units are waiting for putaway.</p>
        <template v-else>
          <fieldset class="fields" :disabled="!enabled || stale">
            <label class="span">Delivery to put away<select v-model="deliveryId" required><option disabled value="">Choose delivery</option>
              <option v-for="d in pendingLines" :key="d.id" :value="d.id">{{ d.reference }} · {{ d.intakeQuantity }} {{ d.catalogId ? 'in intake' : 'in quarantine' }}</option></select></label>
            <label v-if="line && !line.catalogId" class="span">Identified catalog item<select :value="identifiedItem" @change="identifyMixed(($event.target as HTMLSelectElement).value)" required><option disabled value="">Choose the verified variant and condition</option>
              <option v-for="c in eligible" :key="c.id" :value="c.id">{{ label(c.id) }}</option></select></label>
            <p v-else-if="line?.catalogId" class="span field-hint">{{ label(line.catalogId) }}</p>
            <div v-if="needsReconfirmation && currentItem" class="span reconfirmation">
              <p>Received at catalog revision {{ line?.catalogRevision }}. Current revision {{ currentItem.revision }} · {{ label(currentItem.id) }}.</p>
              <label v-if="currentItem.integrationEligible"><input type="checkbox" :checked="confirmedRevision === currentItem.revision"
                @change="confirmedRevision = ($event.target as HTMLInputElement).checked ? currentItem.revision : null" />
                I checked the physical item against catalog revision {{ currentItem.revision }}.</label>
            </div>
            <label class="span">Destination bin<select v-model="target" required><option disabled value="">Choose bin</option>
              <option v-for="l in workspace.locations" :key="l.id" :value="l.id">{{ l.code }} · {{ l.name }}</option></select></label>
            <label>Confirm bin code<input v-model="scanned" required autocomplete="off" spellcheck="false" maxlength="48" /></label>
            <label>Count placed<input v-model="moved" type="number" inputmode="numeric" min="1" :max="line?.intakeQuantity" required /></label>
          </fieldset>
          <p class="field-hint">Read the physical bin label and count the units placed. Unconfirmed units stay in {{ line?.catalogId ? 'intake' : 'quarantine' }}.</p>
          <p v-if="!workspace.locations.length" class="field-hint">Register a bin in <a href="#locations">Locations</a> first.</p>
          <p v-if="!eligible.length || (line?.catalogId && !currentItem?.integrationEligible)" class="field-hint">Putaway needs an eligible item with verified identity, condition and source rights. Check the <a href="#catalog">Catalog</a>.</p>
          <button class="primary" :disabled="!enabled || stale || !verifiedIdentity || !workspace.locations.length">Confirm putaway</button>
        </template>
      </template>
      <template v-else>
        <h3>Prepare an unpublished offer</h3>
        <p v-if="!itemsToOffer.length" class="field-hint">No eligible unbacked stock is available from this purchase.</p>
        <template v-else>
          <fieldset class="fields" :disabled="!enabled || stale">
            <label class="span">Offer item<select v-model="offerItem" required><option disabled value="">Choose eligible stock</option>
              <option v-for="c in itemsToOffer" :key="c.id" :value="c.id">{{ label(c.id) }}</option></select></label>
            <label>Manual unit price ({{ purchase.currency }})<input v-model="price" required inputmode="decimal" /></label>
          </fieldset>
          <fieldset v-if="positions.length" class="backing-list" :disabled="!enabled || stale"><legend>Back from these positions</legend>
            <label v-for="p in positions" :key="p.id"><span><strong>{{ p.locationCode }}</strong> · {{ p.delivery }}<small>{{ p.freeToBackQuantity }} units available to back</small></span>
              <input v-model="quantities[p.id]" type="number" inputmode="numeric" min="0" :max="p.freeToBackQuantity" :aria-label="'Back units from ' + p.locationCode + ' · ' + p.delivery" /></label>
          </fieldset>
          <p v-if="offerItem" class="preview" aria-live="polite">{{ backed }} units of backing · {{ Math.max(0, canBack - backed) }} eligible units withheld</p>
          <p class="field-hint">This internal offer keeps the physical stock in its bins. Published quantity is zero.</p>
          <button class="primary" :disabled="!enabled || stale || !backed || backed > canBack">Save unpublished offer</button>
        </template>
      </template>
      <p v-if="issue" role="alert" class="warning">{{ issue }}</p>
    </form>
  </section>
</template>
