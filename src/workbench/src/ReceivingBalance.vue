<script setup lang="ts">
import { computed } from 'vue'
import type { Catalog, Purchase } from './api'
import { draftBackingLink, partOutLink } from './countLinks'
import { itemLabel, money } from './receiving'
const props = defineProps<{ purchase: Purchase; catalog: Catalog[] }>()
const balances = computed(() => props.purchase.balances)
const layers=computed(()=>[...props.purchase.deliveries,...(props.purchase.derivedLayers ?? [])])
const positions = computed(() => new Map(layers.value.flatMap(d => d.placements.map(p =>
  [p.id, { bin:p.locationId, label:p.locationCode + ' · ' + d.reference }] as const))))
const amount = (value: number | string | null) => money(value, props.purchase.currency)
</script>
<template>
  <aside class="side-panels receiving-balances" aria-label="Purchase readback">
    <section class="panel" role="region" aria-labelledby="balance-title">
      <header class="panel-head"><h2 id="balance-title">Stock balance</h2><span class="count">Purchase revision {{ purchase.revision }}</span></header>
      <dl class="stock-counts">
        <div><dt>Physical on hand</dt><dd>{{ balances.physicalOnHand }}</dd></div>
        <div><dt>Unavailable</dt><dd>{{ balances.unavailable }}</dd></div>
        <div><dt>Reserved</dt><dd>{{ balances.reserved }}</dd></div>
        <div><dt>Free</dt><dd>{{ balances.free }}</dd></div>
      </dl>
      <ul v-if="purchase.deliveries.length" class="receipt-lines" aria-label="Deliveries and bin positions">
        <li v-for="d in layers" :key="d.id">
          <div class="receipt-line-head"><strong>{{ d.reference }}</strong><span>{{ d.quantity }} {{ d.partOut?.parentTransformationId ? 'produced' : 'received' }}</span></div>
          <small>{{ d.catalogId ? itemLabel(catalog, d.catalogId, d.catalogRevision) : 'Mixed delivery' }} · Item cost {{ amount(d.itemCostMinor) }}</small>
          <small v-if="d.partOut?.parentTransformationId && d.partOut.sourcePositionId"><a :href="partOutLink(d.partOut.sourcePositionId,d.partOut.parentTransformationId)">Part-out output receipt</a> · <a :href="'#inventory/lot/'+d.id">Inspect output lot</a></small>
          <small v-if="d.partOut?.consumedQuantity">{{ d.partOut.consumedQuantity }} used in part-outs · Input cost consumed {{ amount(d.partOut.consumedItemCostMinor) }} · Remaining layer cost {{ amount(d.remainingItemCostMinor) }}</small>
          <small v-if="d.quarantinedQuantity">{{ d.quarantinedQuantity }} quarantined in bin positions</small>
          <template v-if="d.recoveryTotals?.recoveredQuantity"><small>{{ d.recoveryTotals.grossLostQuantity }} originally lost · {{ d.recoveryTotals.recoveredQuantity }} recovered · {{ d.lostQuantity }} still missing</small>
            <small>Gross loss cost {{ amount(d.recoveryTotals.grossAllocatedItemCostMinor) }} · Restored {{ amount(d.recoveryTotals.restoredItemCostMinor) }} · Remaining loss {{ amount(d.allocatedLostItemCostMinor) }} · Remaining delivery cost {{ amount(d.remainingItemCostMinor) }}</small></template>
          <small v-else-if="d.lostQuantity">{{ d.lostQuantity }} lost · Allocated item cost {{ amount(d.allocatedLostItemCostMinor) }} · Remaining delivery cost {{ amount(d.remainingItemCostMinor) }}</small>
          <ul class="bin-rows">
            <li v-if="d.intakeQuantity"><span>{{ d.catalogId ? 'Intake' : 'Quarantine' }}</span><strong>{{ d.intakeQuantity }}</strong></li>
            <li v-for="p in d.placements" :key="p.id"><span>Bin {{ p.locationCode }}<small>{{ itemLabel(catalog, p.catalogId, p.catalogRevision) }}</small></span><strong>{{ p.quantity }}</strong></li>
          </ul>
        </li>
      </ul>
    </section>
    <section class="panel" role="region" aria-labelledby="cost-title">
      <header class="panel-head"><h2 id="cost-title">Cost coverage</h2></header>
      <dl class="details finance-details">
        <dt>Known item costs</dt><dd>{{ amount(balances.knownItemCostMinor) }}</dd>
        <dt>Charge awaiting allocation</dt><dd>{{ amount(purchase.pendingChargeMinor) }}</dd>
        <dt>Known purchase components</dt><dd>{{ amount(balances.knownPurchaseComponentsMinor) }}</dd>
        <dt>Free units with known item cost</dt><dd>{{ balances.knownCostFreeQuantity }} of {{ balances.free }}</dd>
        <dt>Free units with unknown item cost</dt><dd>{{ balances.unknownCostFreeQuantity }}</dd>
      </dl>
    </section>
    <section class="panel" role="region" aria-labelledby="offers-title">
      <header class="panel-head"><h2 id="offers-title">Price and publication</h2><span class="tag">Internal only</span></header>
      <dl class="details finance-details">
        <dt>Backed</dt><dd>{{ balances.backedQuantity }}</dd>
        <dt>Free units withheld</dt><dd>{{ balances.withheldQuantity }}</dd>
        <dt>Published</dt><dd>{{ balances.publishedQuantity }}</dd>
      </dl>
      <ul v-if="purchase.offers.length" class="receipt-lines" aria-label="Unpublished offers">
        <li v-for="offer in purchase.offers" :key="offer.id">
          <div class="receipt-line-head"><strong>{{ money(offer.unitPriceMinor, offer.currency) }} per unit</strong><span>{{ offer.backedQuantity }} backed</span></div>
          <small>Offer {{ offer.id }}</small><small>{{ itemLabel(catalog, offer.catalogId, offer.catalogRevision) }}</small>
          <p v-if="offer.backedQuantity && !offer.backingCurrent" class="warning">The backing needs renewed catalog confirmation. Check the item revision before using this offer.</p>
          <ul class="bin-rows">
            <li v-for="b in offer.backing" :key="b.placementId">
              <a v-if="positions.has(b.placementId)" :href="draftBackingLink(positions.get(b.placementId)!.bin,b.placementId,'offers',offer.id)"
                :aria-label="positions.get(b.placementId)!.label+' · Review draft offer '+offer.id+' at position '+b.placementId">{{ positions.get(b.placementId)!.label }}</a>
              <span v-else>Position {{ b.placementId }}</span><strong>{{ b.quantity }}</strong>
            </li>
          </ul>
        </li>
      </ul>
    </section>
  </aside>
</template>
