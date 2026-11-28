<script setup lang="ts">
import { computed } from 'vue'
import type { Catalog, Purchase, ReceivingWorkspace } from './api'
import { draftBackingLink, partOutLink } from './countLinks'
import { itemLabel, receiptCost, recordedOfferPrice } from './receiving'
import { purchasePositions } from './pooledOffers'
import { contentLabel, inspectionLink } from './setContents'
const props = defineProps<{ purchase: Purchase; stock: ReceivingWorkspace; catalog: Catalog[] }>()
const balances = computed(() => props.purchase.balances)
const layers=computed(()=>[...props.purchase.deliveries,...(props.purchase.derivedLayers ?? [])])
const purchases = computed(() => new Map(props.stock.purchases.map(p => [p.id,p])))
const positions = computed(() => new Map(props.stock.purchases.flatMap(purchase => purchasePositions(purchase).map(p =>
  [p.id, { bin:p.locationId, label:p.locationCode + ' · ' + p.delivery }] as const))))
const isSet = (id: string | null) => props.catalog.some(row => row.id === id && row.itemType === 'set')
const amount = (value: number | string | null) => receiptCost(value, props.purchase.currency)
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
          <small v-if="d.setContents">{{ contentLabel(d.setContents) }}</small>
          <small v-if="d.partOut?.parentTransformationId && d.partOut.sourcePositionId"><a :href="partOutLink(d.partOut.sourcePositionId,d.partOut.parentTransformationId)">Part-out output receipt</a> · <a :href="'#inventory/lot/'+d.id">Inspect output lot</a></small>
          <small v-if="d.partOut?.consumedQuantity">{{ d.partOut.consumedQuantity }} used in part-outs · Input cost consumed {{ amount(d.partOut.consumedItemCostMinor) }} · Remaining layer cost {{ amount(d.remainingItemCostMinor) }}</small>
          <small v-if="d.quarantinedQuantity">{{ d.quarantinedQuantity }} quarantined in bin positions</small>
          <template v-if="d.recoveryTotals?.recoveredQuantity"><small>{{ d.recoveryTotals.grossLostQuantity }} originally lost · {{ d.recoveryTotals.recoveredQuantity }} recovered · {{ d.lostQuantity }} still missing</small>
            <small>Gross loss cost {{ amount(d.recoveryTotals.grossAllocatedItemCostMinor) }} · Restored {{ amount(d.recoveryTotals.restoredItemCostMinor) }} · Remaining loss {{ amount(d.allocatedLostItemCostMinor) }} · Remaining delivery cost {{ amount(d.remainingItemCostMinor) }}</small></template>
          <small v-else-if="d.lostQuantity">{{ d.lostQuantity }} lost · Allocated item cost {{ amount(d.allocatedLostItemCostMinor) }} · Remaining delivery cost {{ amount(d.remainingItemCostMinor) }}</small>
          <ul class="bin-rows">
            <li v-if="d.intakeQuantity"><span>{{ d.catalogId ? 'Intake' : 'Quarantine' }}</span><strong>{{ d.intakeQuantity }}</strong></li>
            <li v-for="p in d.placements" :key="p.id"><span>Bin {{ p.locationCode }}<small>{{ itemLabel(catalog, p.catalogId, p.catalogRevision) }}</small>
              <small v-if="p.setContents || isSet(p.catalogId)">{{ contentLabel(p.setContents) }}
                <a v-if="!p.setContents && stock.setContentsSupported" :href="inspectionLink(p.id)">Inspect set contents</a>
              </small></span><strong>{{ p.quantity }}</strong></li>
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
          <div class="receipt-line-head"><strong>{{ recordedOfferPrice(offer) }}</strong>
            <span>{{ offer.backedQuantity }} backed{{ offer.pool ? ' by this purchase' : '' }}</span></div>
          <small>Offer {{ offer.id }}</small><small>{{ itemLabel(catalog, offer.catalogId, offer.catalogRevision) }}</small>
          <small v-if="offer.setContents || isSet(offer.catalogId)">{{ contentLabel(offer.setContents) }}</small>
          <p v-if="(offer.pool?.totalBackedQuantity ?? offer.backedQuantity) && !offer.backingCurrent" class="warning">The backing needs renewed catalog confirmation. Check the item revision before using this offer.</p>
          <template v-if="offer.pool">
            <strong>{{ offer.pool.totalBackedQuantity }} backed across {{ offer.pool.sources.length }} purchases</strong>
            <ul class="receipt-lines" aria-label="Contributing purchases"><li v-for="source in offer.pool.sources" :key="source.purchaseId">
              <a :href="'#receiving/' + source.purchaseId">{{ purchases.get(source.purchaseId)?.reference }}</a> · {{ source.backedQuantity }} backed
              <ul v-if="source.backing.length" class="bin-rows">
                <li v-for="b in source.backing" :key="b.placementId">
                  <a :href="draftBackingLink(positions.get(b.placementId)!.bin,b.placementId,'offers',offer.id)"
                    :aria-label="purchases.get(source.purchaseId)?.reference + ' · ' + positions.get(b.placementId)!.label + ' · Review draft offer ' + offer.id + ' at position ' + b.placementId">{{ positions.get(b.placementId)!.label }}</a>
                  <strong>{{ b.quantity }}</strong>
                </li>
              </ul>
            </li></ul>
          </template>
          <ul v-else class="bin-rows">
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
