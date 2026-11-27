import { sameLabel } from './binLabels'
import { api, ApiFailure, ContractFailure, type PartOutCommand, type PartOutEvidence } from './api'

export async function recoverPartOut(command: PartOutCommand) {
  const outputs = new Map(command.outputs.map(line => [line.lineId,line]))
  const residuals = new Map(command.physicalResiduals.map(line => [line.lineId,line]))
  const seenOutputs = new Set<string>(), seenResiduals = new Set<string>()
  let evidence: PartOutEvidence | undefined
  let outputAfter = '', residualAfter = '', readOutputs = true, readResiduals = true
  do {
    let page: PartOutEvidence
    try {
      page = await api.partOutReceipt(command.sourcePositionId,{commandId:command.commandId},outputAfter,residualAfter,100)
    } catch (error) {
      if (!evidence && error instanceof ApiFailure && error.status===404 && error.detail?.code==='unknown_stock_part_out') return
      throw error
    }
    if (!page.complete || page.purchaseId!==command.purchaseId || page.revision!==command.expectedRevision+1 ||
        page.sourceLocation.id!==command.sourceLocationId || !sameLabel(page.confirmedSourceLocationCode,command.confirmedSourceLocationCode) ||
        page.recordedSourceCatalog.id!==command.sourceCatalogId || page.recordedSourceCatalog.revision!==command.sourceCatalogRevision ||
        page.quantity!==command.quantity || page.reason!==command.reason || page.residualItemCostMinor!==command.residualItemCostMinor ||
        (evidence && (page.id!==evidence.id || page.correlationId!==evidence.correlationId || page.sourceItemCostMinor!==evidence.sourceItemCostMinor)))
      throw new ContractFailure()
    evidence = page
    if (readOutputs) {
      for (const row of page.outputs) {
        const expected=outputs.get(row.lineId)
        if (!expected || expected.catalogId!==row.recordedCatalog.id || expected.catalogRevision!==row.recordedCatalog.revision ||
            expected.locationId!==row.location.id || !sameLabel(expected.confirmedLocationCode,row.confirmedLocationCode) ||
            expected.quantity!==row.quantity || expected.itemCostMinor!==row.itemCostMinor || expected.disposition!==row.disposition) throw new ContractFailure()
        outputs.delete(row.lineId)
      }
      outputAfter=page.nextOutputAfter || ''
      if (outputAfter && (!outputs.size || seenOutputs.has(outputAfter))) throw new ContractFailure()
      seenOutputs.add(outputAfter); readOutputs=!!outputAfter
    }
    if (readResiduals) {
      for (const row of page.physicalResiduals) {
        const expected=residuals.get(row.lineId)
        if (!expected || expected.description!==row.description || expected.disposition!==row.disposition || expected.quantity!==row.quantity) throw new ContractFailure()
        residuals.delete(row.lineId)
      }
      residualAfter=page.nextResidualAfter || ''
      if (residualAfter && (!residuals.size || seenResiduals.has(residualAfter))) throw new ContractFailure()
      seenResiduals.add(residualAfter); readResiduals=!!residualAfter
    }
  } while (readOutputs || readResiduals)
  const expectedCost=command.residualItemCostMinor===null ? null : command.outputs.reduce(
    (sum,row)=>sum+(row.itemCostMinor ?? 0),command.residualItemCostMinor)
  if (outputs.size || residuals.size || !evidence || evidence.sourceItemCostMinor!==expectedCost) throw new ContractFailure()
  return evidence
}
