import type { components, operations } from './schema'
import * as valid from './validators.cjs'

type Schema = components['schemas']
export type Session = Schema['Session']
export type Workspace = Schema['Workspace']
export type Catalog = Schema['CatalogRow']
export type Location = Schema['LocationRow']
export type Diagnostic = Schema['Diagnostic']
export type CatalogInput = Omit<Schema['CatalogCommand'], 'commandId' | 'id' | 'expectedRevision'>
export type LocationInput = Pick<Schema['LocationCommand'], 'code' | 'name'>
export type RecountPreviewRequest = Schema['RecountPreviewRequest']
export type RecountTarget = Schema['RecountTarget']
export type RecountPreviewPage = Schema['RecountPreviewPage']
export type RecountBatchPage = Schema['RecountBatchPage']
export type RecountQueuePage = Schema['RecountQueuePage']
export type RecountBatchList = Schema['RecountBatchList']
export type StockPage = Schema['StockRegisterPage']
export type PositionMovementPage = Schema['PositionMovementPage']
export type PositionTimelinePage = Schema['StockPositionTimeline']
export type TimelineEvent = Schema['StockPositionTimelineEvent']
export type ReceiptLot = Schema['StockReceiptLot']
export type StockPosition = Schema['StockRegisterRow']
export type StockQuery = NonNullable<operations['searchStockPositions']['parameters']['query']>
export type BinTransferSnapshot = Schema['BinTransferSnapshot']
export type CountSnapshot = Schema['BinCountSnapshot']
export type CountPosition = Schema['CountPosition']
export type CountObservation = Schema['StockCountObservation']
export type TransferPosition = BinTransferSnapshot['positions'][number]
export type TransferCommand = Schema['TransferCommand']
export type TransferControls = { enabled: boolean; pending: boolean; feedback: string; workspaceId: string; dispatch: Dispatch }
export type ReceivingWorkspace = Schema['LossReceivingWorkspace']
export type Purchase = ReceivingWorkspace['purchases'][number]
export type QuarantineSnapshot = Schema['StockQuarantineSnapshot']
export type QuarantineEvidence = Schema['StockQuarantineEvidence']
export type DraftBackingPage = Schema['DraftBackingSnapshot']
export type DraftOfferHistory = Schema['DraftBackingHistory']
export type WithdrawalEvidence = Schema['DraftBackingWithdrawalEvidence']
export type RecoveryReview = Schema['StockLossRecoveryReview']
export type RecoveryEvidence = Schema['StockLossRecoveryEvidence']
export type RecoveryCommand = Schema['StockLossRecoveryCommand']
export type StockLoss = Schema['StockLossSnapshot']
export type StockLossCommand = Schema['StockLossCommand']
export type ReceivingCommand = { workspaceId: string } & (
  | { kind: 'quarantine'; body: Schema['StockQuarantineCommand'] }
  | { kind: 'quarantine-release'; body: Schema['StockQuarantineReleaseCommand'] }
  | { kind: 'offer-withdrawal'; body: Schema['DraftBackingWithdrawalCommand'] }
  | { kind: 'recount'; body: Schema['RecountCommand'] }
  | { kind: 'purchase'; body: Schema['PurchaseCommand'] }
  | { kind: 'delivery'; body: Schema['DeliveryCommand'] }
  | { kind: 'putaway'; body: Schema['PutawayCommand'] }
  | { kind: 'offer'; body: Schema['OfferCommand'] }
  | { kind: 'transfer'; body: TransferCommand }
  | { kind: 'count'; body: Schema['StockCountCommand'] }
  | { kind: 'stock-loss'; body: StockLossCommand }
  | { kind: 'stock-loss-recovery'; body: RecoveryCommand }
  | { kind: 'shortage-closure'; body: Schema['StockShortageClosureCommand'] }
  | { kind: 'hold'; body: Schema['StockHoldCommand'] }
  | { kind: 'hold-release'; locationId: string; positionId: string; body: Schema['StockHoldReleaseCommand'] })
export type Command = ReceivingCommand | { workspaceId: string } & (
  | { kind: 'catalog'; body: Schema['CatalogCommand'] }
  | { kind: 'location'; body: Schema['LocationCommand'] })
export type Held = { workspaceId: string | null; commandId: string }
export type Dispatch = (command: ReceivingCommand) => Promise<Schema['Receipt'] | Schema['ReceivingReceipt'] | Schema['TransferReceipt'] | Schema['StockCountReceipt'] | Schema['StockHoldReceipt'] | Schema['StockLossReceipt'] | Schema['RecountReceipt'] | Schema['StockQuarantineReceipt'] | Schema['DraftBackingWithdrawalReceipt'] | Schema['StockShortageClosureReceipt'] | Schema['StockLossRecoveryReceipt'] | undefined>

