export type OperationView = 'receiving' | 'orders' | 'recovery'
export type EventEntry = { title: string; detail: string }

export const receiptQuantity = 12
export const receiptPart = 'PL-3020'
export const receiptBin = 'B-12'

type ReceiptStep = { id: number; kind: 'receive' | 'putaway'; quantity: number }
export type Receipt = {
  counted: number
  stored: number
  events: EventEntry[]
  reversible: ReceiptStep[]
  nextId: number
}
export type OrderStage =
  'pick' | 'short' | 'pack' | 'label' | 'printing' | 'handoff' | 'shipped' | 'return' | 'restocked'
export type Order = { stage: OrderStage; amended: boolean; scanned: string[]; events: EventEntry[] }
export type ReadStatus = 'initial' | 'reading' | 'failed' | 'current'
export type Recovery = {
  read: ReadStatus
  response: 'success' | 'failure'
  reviewed: boolean
  sent: boolean
  verified: boolean
  successfulReads: number
  events: EventEntry[]
}

export function newOperations() {
  return {
    receipt: {
      counted: 0,
      stored: 0,
      reversible: [],
      nextId: 1,
      events: [{ title: 'Receipt opened', detail: '12 units expected from purchase PO-208.' }]
    } as Receipt,
    order: {
      stage: 'pick',
      amended: false,
      scanned: [],
      events: [{ title: 'Stock reserved', detail: '4 units allocated to tote T-04.' }]
    } as Order,
    recovery: {
      read: 'initial',
      response: 'success',
      reviewed: false,
      sent: false,
      verified: false,
      successfulReads: 0,
      events: [
        {
          title: 'Response lost after send',
          detail: 'The outcome for lot RC-01 is unknown. Read its current quantity before any new write.'
        }
      ]
    } as Recovery
  }
}

export function bags(order: Order): string[] {
  return order.amended ? ['BAG-41A', 'BAG-41B', 'BAG-41C'] : ['BAG-41A', 'BAG-41B']
}

export function orderCounts(order: Order) {
  const quantity = bags(order).length * 2
  const released = ['shipped', 'return', 'restocked'].includes(order.stage)
  const onHand = order.stage === 'shipped' ? 12 - quantity : 12
  const reserved = released ? 0 : quantity
  const unavailable = order.stage === 'return' ? quantity : 0
  return { quantity, onHand, reserved, unavailable, sellable: onHand - reserved - unavailable }
}

export type Outcome = { message: string; invalid?: string; next?: string }
const normalized = (value: string) => value.trim().toUpperCase()

function addReceiptStep(receipt: Receipt, kind: ReceiptStep['kind'], quantity: number) {
  const id = receipt.nextId++
  receipt.reversible.push({ id, kind, quantity })
  return id
}

export function receiveUnits(receipt: Receipt, scan: string, quantity: number): Outcome {
  if (receipt.counted === receiptQuantity || receipt.counted > receipt.stored) {
    return { message: 'Finish the current receipt step first.' }
  }
  if (normalized(scan) !== receiptPart)
    return { message: 'Item code does not match the blue plate on this receipt.', invalid: 'receipt-item' }
  const remaining = receiptQuantity - receipt.counted
  if (!Number.isInteger(quantity) || quantity < 1 || quantity > remaining) {
    return { message: `Enter a whole quantity from 1 to ${remaining}.`, invalid: 'receipt-count' }
  }
  receipt.counted += quantity
  const id = addReceiptStep(receipt, 'receive', quantity)
  receipt.events.unshift({
    title: 'Units received',
    detail: `Step ${id} · ${quantity} placed in the intake tray, awaiting a verified bin.`
  })
  return { message: `${quantity} units received into the intake tray.`, next: 'receipt-bin' }
}

export function putAway(receipt: Receipt, bin: string): Outcome {
  const staged = receipt.counted - receipt.stored
  if (staged < 1) return { message: 'Receive units before confirming putaway.' }
  if (normalized(bin) !== receiptBin)
    return {
      message: `Bin does not match the assigned location. Scan or enter ${receiptBin}.`,
      invalid: 'receipt-bin'
    }
  receipt.stored += staged
  const id = addReceiptStep(receipt, 'putaway', staged)
  receipt.events.unshift({
    title: 'Putaway confirmed',
    detail: `Step ${id} · ${staged} moved from the intake tray to ${receiptBin}.`
  })
  return {
    message: `${staged} units confirmed in ${receiptBin}.`,
    next: receipt.stored === receiptQuantity ? 'op-task-heading' : 'receipt-item'
  }
}

export function reverseReceiptStep(receipt: Receipt, expectedId: number): Outcome {
  const step = receipt.reversible.at(-1)
  if (!step || step.id !== expectedId) return { message: 'Review the last receipt step before reversing it.' }
  let detail: string
  if (step.kind === 'putaway') {
    receipt.stored -= step.quantity
    detail = `${step.quantity} units moved from ${receiptBin} back to the intake tray.`
  } else {
    receipt.counted -= step.quantity
    detail = `${step.quantity} units removed from intake and marked awaiting receipt.`
  }
  receipt.reversible.pop()
  const title = step.kind === 'putaway' ? 'Putaway reversed' : 'Receipt reversed'
  receipt.events.unshift({ title, detail: `Reverses step ${step.id} · ${detail}` })
  return { message: `${title}. ${detail}`, next: step.kind === 'putaway' ? 'receipt-bin' : 'receipt-item' }
}

