import type { RegistryRow } from './api'

export const capacityLink=(id:string,eventId?:string)=>'#locations/'+id+'/capacity'+(eventId ? '/'+eventId : '')
export function limitLabel(limit:number|null) {
  return limit===null ? 'No piece limit set' : 'Piece limit '+limit
}
export function capacityLabel(row:RegistryRow|undefined) {
  if(!row?.capacity)return 'Capacity unavailable'
  const c=row.capacity
  return row.physicalQuantity+' present · '+limitLabel(c.pieceLimit)+(c.remainingQuantity===null ? '' :
    ' · '+c.remainingQuantity+(c.remainingQuantity===1?' space remaining':' spaces remaining'))+
    (c.overCapacityQuantity ? ' · '+c.overCapacityQuantity+' above limit' : '')
}
export function exceedsCapacity(row:RegistryRow|undefined,net:number) {
  const room=row?.capacity?.remainingQuantity
  return typeof room==='number' && net>room
}
export function capacityPreview(row:RegistryRow|undefined,net:number) {
  if(!row?.capacity)return 'Waiting for bin capacity.'
  const c=row.capacity
  const summary=row.location.code+' · '+capacityLabel(row)
  if(!net || !Number.isSafeInteger(net))return summary
  const change=net>0 ? 'Adding '+net : 'Removing '+(-net)
  return summary+'. '+change+(Math.abs(net)===1?' piece.':' pieces.')+(exceedsCapacity(row,net) ? ' Choose another bin or reduce the quantity.' : '')
}