export function commandLabel(command: Command) {
  switch (command.kind) {
    case 'catalog': case 'location': return command.body.name
    case 'quarantine': return 'Quarantine of ' + command.body.quantity + ' in ' + command.body.confirmedLocationCode
    case 'quarantine-release': return 'Inspection release of ' + command.body.quantity + ' in ' + command.body.confirmedLocationCode
    case 'offer-withdrawal': return 'Draft backing withdrawal in ' + command.body.confirmedLocationCode
    case 'recount': return 'Recount batch'
    case 'purchase': return 'Purchase ' + command.body.reference
    case 'delivery': return 'Delivery ' + command.body.reference
    case 'putaway': return 'Putaway to ' + command.body.confirmedLocationCode
    case 'offer': return 'Unpublished offer'
    case 'hold': return 'Build hold for ' + command.body.purpose
    case 'hold-release': return 'Release of ' + command.body.quantity + (command.body.quantity === 1 ? ' held unit' : ' held units')
    case 'shortage-closure': return 'Shortage closure in ' + command.body.confirmedLocationCode
    case 'stock-loss-recovery': return 'Inspected loss recovery in ' + command.body.confirmedLocationCode
    case 'stock-loss': return 'Stock loss in ' + command.body.confirmedLocationCode
    case 'count': return 'Stock count of ' + command.body.countedQuantity + ' in ' + command.body.confirmedLocationCode
    case 'transfer': return 'Transfer of ' + command.body.quantity + ' from ' + command.body.confirmedSourceLocationCode + ' to ' + command.body.confirmedDestinationLocationCode
  }
}

const commands = {
  quarantine: { path: 'inventory/quarantines', valid: valid.StockQuarantineCommand },
  'quarantine-release': { path: 'inventory/quarantine-releases', valid: valid.StockQuarantineReleaseCommand },
  'offer-withdrawal': { path: 'inventory/draft-backing-withdrawals', valid: valid.DraftBackingWithdrawalCommand },
  recount: { path: 'inventory/recounts/batches', valid: valid.RecountCommand },
  catalog: { path: 'catalog', valid: valid.CatalogCommand },
  location: { path: 'locations', valid: valid.LocationCommand },
  purchase: { path: 'receiving/purchases', valid: valid.PurchaseCommand },
  delivery: { path: 'receiving/deliveries', valid: valid.DeliveryCommand },
  putaway: { path: 'receiving/putaway', valid: valid.PutawayCommand },
  offer: { path: 'receiving/offers', valid: valid.OfferCommand },
  'shortage-closure': { path:'receiving/shortage-closures', valid: valid.StockShortageClosureCommand },
  'stock-loss-recovery': { path:'receiving/stock-loss-recoveries',valid:valid.StockLossRecoveryCommand },
  'stock-loss': { path: 'receiving/stock-losses', valid: valid.StockLossCommand },
  count: { path: 'receiving/counts', valid: valid.StockCountCommand },
  hold: { path: 'receiving/holds', valid: valid.StockHoldCommand },
  'hold-release': { path: 'receiving/holds/release', valid: valid.StockHoldReleaseCommand },
  transfer: { path: 'receiving/transfers', valid: valid.TransferCommand }
}

export class ApiFailure extends Error {
  constructor(readonly status: number, readonly detail?: Schema['Error']) {
    super(detail?.message || 'The server could not complete the request.')
  }
}

export class ContractFailure extends Error {
  constructor() { super('This Workbench could not verify the server response. Check the application version.') }
}

export class InvalidCommand extends Error {}

async function request(path: string, options: RequestInit = {}) {
  if (window.location.protocol !== 'https:') throw new Error('Open the private HTTPS address to connect.')
  const response = await fetch('/api/v1/' + path, {
    ...options, cache: 'no-store', mode: 'same-origin', credentials: 'same-origin', redirect: 'error',
    signal: AbortSignal.timeout(15000),
    headers: { Accept: 'application/json', ...options.headers }
  })
  if (!response.ok) {
    const body: unknown = await response.json().catch(() => null)
    throw new ApiFailure(response.status, valid.Error(body) ? body : undefined)
  }
  return response
}

async function read<T>(path: string, guard: (value: unknown) => value is T, options?: RequestInit) {
  const response = await request(path, options)
  const data: unknown = await response.json().catch(() => null)
  if (!guard(data)) throw new ContractFailure()
  return data
}

function recountCursor(after: string) {
  const params = new URLSearchParams({limit: '25'})
  if (after) params.set('after', after)
  return '?' + params
}
function checkedPreview(page: RecountPreviewPage, input: RecountPreviewRequest) {
  if (page.previewId !== input.previewId || page.mode !== input.scope || page.locationId !== input.locationId ||
      page.q !== input.q || page.itemType !== input.itemType) throw new ContractFailure()
  if (input.scope === 'selected') {
    const ids = new Set(input.positionIds)
    if ((page.complete && page.total !== ids.size) || page.rows.some(row => !ids.has(row.positionId))) throw new ContractFailure()
  }
  return page
}

