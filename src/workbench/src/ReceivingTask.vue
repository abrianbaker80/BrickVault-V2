<script setup lang="ts">
import {capacityPreview,exceedsCapacity} from './binCapacity'
import { useBinRegistry } from './useBinRegistry'
import { computed, ref, watch } from 'vue'
import type { Dispatch, Purchase, ReceivingWorkspace, Workspace } from './api'
import { itemLabel, legacyPriceCompatible, parseMoney, receiptCost } from './receiving'
import { distinguishPositions, purchasePositions } from './pooledOffers'
import SetContentsFields from './SetContentsFields.vue'
import { contentKey, contentLabel, recordedContents, type Presence } from './setContents'

const props = defineProps<{ purchase: Purchase; stock: ReceivingWorkspace; workspace: Workspace; workspaceId: string; enabled: boolean; dispatch: Dispatch }>()
type Step = 'receive' | 'putaway' | 'offer'
const pendingLines = computed(() => props.purchase.deliveries.filter(d => d.intakeQuantity > 0))
const remaining = computed(() => props.purchase.orderedQuantity - props.purchase.deliveries.reduce((total, delivery) => total + delivery.quantity, 0))
const eligible = computed(() => props.workspace.catalog.filter(c => c.integrationEligible))
const placements = computed(() => purchasePositions(props.purchase))
const step = ref<Step>(pendingLines.value.length ? 'putaway' : remaining.value ? 'receive' : 'offer')
const baseRevision = ref(props.purchase.revision)
const stale = computed(() => props.purchase.revision !== baseRevision.value || (step.value === 'offer' && catalogChanged.value))
const issue = ref('')
const deliveryReference = ref('')
const counted = ref<number>()
const identity = ref('')
const receiptItem = ref('')
const box = ref<Presence>('')
const instructions = ref<Presence>('')
const inspected = computed(() => recordedContents(box.value, instructions.value))
const receivingSet = computed(() => identity.value === 'identified' && props.workspace.catalog.some(c => c.id === receiptItem.value && c.itemType === 'set'))
const basis = ref('')
const amount = ref('')
const deliveryId = ref(pendingLines.value[0]?.id || '')
const line = computed(() => pendingLines.value.find(d => d.id === deliveryId.value))
const identifiedItem = ref('')
const target = ref('')
const scanned = ref('')
const bins=useBinRegistry(()=>[scanned.value])
const moved = ref<number>()
const targetRow=computed(()=>bins.rows.value.get(target.value))
const tooFull=computed(()=>exceedsCapacity(targetRow.value,Number(moved.value || 0)))
const offerItem = ref('')
const offeredRevision = ref<number>()
const offerConfiguration = ref('')
const sellingCurrency = ref(props.purchase.currency)
const priceCurrency = computed(() => sellingCurrency.value.trim().toUpperCase())
const price = ref('')
const quantities = ref<Record<string, number | ''>>({})
const includeOther = ref(false)
const otherRevisions = ref<Record<string, number>>({})
const otherQuantities = ref<Record<string, number>>({})
const selectedItem = computed(() => line.value?.catalogId || identifiedItem.value)
const currentItem = computed(() => props.workspace.catalog.find(c => c.id === selectedItem.value))
const placingSet = computed(() => currentItem.value?.itemType === 'set')
const needsReconfirmation = computed(() => Boolean(line.value?.catalogId && currentItem.value &&
  currentItem.value.revision !== line.value.catalogRevision))
const confirmedRevision = ref<number | null>(null)
const verifiedIdentity = computed(() => currentItem.value?.integrationEligible &&
  ((line.value?.catalogId && !needsReconfirmation.value) || confirmedRevision.value === currentItem.value.revision))
const available = computed(() => placements.value.filter(p => p.freeToBackQuantity > 0 &&
  eligible.value.some(c => c.id === p.catalogId && c.revision === p.catalogRevision &&
    (c.itemType !== 'set' || (props.stock.setContentsSupported && p.setContents)))))
