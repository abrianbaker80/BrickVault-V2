import type { ApiFailure, Command, RegistryRow } from './api'

export function confirmsCapacityRejection(command:Command, failure:ApiFailure) {
  if(failure.status!==409 || failure.detail?.code!=='location_capacity_exceeded')return false
  const c=failure.detail.capacity
  if(!c || !Number.isSafeInteger(c.pieceLimit) || c.pieceLimit<1 ||
     !Number.isSafeInteger(c.physicalQuantity) || c.physicalQuantity<0 ||
     c.remainingQuantity!==Math.max(0,c.pieceLimit-c.physicalQuantity) ||
     !Number.isSafeInteger(c.requestedNetQuantity) || c.requestedNetQuantity<=c.remainingQuantity)return false

  const bin=c.locationId.toLowerCase()
  switch(command.kind){
    case 'putaway':
      return command.body.locationId.toLowerCase()===bin && command.body.quantity===c.requestedNetQuantity
    case 'transfer':
      return command.body.destinationLocationId.toLowerCase()===bin && command.body.sourceLocationId.toLowerCase()!==bin &&
        command.body.quantity===c.requestedNetQuantity
    case 'part-out': {
      const outputPieces=command.body.outputs.reduce((sum,line)=>sum+(line.locationId.toLowerCase()===bin?line.quantity:0),0)
      const consumed=command.body.sourceLocationId.toLowerCase()===bin?command.body.quantity:0
      return outputPieces-consumed===c.requestedNetQuantity
    }
    default: return false
  }
}

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