function invalidTimelineEvent(event: TimelineEvent) {
  if (event.kind==='stock-loss-recovery') return !event.postingId || event.quantity < 1 || event.physicalDelta !== event.quantity ||
    event.reservedDelta !== 0 || event.offerBackedDelta !== 0 || (event.quarantineDelta ?? 0) < 0 || (event.quarantineDelta ?? 0) > event.quantity
  if (event.kind==='shortage-closure') return !event.caseId || !event.observationId || event.quantity!==event.ledgerQuantityAtWitness ||
    event.physicalDelta!==0 || event.reservedDelta!==0 || event.offerBackedDelta!==0
  return event.kind === 'offer-withdrawal' && (!event.offerId || event.physicalDelta !== 0 || event.reservedDelta !== 0 ||
    event.offerBackedDelta !== -event.quantity || event.quantity < 1)
}

function verifyDraftBalance(page: DraftBackingPage | DraftOfferHistory, bin: string, position: string) {
  const layer = page.position.layer
  if (page.position.location.id !== bin || layer.placementId !== position || (page.complete &&
      (page.physicalOnHand !== layer.quantity || page.offerBackedQuantity !== layer.backedQuantity ||
       page.unavailableQuantity === undefined || page.unavailableQuantity !== layer.quarantinedQuantity ||
       page.heldQuantity + page.unavailableQuantity + page.sellableQuantity !== page.physicalOnHand ||
       page.withheldQuantity + page.offerBackedQuantity !== page.sellableQuantity ||
       page.freeToBackQuantity !== layer.freeToBackQuantity || page.freeToBackQuantity > page.withheldQuantity))) throw new ContractFailure()
  const offers = 'offers' in page ? page.offers : [page.offer]
  if (offers.some(o => o.totalOfferBackedQuantity < o.backedQuantity || o.backedQuantity > page.offerBackedQuantity ||
      (o.canWithdraw && (page.hasOpenShortage || o.authority !== 'internal-unpublished' || !o.backedQuantity)))) throw new ContractFailure()
}
function verifyRecoveryTotals(row: {lostQuantity:number; allocatedLostItemCostMinor:number|null; recoveryTotals?:Schema['DeliveryRecoveryTotals']}) {
  const totals=row.recoveryTotals
  if (!totals || totals.grossLostQuantity-totals.recoveredQuantity!==row.lostQuantity) throw new ContractFailure()
  const gross=totals.grossAllocatedItemCostMinor, restored=totals.restoredItemCostMinor
  if (row.allocatedLostItemCostMinor===null ? gross!==null || restored!==null :
      gross===null || restored===null || BigInt(gross)-BigInt(restored)!==BigInt(row.allocatedLostItemCostMinor)) throw new ContractFailure()
}
function recoveryPath(bin:string,position:string,posting:string) {
  return 'receiving/bins/'+encodeURIComponent(bin)+'/positions/'+encodeURIComponent(position)+'/posted-loss/'+encodeURIComponent(posting)
}
function costConserved(original:number|null, restored:number|null, remaining:number|null) {
  return original===null ? restored===null && remaining===null :
    restored!==null && remaining!==null && original===restored+remaining
}
const draftPath = (position: string) => 'inventory/positions/' + encodeURIComponent(position) + '/draft-backing'
export const api = {
  session: () => read('session', valid.Session),
  signIn(password: string) {
    const bytes = new TextEncoder().encode('operator:' + password)
    const basic = btoa(Array.from(bytes, byte => String.fromCharCode(byte)).join(''))
    return read('session', valid.Session, { method: 'POST', headers: { Authorization: 'Basic ' + basic } })
  },
  async signOut(csrf: string) {
    await request('session', { method: 'DELETE', headers: { 'X-CSRF-Token': csrf } })
  },
  workspace: (allAudit = false) => read(allAudit ? 'workspace?audit=all' : 'workspace', valid.Workspace),
  positions(query: StockQuery) {
    const params = new URLSearchParams({ quarantine: 'true' })
    for (const [key, value] of Object.entries(query))
      if (value !== undefined && value !== '') params.set(key, String(value))
    return read('inventory/positions?' + params, valid.StockRegisterPage)
  },
  positionMovements(id: string, after: string) {
    const query = new URLSearchParams({ limit: '25' })
    if (after) query.set('after', after)
    return read('inventory/positions/' + encodeURIComponent(id) + '/movements?' + query, valid.PositionMovementPage)
  },
  async receiptLot(id: string, positionAfter: string, eventAfter: string) {
    const query = new URLSearchParams({ positionLimit: '25', eventLimit: '25', quarantine: 'true', withdrawals: 'true', closures:'true', recoveries:'true' })
    if (positionAfter) query.set('positionAfter', positionAfter)
    if (eventAfter) query.set('eventAfter', eventAfter)
    const lot = await read('inventory/receipt-lots/' + encodeURIComponent(id) + '?' + query, valid.StockReceiptLot)
    if (lot.deliveryId !== id || lot.positions.some(p => p.layer.deliveryId !== id || p.layer.purchaseId !== lot.purchaseId))
      throw new ContractFailure()
    if (lot.complete) verifyRecoveryTotals(lot)
    if (lot.complete && (lot.receivedQuantity !== lot.unplacedQuantity + lot.positionedQuantity + lot.lostQuantity ||
        lot.currentOnHandQuantity !== lot.unplacedQuantity + lot.positionedQuantity ||
        lot.positionCount < lot.positions.length || lot.heldQuantity + lot.offerBackedQuantity > lot.positionedQuantity ||
        lot.freeToBackQuantity + lot.heldQuantity + lot.offerBackedQuantity > lot.positionedQuantity ||
        (lot.itemCostMinor === null
          ? lot.allocatedLostItemCostMinor !== null || lot.remainingItemCostMinor !== null
          : lot.allocatedLostItemCostMinor === null || lot.remainingItemCostMinor === null ||
            lot.itemCostMinor !== lot.allocatedLostItemCostMinor + lot.remainingItemCostMinor)))
      throw new ContractFailure()
    for (const row of lot.events) {
      const event = row.event
      if (invalidTimelineEvent(event) || event.kind === 'transfer-in' ||
          (event.kind === 'transfer-out' && (event.id !== event.relatedPositionId || event.relatedPositionId === row.positionId ||
            !event.relatedLocationId || !event.relatedLocationCode || !event.locationCodeAtEvent ||
            event.physicalDelta !== 0 || event.offerBackedDelta !== 0 || row.transferBackedQuantity > event.quantity)) ||
          (event.kind !== 'transfer-out' && row.transferBackedQuantity !== 0) ||
          (event.kind === 'count' && event.id !== event.observationId) ||
          (event.kind === 'stock-loss' && event.id !== event.postingId)) throw new ContractFailure()
    }
    return lot
  },
  async positionTimeline(id: string, after: string, previous?: StockPosition) {
    const query = new URLSearchParams({ limit: '25', quarantine: 'true', withdrawals: 'true', closures:'true', recoveries:'true' })
    if (after) query.set('after', after)
    const page = await read('inventory/positions/' + encodeURIComponent(id) + '/timeline?' + query, valid.StockPositionTimeline)
    const position = page.position
    if (page.positionId !== id || position.layer.placementId !== id || page.originPlacementId !== position.originPlacementId ||
        page.purchaseId !== position.layer.purchaseId || page.deliveryId !== position.layer.deliveryId ||
        page.location.id !== position.location.id || page.location.revision !== position.location.revision ||
        page.location.code !== position.location.code || page.location.name !== position.location.name ||
        position.layer.catalogId !== position.layer.currentCatalog.id) throw new ContractFailure()
    if (previous && (position.originPlacementId !== previous.originPlacementId || position.location.id !== previous.location.id ||
        position.layer.purchaseId !== previous.layer.purchaseId || position.layer.deliveryId !== previous.layer.deliveryId ||
        position.layer.catalogId !== previous.layer.catalogId || position.location.revision < previous.location.revision)) throw new ContractFailure()
    for (const event of page.events) {
      if (invalidTimelineEvent(event) || event.locationId !== page.location.id ||
          (event.kind === 'count' && (event.id !== event.observationId || event.ledgerQuantityAtWitness === null)) ||
          (event.kind === 'stock-loss' && event.id !== event.postingId)) throw new ContractFailure()
      if (event.kind === 'transfer-in' || event.kind === 'transfer-out') {
        const destinationId = event.kind === 'transfer-in' ? id : event.relatedPositionId
        if (!event.relatedPositionId || event.relatedPositionId === id || !event.relatedLocationCode ||
            !event.locationCodeAtEvent || !event.relatedLocationId || event.relatedLocationId === page.location.id ||
            event.id !== destinationId) throw new ContractFailure()
      } else if (event.relatedPositionId) throw new ContractFailure()
    }
    return page
  },
  async quarantine(bin: string, position: string, after = '') {
    const result = await read('inventory/positions/' + encodeURIComponent(position) + '/quarantine' + recountCursor(after) + '&lossAttribution=true&recoveries=true', valid.StockQuarantineSnapshot)
    const layer = result.position.layer
    if (result.position.location.id !== bin || layer.placementId !== position || layer.currentCatalog.id !== layer.catalogId ||
        (result.complete && (result.onHandQuantity !== layer.quantity || result.offerBackedQuantity !== layer.backedQuantity ||
          result.sellableQuantity + result.heldQuantity + result.quarantinedQuantity !== result.onHandQuantity ||
          result.offerBackedQuantity > result.sellableQuantity ||
          result.availableToQuarantineQuantity > result.sellableQuantity - result.offerBackedQuantity ||
          result.quarantines.some(q => q.lostQuantity === undefined || q.quantity !== q.remainingQuantity + q.releasedQuantity + q.lostQuantity)))) throw new ContractFailure()
    return result
  },
  async quarantineReceipt(bin: string, position: string, selector: { receiptId:string } | { commandId:string }) {
    const result = await read('inventory/positions/' + encodeURIComponent(position) + '/quarantine/receipts?' +
      new URLSearchParams({...selector,recoveries:'true'}), valid.StockQuarantineEvidence)
    if (result.positionId !== position || result.location.id !== bin || result.recordedCatalog.id !== result.catalogId ||
        result.recordedCatalog.revision !== result.catalogRevision || ('receiptId' in selector ? selector.receiptId !== result.id : selector.commandId !== result.commandId) ||
        (result.kind === 'quarantine' ? result.id !== result.quarantineId || result.inspectionResult !== null :
          result.id === result.quarantineId || result.inspectionResult !== 'passed')) throw new ContractFailure()
    return result
  },
  async draftBacking(bin: string, position: string, after: string) {
    const result = await read(draftPath(position) + recountCursor(after) + '&quarantine=true', valid.DraftBackingSnapshot)
    verifyDraftBalance(result,bin,position)
    return result
  },
  async draftOfferHistory(bin: string, position: string, offer: string, after: string) {
    const result = await read(draftPath(position) + '/' + encodeURIComponent(offer) + '/history' + recountCursor(after) + '&quarantine=true', valid.DraftBackingHistory)
    verifyDraftBalance(result,bin,position)
    if (result.offer.offerId !== offer) throw new ContractFailure()
    return result
  },
  async withdrawalReceipt(bin: string, position: string, selector: { receiptId:string } | { commandId:string }) {
    const result = await read(draftPath(position) + '/receipts?' + new URLSearchParams(selector), valid.DraftBackingWithdrawalEvidence)
    if (result.positionId !== position || result.location.id !== bin ||
        ('receiptId' in selector ? result.id !== selector.receiptId : result.commandId !== selector.commandId) ||
        result.quantity !== result.priorBackedQuantity - result.remainingBackedQuantity ||
        result.quantity !== result.priorTotalOfferBackedQuantity - result.remainingTotalOfferBackedQuantity ||
        result.remainingBackedQuantity > result.remainingTotalOfferBackedQuantity) throw new ContractFailure()
    return result
  },
  async freezeRecount(input: RecountPreviewRequest, workspaceId: string, csrf: string) {
    const page = await read('inventory/recounts/previews', valid.RecountPreviewPage, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Workspace-Id': workspaceId },
      body: JSON.stringify(input)
    })
    return checkedPreview(page, input)
  },
  async recountPreview(input: RecountPreviewRequest, after: string) {
    const page = await read('inventory/recounts/previews/' + encodeURIComponent(input.previewId) + recountCursor(after), valid.RecountPreviewPage)
    return checkedPreview(page, input)
  },
  async recountBatch(id: string, after = '') {
    const page = await read('inventory/recounts/batches/' + encodeURIComponent(id) + recountCursor(after), valid.RecountBatchPage)
    if (page.batchId !== id || page.total !== page.queued + page.changed + page.alreadyQueued + page.unavailable ||
        page.rows.some(r => (r.outcome === 'queued' && r.queueBatchId !== id) ||
          (r.outcome === 'already-queued' && (!r.queueBatchId || r.queueBatchId === id)) ||
          (Boolean(r.queueBatchId) !== Boolean(r.taskState)) ||
          ((r.outcome === 'changed' || r.outcome === 'unavailable') && r.taskState !== null) ||
          (Boolean(r.observationId) !== (r.taskState === 'witnessed')))) throw new ContractFailure()
    return page
  },
  recountQueue: (after: string) => read('inventory/recounts/queue' + recountCursor(after), valid.RecountQueuePage),
  recountBatches: (after: string) => read('inventory/recounts/batches' + recountCursor(after), valid.RecountBatchList),
  async adjustmentReview(binId: string, positionId: string, caseId: string, after = '') {
    const params = new URLSearchParams({ limit: '25', quarantine:'true' })
    if (after) params.set('after', after)
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/positions/' +
      encodeURIComponent(positionId) + '/shortages/' + encodeURIComponent(caseId) + '/adjustment-review?' + params, valid.StockAdjustmentReview)
    const needsAttribution = (result.quarantinedQuantity ?? 0) > 0 && result.candidateLossQuantity > 0
    if (result.location.id !== binId || result.positionId !== positionId || result.caseId !== caseId ||
        (result.complete && (result.quarantinedQuantity === undefined || result.quarantineBlocksLoss === undefined ||
          (result.offerCapacityAfterLoss === null) !== needsAttribution ||
          (result.minimumOfferBackingReduction === null) !== needsAttribution)) ||
        result.latestObservation.positionId !== positionId || result.latestObservation.purchaseId !== result.purchaseId ||
        result.latestObservation.locationId !== binId) throw new ContractFailure()
    return result
  },
  async recoveryReview(bin:string,position:string,posting:string,after='',historyAfter='') {
    const query=new URLSearchParams({limit:'25',historyLimit:'25'})
    if (after) query.set('after',after)
    if (historyAfter) query.set('historyAfter',historyAfter)
    const page=await read(recoveryPath(bin,position,posting)+'/recovery-review?'+query,valid.StockLossRecoveryReview)
    const layer=page.position.layer
    if (page.postingId!==posting || page.position.location.id!==bin || layer.placementId!==position ||
        layer.catalogId!==layer.currentCatalog.id || page.receipt.deliveryId!==layer.deliveryId || page.receipt.catalogId!==layer.catalogId ||
        (page.complete && (page.originalLostQuantity!==page.recoveredQuantity+page.remainingQuantity ||
          !costConserved(page.originalAllocatedItemCostMinor,page.restoredItemCostMinor,page.remainingLostItemCostMinor) ||
          new Set(page.sources.map(s=>s.quarantineId)).size!==page.sources.length || (!after && page.sources[0]?.quarantineId!==null) ||
          page.sources.some(s=>s.originalQuantity!==s.recoveredQuantity+s.remainingQuantity || !costConserved(s.originalItemCostMinor,s.restoredItemCostMinor,s.remainingItemCostMinor)) ||
          page.recoveries.some(h=>h.saleableQuantity+h.quarantineQuantity<=0 || (h.quarantineQuantity>0 ? h.recoveredQuarantineId!==h.id : h.recoveredQuarantineId!==null))))) throw new ContractFailure()
    return page
  },
  async recoveryReceipt(bin:string,position:string,posting:string,exact:{recoveryId:string}|{commandId:string},after='',limit=25) {
    const query=new URLSearchParams({...exact,limit:String(limit)})
    if (after) query.set('after',after)
    const page=await read(recoveryPath(bin,position,posting)+'/recovery-receipt?'+query,valid.StockLossRecoveryEvidence)
    if (page.postingId!==posting || page.positionId!==position || page.location.id!==bin ||
        ('recoveryId' in exact ? page.id!==exact.recoveryId : page.commandId!==exact.commandId) ||
        page.recordedCatalog.id!==page.receipt.catalogId || (page.complete && (
          (page.quarantineQuantity>0 ? page.recoveredQuarantineId!==page.id : page.recoveredQuarantineId!==null) ||
          !page.allocations.length || new Set(page.allocations.map(r=>r.quarantineId)).size!==page.allocations.length ||
          page.allocations.some(r=>r.saleableQuantity+r.quarantineQuantity<1 ||
            (r.restoredItemCostMinor===null)!==(page.restoredItemCostMinor===null)) ||
          (!after && !page.nextAfter && (page.allocations.reduce((sum,r)=>sum+r.saleableQuantity,0)!==page.saleableQuantity ||
            page.allocations.reduce((sum,r)=>sum+r.quarantineQuantity,0)!==page.quarantineQuantity ||
            (page.restoredItemCostMinor!==null && page.allocations.reduce((sum,r)=>sum+(r.restoredItemCostMinor??0),0)!==page.restoredItemCostMinor)))))) throw new ContractFailure()
    return page
  },
  async postedLoss(bin: string, position: string, exact: { postingId: string } | { observationId: string } | { commandId: string } | { caseId: string }, after = '', limit = 25, quarantineAfter = '', quarantineLimit = 25) {
    const params = new URLSearchParams({ ...exact, limit: String(limit), quarantine: 'true', recoveries: 'true', quarantineLimit: String(quarantineLimit) })
    if (quarantineAfter) params.set('quarantineAfter', quarantineAfter)
    if (after) params.set('after', after)
    const result = await read('receiving/bins/' + encodeURIComponent(bin) + '/positions/' + encodeURIComponent(position) + '/posted-loss?' + params, valid.StockLossSnapshot)
    if (result.location.id !== bin || result.positionId !== position || result.reviewedObservation.positionId !== position ||
        result.reviewedObservation.purchaseId !== result.purchaseId || result.reviewedObservation.locationId !== bin ||
        ('postingId' in exact && result.postingId !== exact.postingId) || ('commandId' in exact && result.commandId !== exact.commandId) ||
        ('caseId' in exact && result.caseId !== exact.caseId)) throw new ContractFailure()
    if (result.complete) {
      const {quarantineAttributions:rows, quarantinedQuantityBefore:before, quarantinedQuantityAfter:afterLoss} = result
      if (!rows || before === undefined || afterLoss === undefined || result.quarantineAttributionRecorded === undefined) throw new ContractFailure()
      const lost = before-afterLoss
      if (lost < 0 || lost > result.lostQuantity || rows.some(q=>q.quantity !== q.priorRemainingQuantity-q.remainingQuantity) ||
          (!quarantineAfter && !result.nextQuarantineAfter && rows.reduce((n,q)=>n+q.quantity,0) !== lost) ||
          (!result.quarantineAttributionRecorded && (lost || rows.length || result.nextQuarantineAfter))) throw new ContractFailure()
    }
    return result
  },
  async positionHolds(binId: string, positionId: string, after = '', commandId = '') {
    const params = new URLSearchParams({ limit: '25', quarantine:'true' })
    if (after) params.set('after', after)
    if (commandId) params.set('commandId', commandId)
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/positions/' +
      encodeURIComponent(positionId) + '/holds?' + params, valid.StockHoldPositionSnapshot)
    if (result.location.id !== binId || result.positionId !== positionId ||
        (commandId && (result.holds.length !== 1 || result.holds[0].commandId !== commandId))) throw new ContractFailure()
    return result
  },
  async holdReleases(binId: string, positionId: string, holdId: string, after = '', commandId = '') {
    const params = new URLSearchParams({ limit: '25' })
    if (after) params.set('after', after)
    if (commandId) params.set('commandId', commandId)
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/positions/' +
      encodeURIComponent(positionId) + '/holds/' + encodeURIComponent(holdId) + '/releases?' + params, valid.StockHoldReleasePage)
    if (result.holdId !== holdId || (commandId && (result.releases.length !== 1 ||
        result.releases[0].commandId !== commandId))) throw new ContractFailure()
    return result
  },
  async closure(bin:string,position:string,caseId:string, selector?: {receiptId:string}|{commandId:string}) {
    const path='receiving/bins/'+encodeURIComponent(bin)+'/positions/'+encodeURIComponent(position)+'/shortages/'+encodeURIComponent(caseId)+'/closure'
    const result=await read(path+(selector && 'commandId' in selector ? '?'+new URLSearchParams(selector) : ''),valid.StockShortageClosureEvidence)
    const {openingObservation:opening,recountObservation:exact}=result
    if (result.location.id!==bin || result.positionId!==position || result.caseId!==caseId ||
        (selector && ('receiptId' in selector ? selector.receiptId!==result.id : selector.commandId!==result.commandId)) ||
        [opening,exact].some(o=>o.locationId!==bin || o.positionId!==position || o.purchaseId!==result.purchaseId) ||
        opening.id!==caseId || opening.id===exact.id || opening.missingQuantity<=0 || exact.expectedPurchaseRevision<=opening.expectedPurchaseRevision ||
        exact.missingQuantity!==0 || exact.unidentifiedSurplusQuantity!==0 || exact.countedQuantity!==result.ledgerQuantity ||
        exact.ledgerQuantity!==result.ledgerQuantity || result.quarantinedQuantity+result.heldQuantity+result.offerBackedQuantity>result.ledgerQuantity)
      throw new ContractFailure()
    return result
  },
  async binCounts(binId: string, query: { positionId?: string; observationId?: string; commandId?: string; after?: string; limit?: string }, withClosures=false) {
    const params=new URLSearchParams(query)
    if (withClosures) params.set('closures','true')
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/counts?' + params, valid.BinCountSnapshot)
    const scoped = Boolean(query.positionId || query.commandId || query.observationId)
    const wrongPosition = (id: string) => scoped && id !== result.positionId
    if (withClosures && result.complete && (!result.closures || result.closures.some(c=>wrongPosition(c.positionId) ||
        c.openingObservationId!==c.caseId || c.openingObservationId===c.recountObservationId) ||
        ((query.observationId || query.commandId) && result.closures.length>1))) throw new ContractFailure()
    if (result.location.id !== binId || scoped !== Boolean(result.positionId) || (query.positionId && wrongPosition(query.positionId)) ||
        result.observations.some(o => o.locationId !== binId || wrongPosition(o.positionId) ||
          (query.observationId && o.id !== query.observationId) || (query.commandId && o.commandId !== query.commandId)) ||
        result.positions.some(p => wrongPosition(p.positionId)) || result.shortages.some(s => wrongPosition(s.positionId)) ||
        ((query.observationId || query.commandId) && result.complete && result.observations.length !== 1)) throw new ContractFailure()
    return result
  },
  binTransfers: (id: string) => read('receiving/bins/' + encodeURIComponent(id) + '/movements?quarantine=true', valid.BinTransferSnapshot),
  async receiving() {
    const page=await read('receiving/current?holds=true&losses=true&quarantine=true&recoveries=true',valid.LossReceivingWorkspace)
    if (page.complete) for (const purchase of page.purchases) for (const delivery of purchase.deliveries) verifyRecoveryTotals(delivery)
    return page
  },
  diagnostic: (id: string) => read('diagnostics/' + encodeURIComponent(id), valid.Diagnostic),
  async save(command: Command, csrf: string) {
    const options = {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Workspace-Id': command.workspaceId },
      body: JSON.stringify(command.body)
    }
    const path = commands[command.kind].path
    const receipt = command.kind === 'catalog' || command.kind === 'location'
      ? await read(path, valid.Receipt, options)
      : command.kind === 'hold' || command.kind === 'hold-release' ? await read(path, valid.StockHoldReceipt, options)
      : command.kind === 'quarantine' || command.kind === 'quarantine-release' ? await read(path, valid.StockQuarantineReceipt, options)
      : command.kind === 'offer-withdrawal' ? await read(path, valid.DraftBackingWithdrawalReceipt, options)
      : command.kind === 'recount' ? await read(path, valid.RecountReceipt, options)
      : command.kind === 'stock-loss-recovery' ? await read(path,valid.StockLossRecoveryReceipt,options)
      : command.kind === 'shortage-closure' ? await read(path,valid.StockShortageClosureReceipt,options)
      : command.kind === 'stock-loss' ? await read(path, valid.StockLossReceipt, options)
      : command.kind === 'count' ? await read(path, valid.StockCountReceipt, options)
      : command.kind === 'transfer' ? await read(path, valid.TransferReceipt, options) : await read(path, valid.ReceivingReceipt, options)
    if (receipt.commandId !== command.body.commandId || receipt.kind !== command.kind ||
        ((command.kind === 'quarantine' || command.kind === 'quarantine-release') && receipt.revision !== command.body.expectedRevision + 1) ||
        ((command.kind === 'stock-loss-recovery' || command.kind === 'shortage-closure' || command.kind === 'offer-withdrawal') && receipt.revision !== command.body.expectedRevision + 1) ||
        (command.kind === 'recount' && (receipt.entityId !== command.body.previewId || receipt.revision !== 1))) throw new ContractFailure()
    return receipt
  }
}

