export const countReceiptLink = (bin: string, receipt: string) => '#locations/' + bin + '/counts/' + receipt
export const countPositionLink = (bin: string, position: string) => '#locations/' + bin + '/positions/' + position + '/counts'
export const holdPositionLink = (bin: string, position: string) => '#locations/' + bin + '/positions/' + position + '/holds'
export function readCountLink(hash: string) {
  const [page, binId, kind, id, action, ...extra] = hash.slice(1).split('/')
  if (page !== 'locations' || !binId) return
  const uuid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
  if (!uuid.test(binId) || extra.length) return
  if (!kind && !id) return { binId: binId.toLowerCase() }
  if (!id || !uuid.test(id)) return
  if (kind === 'counts' && !action) return { binId: binId.toLowerCase(), receiptId: id.toLowerCase() }
  if (kind === 'positions' && action === 'holds') return { binId: binId.toLowerCase(), holdPositionId: id.toLowerCase() }
  if (kind === 'positions' && action === 'counts') return { binId: binId.toLowerCase(), positionId: id.toLowerCase() }
}
export type CountLink = ReturnType<typeof readCountLink>