const itemsToOffer = computed(() => eligible.value.filter(c => available.value.some(p => p.catalogId === c.id)))
const offerIdentity = computed(() => eligible.value.find(c => c.id === offerItem.value))
const catalogChanged = computed(() => !!offerItem.value && offeredRevision.value !== offerIdentity.value?.revision)
const configurations = computed(() => new Map(available.value.filter(p => p.catalogId === offerItem.value && p.setContents)
  .map(p => [contentKey(p.setContents), p.setContents!])))
const promised = computed(() => configurations.value.get(offerConfiguration.value))
const positions = computed(() => distinguishPositions(available.value.filter(p => p.catalogId === offerItem.value &&
  (offerIdentity.value?.itemType !== 'set' || (promised.value && contentKey(p.setContents) === offerConfiguration.value)))))
const otherSources = computed(() => {
  const item = offerIdentity.value
  if (!props.stock.pooledOffersSupported || !item) return []
  return props.stock.purchases.filter(p => p.id !== props.purchase.id).map(purchase => ({
    purchase, positions: distinguishPositions(purchasePositions(purchase).filter(p => p.catalogId === item.id &&
      p.catalogRevision === item.revision && p.freeToBackQuantity > 0 &&
      (item.itemType !== 'set' || (promised.value && contentKey(p.setContents) === offerConfiguration.value))))
  })).filter(source => source.positions.length > 0)
})
const otherStale = computed(() => Object.entries(otherRevisions.value).some(([id, revision]) =>
  !props.stock.pooledOffersSupported || otherSources.value.find(source => source.purchase.id === id)?.purchase.revision !== revision))
