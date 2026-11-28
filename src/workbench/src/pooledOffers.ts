import type { Catalog, Purchase, ReceivingWorkspace } from './api'
import { contentKey, type Contents } from './setContents'

type Offer = Purchase['offers'][number]
type Pool = NonNullable<Offer['pool']>

export function purchasePositions(purchase: Purchase) {
  return [...purchase.deliveries, ...(purchase.derivedLayers ?? [])].flatMap(delivery =>
    delivery.placements.map(position => ({ ...position, delivery: delivery.reference,
      deliveryId: delivery.id, itemCostMinor: delivery.itemCostMinor })))
}

export function distinguishPositions(rows: ReturnType<typeof purchasePositions>) {
  const counts = new Map<string, number>()
  const key = (p: typeof rows[number]) => p.locationId + '/' + p.deliveryId
  for (const p of rows) counts.set(key(p), (counts.get(key(p)) ?? 0) + 1)
  return rows.map(p => ({ ...p, duplicateLabel: counts.get(key(p))! > 1 }))
}

function sameBacking(left: Offer['backing'], right: Offer['backing']) {
  if (left.length !== right.length) return false
  const amounts = new Map(left.map(line => [line.placementId, line.quantity]))
  return amounts.size === left.length && new Set(right.map(line => line.placementId)).size === right.length &&
    right.every(line => amounts.get(line.placementId) === line.quantity)
}

function samePool(first: Pool, next: Pool) {
  if (first.originPurchaseId !== next.originPurchaseId || first.totalBackedQuantity !== next.totalBackedQuantity ||
      first.sources.length !== next.sources.length) return false
  const byPurchase = new Map(first.sources.map(source => [source.purchaseId, source]))
  return new Set(next.sources.map(source => source.purchaseId)).size === next.sources.length && next.sources.every(source => {
    const original = byPurchase.get(source.purchaseId)
    return original && original.backedQuantity === source.backedQuantity && sameBacking(original.backing, source.backing)
  })
}

export function validOfferSources(page: ReceivingWorkspace, supported: boolean, setContentsSupported: boolean) {
  const positions = new Map<string, { purchaseId: string; catalogId: string; catalogRevision: number; backedQuantity: number; setContents?: Contents }>()
  const purchases = new Set<string>()
  for (const purchase of page.purchases) {
    if (purchases.has(purchase.id)) return false
    purchases.add(purchase.id)
    if (!setContentsSupported && [...purchase.deliveries, ...(purchase.derivedLayers ?? [])].some(d => d.setContents)) return false
    for (const position of purchasePositions(purchase)) {
      if (positions.has(position.id) || (!setContentsSupported && position.setContents)) return false
      positions.set(position.id, { ...position, purchaseId: purchase.id })
    }
  }
  const originals = new Map<string, { offer: Offer; seen: Set<string> }>()
  const allocated = new Map<string, number>()
  for (const purchase of page.purchases) {
    let purchaseTotal = 0
    for (const offer of purchase.offers) {
      const pool = offer.pool
      if ((!setContentsSupported && offer.setContents) || (!supported && (pool || offer.backedQuantity > 1_000_000)) ||
          new Set(offer.backing.map(line => line.placementId)).size !== offer.backing.length) return false
      let local = 0
      for (const line of offer.backing) {
        const position = positions.get(line.placementId)
        if (!position || position.purchaseId !== purchase.id || position.catalogId !== offer.catalogId ||
            position.catalogRevision !== offer.catalogRevision ||
            ((offer.setContents || pool) && contentKey(offer.setContents) !== contentKey(position.setContents))) return false
        local += line.quantity
        allocated.set(line.placementId, (allocated.get(line.placementId) ?? 0) + line.quantity)
      }
      if (local !== offer.backedQuantity) return false
      purchaseTotal += local
      const previous = originals.get(offer.id)
      if (previous) {
        const original = previous.offer
        if (previous.seen.has(purchase.id) || !pool || !original.pool || offer.catalogId !== original.catalogId ||
            offer.catalogRevision !== original.catalogRevision || offer.currency !== original.currency ||
            offer.unitPriceMinor !== original.unitPriceMinor || offer.priceScaleVerified !== original.priceScaleVerified ||
            contentKey(offer.setContents) !== contentKey(original.setContents) ||
            offer.backingCurrent !== original.backingCurrent || !samePool(original.pool, pool)) return false
        previous.seen.add(purchase.id)
      } else {
        originals.set(offer.id, { offer, seen: new Set([purchase.id]) })
      }
      if (pool) {
        const own = pool.sources.find(source => source.purchaseId === purchase.id)
        if (!own || own.backedQuantity !== local || !sameBacking(own.backing, offer.backing)) return false
      }
    }
    if (page.complete && purchaseTotal !== purchase.balances.backedQuantity) return false
  }
  for (const { offer, seen } of originals.values()) {
    const pool = offer.pool
    if (!pool) continue
    if (!seen.has(pool.originPurchaseId) || seen.size < 2 || pool.sources.length !== seen.size ||
        new Set(pool.sources.map(source => source.purchaseId)).size !== seen.size ||
        pool.sources.some(source => !seen.has(source.purchaseId)) ||
        pool.sources.reduce((total, source) => total + source.backedQuantity, 0) !== pool.totalBackedQuantity) return false
  }
  return !page.complete || [...positions].every(([id, position]) => (allocated.get(id) ?? 0) === position.backedQuantity)
}

export function validOfferCatalog(page: ReceivingWorkspace, catalog: Catalog[]) {
  const identities = new Map(catalog.map(record => [record.id,record]))
  for (const purchase of page.purchases) {
    if (page.setContentsSupported) {
      for (const position of purchasePositions(purchase)) {
        const identity = identities.get(position.catalogId)
        if (identity?.revision !== position.catalogRevision) continue
        if (identity.itemType === 'set' ? !position.setContents && position.freeToBackQuantity > 0 : !!position.setContents) return false
      }
    }
    for (const offer of purchase.offers) {
      const identity = identities.get(offer.catalogId)
      if (identity?.revision !== offer.catalogRevision) continue
      if (offer.setContents && identity.itemType !== 'set') return false
      if (offer.pool && identity.itemType === 'set' && !offer.setContents) return false
    }
  }
  return true
}
