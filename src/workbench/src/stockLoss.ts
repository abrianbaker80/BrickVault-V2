import { sameLabel } from './binLabels'
import { api, ApiFailure, ContractFailure, type StockLoss, type StockLossCommand } from './api'

export async function recoverStockLoss(command: StockLossCommand) {
  const offers = new Map(command.offerReductions.map(row => [row.offerId,row.quantity]))
  const attributions = new Map(command.quarantineAttributions?.map(row => [row.quarantineId,row.quantity]))
  const seenOfferPages = new Set<string>()
  const seenQuarantinePages = new Set<string>()
  let receipt: StockLoss | undefined
  let offerAfter: string | null = ''
  let quarantineAfter: string | null = ''
  do {
    let page: StockLoss
    try {
      page = await api.postedLoss(command.locationId, command.positionId, { commandId: command.commandId },
        offerAfter ?? '', 100, quarantineAfter ?? '', 100)
    } catch (failure) {
      if (!receipt && failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_stock_loss') return
      throw failure
    }
    if (!page.complete || (receipt && (page.postingId !== receipt.postingId || page.correlationId !== receipt.correlationId)) ||
        page.reviewedObservation.id !== command.expectedObservationId || page.caseId !== command.caseId || page.purchaseId !== command.purchaseId ||
        page.revision !== command.expectedRevision + 1 || page.reason !== command.reason || !sameLabel(page.confirmedLocationCode,command.confirmedLocationCode) ||
        page.quarantineAttributionRecorded !== (command.quarantineAttributions != null))
      throw new ContractFailure()
    receipt = page
    if (offerAfter !== null) {
      for (const row of page.reductions) {
        if (offers.get(row.offerId) !== row.quantity) throw new ContractFailure()
        offers.delete(row.offerId)
      }
      offerAfter = page.nextAfter
      if (offerAfter) {
        if (!offers.size || !page.reductions.length || seenOfferPages.has(offerAfter)) throw new ContractFailure()
        seenOfferPages.add(offerAfter)
      }
    }
    if (quarantineAfter !== null) {
      for (const row of page.quarantineAttributions ?? []) {
        if (attributions.get(row.quarantineId) !== row.quantity) throw new ContractFailure()
        attributions.delete(row.quarantineId)
      }
      quarantineAfter = page.nextQuarantineAfter ?? null
      if (quarantineAfter) {
        if (!attributions.size || !page.quarantineAttributions?.length || seenQuarantinePages.has(quarantineAfter)) throw new ContractFailure()
        seenQuarantinePages.add(quarantineAfter)
      }
    }
  } while (offerAfter || quarantineAfter)
  if (offers.size || attributions.size) throw new ContractFailure()
  return receipt
}
