export const draftBackingLink = (bin: string, position: string, view?: 'offers' | 'receipts', id?: string) =>
  '#locations/' + bin + '/positions/' + position + '/draft-backing' + (view && id ? '/' + view + '/' + id : '')
export const countReceiptLink = (bin: string, receipt: string) => '#locations/' + bin + '/counts/' + receipt
export const countPositionLink = (bin: string, position: string) => '#locations/' + bin + '/positions/' + position + '/counts'
export const holdPositionLink = (bin: string, position: string) => '#locations/' + bin + '/positions/' + position + '/holds'
export const quarantineLink = (bin: string, position: string, receipt?: string) =>
  '#locations/' + bin + '/positions/' + position + '/quarantine' + (receipt ? '/' + receipt : '')
export const lossCaseLink = (bin: string, position: string, id: string) => '#locations/' + bin + '/positions/' + position + '/loss-review/' + id
export const lossReceiptLink = (bin: string, position: string, id: string) => '#locations/' + bin + '/positions/' + position + '/posted-loss/' + id
export const closureLink = (bin:string,position:string,caseId:string,mode:'review'|'receipt',id:string) =>
  '#locations/'+bin+'/positions/'+position+'/shortages/'+caseId+'/'+mode+'/'+id
export const recoveryLink = (bin:string,position:string,posting:string,receipt?:string) =>
  '#locations/'+bin+'/positions/'+position+'/posted-loss/'+posting+'/recoveries'+(receipt ? '/'+receipt : '')
export type CountLink = { binId: string; receiptId?: string; positionId?: string; holdPositionId?: string;
  quarantinePositionId?: string; quarantineReceiptId?: string; draftPositionId?: string; draftOfferId?: string; withdrawalId?: string;
  recovery?: {position:string; posting:string; receipt?:string};
  closure?: {position:string; caseId:string; mode:'review'|'receipt'; id:string};
  lossCaseId?: string; lossPositionId?: string; lossReceiptId?: string } | undefined
export function readCountLink(hash: string): CountLink {
  const [page, binId, kind, id, action, ...extra] = hash.slice(1).split('/')
  if (page !== 'locations' || !binId) return
  const uuid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
  if (!uuid.test(binId)) return
  if (kind==='positions' && uuid.test(id) && action==='posted-loss' && uuid.test(extra[0] || '') && extra[1]==='recoveries' &&
      (extra.length===2 || (extra.length===3 && uuid.test(extra[2])))) {
    return {binId:binId.toLowerCase(),recovery:{position:id.toLowerCase(),posting:extra[0].toLowerCase(),receipt:extra[2]?.toLowerCase()}}
  }
  if (kind === 'positions' && uuid.test(id) && action === 'shortages' && extra.length===3 &&
      uuid.test(extra[0]) && (extra[1]==='review' || extra[1]==='receipt') && uuid.test(extra[2])) {
    return {binId:binId.toLowerCase(),closure:{position:id.toLowerCase(),caseId:extra[0].toLowerCase(),mode:extra[1],id:extra[2].toLowerCase()}}
  }
  if (kind === 'positions' && uuid.test(id) && action === 'draft-backing') {
    const scope = { binId: binId.toLowerCase(), draftPositionId: id.toLowerCase() }
    if (!extra.length) return scope
    if (extra.length === 2 && uuid.test(extra[1])) {
      if (extra[0] === 'offers') return { ...scope, draftOfferId: extra[1].toLowerCase() }
      if (extra[0] === 'receipts') return { ...scope, withdrawalId: extra[1].toLowerCase() }
    }
    return
  }
  if (kind === 'positions' && uuid.test(id) && extra.length === 1 && uuid.test(extra[0])) {
    if (action === 'quarantine') return { binId:binId.toLowerCase(), quarantinePositionId:id.toLowerCase(), quarantineReceiptId:extra[0].toLowerCase() }
    const context = { binId: binId.toLowerCase(), lossPositionId: id.toLowerCase() }
    if (action === 'loss-review') return { ...context, lossCaseId: extra[0].toLowerCase() }
    if (action === 'posted-loss') return { ...context, lossReceiptId: extra[0].toLowerCase() }
  }
  if (extra.length) return
  if (!kind && !id) return { binId: binId.toLowerCase() }
  if (!id || !uuid.test(id)) return
  if (kind === 'counts' && !action) return { binId: binId.toLowerCase(), receiptId: id.toLowerCase() }
  if (kind === 'positions' && action === 'quarantine') return { binId:binId.toLowerCase(), quarantinePositionId:id.toLowerCase() }
  if (kind === 'positions' && action === 'holds') return { binId: binId.toLowerCase(), holdPositionId: id.toLowerCase() }
  if (kind === 'positions' && action === 'counts') return { binId: binId.toLowerCase(), positionId: id.toLowerCase() }
}