export function pickBag(order: Order, input: string): Outcome {
  const code = normalized(input)
  if (!['pick', 'short'].includes(order.stage)) return { message: 'Picking is complete.' }
  if (!bags(order).includes(code))
    return { message: 'Bag label does not belong to this order line.', invalid: 'order-scan' }
  if (order.scanned.includes(code))
    return { message: `${code} is already in tote T-04. The count has not changed.`, invalid: 'order-scan' }
  order.scanned.push(code)
  const complete = order.scanned.length === bags(order).length
  if (complete) order.stage = 'pack'
  order.events.unshift({ title: 'Bag confirmed', detail: `${code} · 2 units placed in tote T-04.` })
  return {
    message: `${code} confirmed. ${complete ? 'All units are ready to pack.' : 'Continue with the remaining bag labels.'}`,
    next: complete ? 'order-advance' : 'order-scan'
  }
}

export function reportShort(order: Order): Outcome {
  if (order.stage !== 'pick') return { message: 'The short count has already been recorded.' }
  order.stage = 'short'
  const missing = (bags(order).length - order.scanned.length) * 2
  order.events.unshift({
    title: 'Short count reported',
    detail: `${missing} allocated units require a recount. Packing blocked.`
  })
  return {
    message: 'Short count recorded. Confirm each missing bag to release the packing block.',
    next: 'order-scan'
  }
}

export function amendOrder(order: Order): Outcome {
  if (order.amended || !['pick', 'short'].includes(order.stage))
    return { message: 'This amendment cannot be applied again.' }
  order.amended = true
  order.events.unshift({
    title: 'Order amendment applied',
    detail: '2 additional units reserved. BAG-41C added to the order line.'
  })
  return { message: 'Order now requires 6 units. Existing picks are preserved.', next: 'order-scan' }
}

const transitions: Partial<Record<OrderStage, [OrderStage, string, string, string]>> = {
  pack: [
    'label',
    'Parcel packed',
    'Packed in parcel P-401. Physical custody and reservations are retained.',
    'order-advance'
  ],
  label: ['printing', 'Label queued', 'Await physical confirmation of the printed label.', 'order-advance'],
  printing: [
    'handoff',
    'Printed label verified',
    'Parcel P-401 is ready for carrier handoff.',
    'handoff-parcel'
  ],
  handoff: [
    'shipped',
    'Carrier handoff confirmed',
    'On-hand stock and reserved units decreased together.',
    'op-task-heading'
  ],
  shipped: [
    'return',
    'Return received',
    'Parcel contents are physically on hand in the returns tray and held for inspection.',
    'return-bin'
  ]
}

export function advanceOrder(order: Order): Outcome {
  const transition = transitions[order.stage]
  if (!transition) return { message: 'Complete the current task before continuing.' }
  const [stage, title, detail, next] = transition
  order.stage = stage
  order.events.unshift({ title, detail })
  return { message: detail, next }
}

export function verifyReturn(order: Order, bin: string, conditionConfirmed: boolean): Outcome {
  if (order.stage !== 'return') return { message: 'No return is awaiting verification.' }
  if (normalized(bin) !== 'A-07')
    return { message: 'Scan or enter A-07 to verify the return location.', invalid: 'return-bin' }
  if (!conditionConfirmed)
    return {
      message: 'Confirm the condition of every returned unit before release.',
      invalid: 'return-condition'
    }
  order.stage = 'restocked'
  order.events.unshift({
    title: 'Returned units released',
    detail: 'Condition verified; physical placement confirmed in A-07.'
  })
  return { message: 'Returned units are sellable again.', next: 'op-task-heading' }
}

export function finishRead(recovery: Recovery): Outcome {
  if (recovery.response === 'failure') {
    recovery.read = 'failed'
    recovery.events.unshift({
      title: 'Observation read failed',
      detail: 'Prior quantities retained. Publishing blocked until a successful read.'
    })
    return {
      message: 'The simulated read failed. Prior observations are stale. Publishing is blocked.',
      next: 'read-observations'
    }
  }
  recovery.read = 'current'
  recovery.successfulReads += 1
  recovery.verified = recovery.sent
  recovery.events.unshift({
    title: 'Fresh observations received',
    detail: recovery.verified
      ? 'All three targets match their intended quantities.'
      : 'Lot RC-01 has a confirmed zero. Lot RC-03 still differs.'
  })
  return {
    message: recovery.verified
      ? 'All three quantities verified by fresh read.'
      : 'Fresh read complete. One mismatch needs review.',
    next: recovery.verified ? 'op-task-heading' : 'review-mismatch'
  }
}

export function publishCorrection(recovery: Recovery): Outcome {
  if (recovery.read !== 'current' || !recovery.reviewed || recovery.sent)
    return { message: 'Refresh and review the current mismatch before confirming.' }
  recovery.sent = true
  recovery.reviewed = false
  recovery.events.unshift({
    title: 'Correction acknowledged',
    detail: 'Set one lot, RC-03, to 6. A fresh read is still required.'
  })
  return {
    message: 'Correction acknowledged for RC-03. Read observations again to verify.',
    next: 'read-observations'
  }
}
