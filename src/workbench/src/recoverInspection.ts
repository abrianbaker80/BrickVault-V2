import { sameLabel } from './binLabels'
import { api, ApiFailure, ContractFailure, type RecoveryCommand, type RecoveryEvidence } from './api'

export async function recoverInspection(command:RecoveryCommand) {
  const remaining=new Map(command.allocations.map(row=>[row.quarantineId,row]))
  const visited=new Set<string>()
  const saleable=command.allocations.reduce((sum,row)=>sum+row.saleableQuantity,0)
  const quarantine=command.allocations.reduce((sum,row)=>sum+row.quarantineQuantity,0)
  let restoredCost=0
  let receipt:RecoveryEvidence | undefined
  let after=''
  do {
    let page:RecoveryEvidence
    try {
      page=await api.recoveryReceipt(command.locationId,command.positionId,command.postingId,{commandId:command.commandId},after,100)
    } catch (failure) {
      if (!receipt && failure instanceof ApiFailure && failure.status===404 && failure.detail?.code==='unknown_stock_loss_recovery') return
      throw failure
    }
    if (!page.complete || page.purchaseId!==command.purchaseId || page.revision!==command.expectedRevision+1 ||
        page.saleableQuantity!==saleable || page.quarantineQuantity!==quarantine ||
        !command.confirmedIdentity || page.recordedCatalog.id!==command.catalogId || page.recordedCatalog.revision!==command.catalogRevision ||
        page.reason!==command.reason || !sameLabel(page.confirmedLocationCode,command.confirmedLocationCode) ||
        (receipt && (receipt.id!==page.id || receipt.correlationId!==page.correlationId || receipt.restoredItemCostMinor!==page.restoredItemCostMinor))) throw new ContractFailure()
    receipt=page
    for (const row of page.allocations) {
      const source=remaining.get(row.quarantineId)
      if (!source || row.saleableQuantity!==source.saleableQuantity || row.quarantineQuantity!==source.quarantineQuantity) throw new ContractFailure()
      remaining.delete(row.quarantineId)
      restoredCost+=row.restoredItemCostMinor ?? 0
    }
    after=page.nextAfter || ''
    if (after && (!remaining.size || visited.has(after))) throw new ContractFailure()
    visited.add(after)
  } while (after)
  if (remaining.size || (receipt.restoredItemCostMinor!==null && receipt.restoredItemCostMinor!==restoredCost)) throw new ContractFailure()
  return receipt
}