const legacy = 'brickvault.v1.pending.'
const prefix = 'brickvault.v2.pending.'
const uuid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
const journalKey = (workspaceId: string, commandId: string) => prefix + workspaceId + '.' + commandId
export function readJournal(activeId: string) {
  const queued: Command[] = []
  const held: Held[] = []
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith(legacy)) {
      held.push({ workspaceId: null, commandId: key.slice(legacy.length) })
      continue
    }
    if (!key.startsWith(prefix)) continue
    const raw = localStorage.getItem(key)
    if (raw === null) continue
    const value = JSON.parse(raw)
    const { workspaceId, kind, body } = value
    if (!uuid.test(workspaceId) || key !== journalKey(workspaceId, body.commandId)) throw new Error('Invalid recovery identity')
    if (workspaceId !== activeId) held.push({ workspaceId, commandId: body.commandId })
    else if (Object.hasOwn(commands, kind) && commands[kind as Command['kind']].valid(body) &&
        (kind !== 'hold-release' || (uuid.test(value.locationId) && uuid.test(value.positionId)))) queued.push(value)
    else throw new Error('Invalid recovery record')
  }
  return { commands: queued, held }
}

export function remember(command: Command) {
  const validBody = commands[command.kind].valid(command.body)
  if (!validBody || (command.kind === 'hold-release' && (!uuid.test(command.locationId) || !uuid.test(command.positionId)))) throw new InvalidCommand('Check the required fields and quantities before saving.')
  localStorage.setItem(journalKey(command.workspaceId, command.body.commandId), JSON.stringify(command))
}
export function forget(command: Command) {
  localStorage.removeItem(journalKey(command.workspaceId, command.body.commandId))
}

export function requiresFreshCount(error: unknown) {
  return error instanceof ApiFailure && error.status === 409 && error.detail?.code === 'stock_recount_required'
}

export function explain(error: unknown) {
  if (error instanceof ApiFailure) {
    if (error.detail) return error.message + ' Reference ' + error.detail.correlationId + '.'
    if (error.status === 401) return 'Sign in to access the workspace.'
    if (error.status === 403) return 'Access was denied. Check your session and try again.'
    return 'The server returned HTTP ' + error.status + '.'
  }
  if (error instanceof ContractFailure) return error.message
  return 'The service could not be reached. Check the private connection and retry.'
}
