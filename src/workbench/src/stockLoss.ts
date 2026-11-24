import { api, ApiFailure, ContractFailure, type StockLoss, type StockLossCommand } from './api'

export async function recoverStockLoss(command: StockLossCommand) {
  const remaining = new Map(command.offerReductions.map(r => [r.offerId, r.quantity]))
  let receipt: StockLoss | undefined
  let after = ''
  const cursors = new Set<string>()
  do {
    let page: StockLoss
    try {
      page = await api.postedLoss(command.locationId, command.positionId, { commandId: command.commandId }, after, 100)
    } catch (failure) {
      if (!receipt && failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_stock_loss') return
      throw failure
    }
    if (!page.complete || (receipt && (page.postingId !== receipt.postingId || page.correlationId !== receipt.correlationId)) ||
        page.reviewedObservation.id !== command.expectedObservationId || page.caseId !== command.caseId || page.purchaseId !== command.purchaseId ||
        page.revision !== command.expectedRevision + 1 || page.reason !== command.reason || page.confirmedLocationCode !== command.confirmedLocationCode)
      throw new ContractFailure()
    receipt = page
    for (const row of page.reductions) {
      if (remaining.get(row.offerId) !== row.quantity) throw new ContractFailure()
      remaining.delete(row.offerId)
    }
    after = page.nextAfter || ''
    if (after && (!remaining.size || !page.reductions.length || cursors.has(after))) throw new ContractFailure()
    cursors.add(after)
  } while (after)
  if (remaining.size) throw new ContractFailure()
  return receipt
}