const extraBacking = computed(() => otherSources.value.map(source => ({
  purchaseId: source.purchase.id, expectedRevision: otherRevisions.value[source.purchase.id],
  backing: source.positions.filter(p => otherQuantities.value[p.id] > 0)
    .map(p => ({ placementId:p.id, quantity:otherQuantities.value[p.id] }))
})).filter(source => source.backing.length > 0))
const extraUnits = computed(() => Object.values(otherQuantities.value).reduce((sum, quantity) => sum + quantity, 0))
const backed = computed(() => positions.value.reduce((total, p) => total + Number(quantities.value[p.id] || 0), 0))
const canBack = computed(() => positions.value.reduce((sum, p) => sum + p.freeToBackQuantity, 0))
const withheld = computed(() => Math.max(0, canBack.value - backed.value))
const location = computed(() => props.workspace.locations.find(l => l.id === target.value))
const label = (id: string) => itemLabel(props.workspace.catalog, id)
function clearBin() { scanned.value = ''; moved.value = undefined; issue.value = '' }
function clearPlacement() { identifiedItem.value = ''; confirmedRevision.value = null; target.value = ''; clearBin() }
watch(deliveryId, clearPlacement)
watch([deliveryId,receiptItem,identity,selectedItem,step], () => { box.value = ''; instructions.value = '' })
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
function resetOther() {
  includeOther.value = false
  otherRevisions.value = {}
  otherQuantities.value = {}
}
function setOther(purchase: Purchase, positionId: string, value: string) {
  if (otherStale.value) return
  if (value && Number(value) > 0) {
    otherQuantities.value[positionId] = Number(value)
    otherRevisions.value[purchase.id] ??= purchase.revision
  } else {
    delete otherQuantities.value[positionId]
    if (!purchasePositions(purchase).some(position => otherQuantities.value[position.id] > 0)) delete otherRevisions.value[purchase.id]
  }
}
watch(offerItem, () => { offeredRevision.value = offerIdentity.value?.revision; offerConfiguration.value = ''; quantities.value = {}; resetOther(); issue.value = '' }, {flush:'sync'})
watch(offerConfiguration, () => { quantities.value = {}; resetOther(); issue.value = '' })
watch(priceCurrency, () => { price.value = ''; issue.value = '' })
function change(next: Step) {
  step.value = next
  baseRevision.value = props.purchase.revision
  issue.value = ''
  clearPlacement()
  deliveryId.value = pendingLines.value[0]?.id || ''
  deliveryReference.value = ''; counted.value = undefined; identity.value = ''; receiptItem.value = ''; basis.value = ''; amount.value = ''
  offerItem.value = ''; sellingCurrency.value = props.purchase.currency; price.value = ''; quantities.value = {}; resetOther()
}
async function save() {
  if (!props.enabled || stale.value) return
  issue.value = ''
  const common = { commandId: crypto.randomUUID(), purchaseId: props.purchase.id, expectedRevision: baseRevision.value }
  const workspaceId = props.workspaceId
  if (step.value === 'receive') {
    if (!counted.value || !identity.value || !basis.value) return
    if (receivingSet.value && (!props.stock.setContentsSupported || !inspected.value)) return
    let itemCostMinor: number | null = null
    if (basis.value === 'known') {
      try { itemCostMinor = parseMoney(amount.value, props.purchase.currency) }
      catch (error) { issue.value = (error as Error).message; return }
    }
    const result = await props.dispatch({ kind: 'delivery', workspaceId, body: { ...common,
      reference: deliveryReference.value.trim(), quantity: counted.value,
      catalogId: identity.value === 'identified' ? receiptItem.value : null, itemCostMinor,
      ...(receivingSet.value ? {setContents:inspected.value} : {}) } })
    if (result) { change('putaway'); deliveryId.value = result.entityId }
  } else if (step.value === 'putaway') {
    if (!line.value || !moved.value || !location.value || !verifiedIdentity.value || tooFull.value) return
    if (placingSet.value && (!props.stock.setContentsSupported || (!line.value.setContents && !inspected.value))) return
    if (!bins.isActive(location.value.id) || !bins.matches(location.value,scanned.value)) {
      issue.value = 'Bin code does not match the selected destination. Check the physical label before confirming.'
      return
    }
    const result = await props.dispatch({ kind: 'putaway', workspaceId, body: { ...common,
      deliveryId: line.value.id, catalogId: selectedItem.value, locationId: target.value,
      ...(confirmedRevision.value !== null ? { confirmedCatalogRevision: confirmedRevision.value } : {}),
      confirmedLocationCode: scanned.value, quantity: moved.value,
      ...(placingSet.value ? {setContents:line.value.setContents ?? inspected.value} : {}) } })
    if (result) change(remaining.value ? 'receive' : pendingLines.value.length ? 'putaway' : 'offer')
  } else {
    if (!offerItem.value || !backed.value) { issue.value = 'Enter the quantity to back from at least one position.'; return }
    let unitPriceMinor: number
    try { unitPriceMinor = parseMoney(price.value, priceCurrency.value) }
    catch (error) { issue.value = (error as Error).message; return }
    if (unitPriceMinor <= 0) { issue.value = 'Enter a positive manual price.'; return }
    const backing = positions.value.filter(p => Number(quantities.value[p.id]) > 0)
      .map(p => ({ placementId: p.id, quantity: Number(quantities.value[p.id]) }))
    if (otherStale.value || !offerIdentity.value || (offerIdentity.value.itemType === 'set' && !promised.value)) return
    const sources = extraBacking.value
    if (backing.length + sources.reduce((sum, source) => sum + source.backing.length, 0) > 64) {
      issue.value = 'Choose up to 64 positions for one offer.'
      return
    }
    const result = sources.length ? await props.dispatch({ kind:'pooled-offer', workspaceId, body:{
      ...common, catalogId:offerItem.value, expectedCatalogRevision:offerIdentity.value.revision, unitPriceMinor,
      currency:priceCurrency.value, backing, additionalSources:sources, ...(promised.value ? {setContents:promised.value} : {})
    } }) : await props.dispatch({ kind: 'offer', workspaceId, body: {
      ...common, catalogId: offerItem.value, unitPriceMinor, backing, ...(promised.value ? {setContents:promised.value} : {}),
      ...(priceCurrency.value === props.purchase.currency && legacyPriceCompatible(priceCurrency.value)
        ? {} : { currency: priceCurrency.value })
    } })
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
    <div v-if="stale" class="warning revision" role="status">{{ catalogChanged && step === 'offer' ? 'The selected catalog identity changed. Review the current item and stock.' : 'The purchase changed. Review its current balances before recording another action.' }}
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
          <template v-if="receivingSet">
            <SetContentsFields v-model:box="box" v-model:instructions="instructions" />
            <p class="span field-hint">Record sets with different box or instruction contents as separate deliveries.</p>
          </template>
          <label>Item cost basis<select v-model="basis" required><option disabled value="">Choose basis</option><option value="known">Documented</option><option value="unknown">Unknown</option></select></label>
          <label v-if="basis === 'known'">Total item cost ({{ purchase.currency }})<input v-model="amount" inputmode="decimal" required /></label>
        </fieldset>
        <p v-if="remaining" class="field-hint">Received units stay unavailable until their destination and count are confirmed.</p>
        <p v-if="receivingSet && !stock.setContentsSupported" class="warning">Update the server to record this set’s box and instructions.</p>
        <button v-if="remaining" class="primary" :disabled="!enabled || stale || (receivingSet && !stock.setContentsSupported)">Receive delivery</button>
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
            <template v-if="placingSet && line">
              <p v-if="line.setContents" class="span field-hint">{{ contentLabel(line.setContents) }}</p>
              <template v-else><SetContentsFields v-model:box="box" v-model:instructions="instructions" />
                <p class="span field-hint">Count only sets with these contents into this position.</p></template>
              <p v-if="!stock.setContentsSupported" class="span warning">Update the server to record this set’s box and instructions.</p>
            </template>
            <div v-if="needsReconfirmation && currentItem" class="span reconfirmation">
              <p>Received at catalog revision {{ line?.catalogRevision }}. Current revision {{ currentItem.revision }} · {{ label(currentItem.id) }}.</p>
              <label v-if="currentItem.integrationEligible"><input type="checkbox" :checked="confirmedRevision === currentItem.revision"
                @change="confirmedRevision = ($event.target as HTMLInputElement).checked ? currentItem.revision : null" />
                I checked the physical item against catalog revision {{ currentItem.revision }}.</label>
            </div>
            <label class="span">Destination bin<select v-model="target" required><option disabled value="">Choose bin</option>
              <option v-for="l in bins.destinations.value" :key="l.id" :value="l.id">{{ l.code }} · {{ l.name }}</option></select></label>
            <label>Confirm bin code<input v-model="scanned" required autocomplete="off" spellcheck="false" maxlength="48" /></label>
            <label>Count placed<input v-model="moved" type="number" inputmode="numeric" min="1" :max="line?.intakeQuantity" required /></label>
          </fieldset>
          <p class="field-hint">Read the physical bin label and count the units placed. Unconfirmed units stay in {{ line?.catalogId ? 'intake' : 'quarantine' }}.</p>
          <p v-if="target && bins.confirmed.value" :class="tooFull?'warning':'field-hint'" role="status">{{ capacityPreview(targetRow,Math.max(0,Number(moved || 0))) }}</p>
          <p v-if="target && bins.rows.value.get(target)?.archived" class="warning">The selected destination is archived. Choose an active bin.</p>
          <p v-if="bins.confirmed.value && !bins.destinations.value.length" class="field-hint">Register a bin in <a href="#locations">Locations</a> first.</p>
          <p v-if="!eligible.length || (line?.catalogId && !currentItem?.integrationEligible)" class="field-hint">Putaway needs an eligible item with verified identity, condition and source rights. Check the <a href="#catalog">Catalog</a>.</p>
          <button class="primary" :disabled="!enabled || stale || tooFull || !verifiedIdentity || (placingSet && !stock.setContentsSupported) || (!bins.confirmed.value || !bins.destinations.value.length || (!!target && !bins.isActive(target)) || bins.pending(scanned))">Confirm putaway</button>
        </template>
      </template>
      <template v-else>
        <h3>Prepare an unpublished offer</h3>
        <p v-if="!itemsToOffer.length" class="field-hint">No eligible unbacked stock is available from this purchase.</p>
        <template v-else>
          <fieldset class="fields" :disabled="!enabled || stale">
            <label class="span">Offer item<select v-model="offerItem" required><option disabled value="">Choose eligible stock</option>
              <option v-for="c in itemsToOffer" :key="c.id" :value="c.id">{{ label(c.id) }}</option></select></label>
            <label v-if="offerIdentity?.itemType === 'set'" class="span">Set contents for this offer<select v-model="offerConfiguration" required>
              <option disabled value="">Choose the contents offered to the buyer</option>
              <option v-for="[key, contents] in configurations" :key="key" :value="key">{{ contentLabel(contents) }}</option>
            </select></label>
            <label>Selling currency<input v-model="sellingCurrency" pattern="[A-Za-z]{3}" maxlength="3" required
              aria-describedby="selling-currency-hint" autocapitalize="characters" spellcheck="false" @change="sellingCurrency = priceCurrency" /></label>
            <label>Manual unit price{{ priceCurrency ? ' (' + priceCurrency + ')' : '' }}<input v-model="price" required inputmode="decimal" /></label>
            <p id="selling-currency-hint" class="span field-hint">Use a three-letter currency code. Changing the currency clears the price.</p>
          </fieldset>
          <fieldset v-if="positions.length" class="backing-list" :disabled="!enabled || stale"><legend>Back from these positions</legend>
            <label v-for="p in positions" :key="p.id"><span><strong>{{ p.locationCode }}</strong> · {{ p.delivery }}<small>{{ p.freeToBackQuantity }} units available to back</small>
              <small v-if="includeOther">Whole delivery cost {{ receiptCost(p.itemCostMinor,purchase.currency) }}{{ p.itemCostMinor === null ? ' ('+purchase.currency+')' : '' }}</small>
              <small v-if="p.duplicateLabel">Position {{ p.id }}</small></span>
              <input v-model="quantities[p.id]" type="number" inputmode="numeric" min="0" :max="p.freeToBackQuantity" :aria-label="'Back units from ' + p.locationCode + ' · ' + p.delivery + (p.duplicateLabel ? ' · Position ' + p.id : '')" /></label>
          </fieldset>
          <template v-if="offerItem && (otherSources.length || includeOther)">
            <button type="button" :aria-expanded="includeOther" :disabled="!enabled || stale"
              @click="includeOther ? resetOther() : includeOther = true">{{ includeOther ? 'Remove other purchases' : 'Include stock from other purchases' }}</button>
            <template v-if="includeOther">
              <p v-if="otherStale" role="status" class="warning">A contributing purchase changed. Review its current stock before using it.
                <button type="button" @click="resetOther()">Review contributing purchases</button></p>
              <fieldset v-for="source in otherSources" :key="source.purchase.id" class="backing-list" :disabled="!enabled || stale || otherStale">
                <legend>{{ source.purchase.reference }}</legend>
                <label v-for="p in source.positions" :key="p.id"><span><strong>{{ p.locationCode }}</strong> · {{ p.delivery }}
                  <small>{{ p.freeToBackQuantity }} units available to back</small>
                  <small>Whole delivery cost {{ receiptCost(p.itemCostMinor,source.purchase.currency) }}{{ p.itemCostMinor === null ? ' ('+source.purchase.currency+')' : '' }}</small>
                  <small v-if="p.duplicateLabel">Position {{ p.id }}</small></span>
                  <input type="number" inputmode="numeric" min="0" :max="p.freeToBackQuantity" :value="otherQuantities[p.id] ?? ''"
                    :aria-label="'Back units from ' + source.purchase.reference + ' · ' + p.locationCode + ' · ' + p.delivery + (p.duplicateLabel ? ' · Position ' + p.id : '')"
                    @input="setOther(source.purchase,p.id,($event.target as HTMLInputElement).value)" /></label>
              </fieldset>
            </template>
          </template>
          <p v-if="offerItem && !extraUnits" class="preview" aria-live="polite">{{ backed }} {{ backed === 1 ? 'unit' : 'units' }} of backing · {{ withheld }} eligible {{ withheld === 1 ? 'unit' : 'units' }} withheld</p>
          <p v-if="extraUnits" class="preview" aria-live="polite">{{ backed + extraUnits }} units across {{ extraBacking.length + 1 }} purchases · {{ backed }} from {{ purchase.reference }}</p>
          <p class="field-hint">This internal offer keeps the physical stock in its bins. Published quantity is zero.</p>
          <button class="primary" :disabled="!enabled || stale || otherStale || !backed || backed > canBack">Save unpublished offer</button>
        </template>
      </template>
      <p v-if="issue" role="alert" class="warning">{{ issue }}</p>
    </form>
  </section>
</template>
