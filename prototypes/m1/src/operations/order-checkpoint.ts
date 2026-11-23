import { advanceOrder, amendOrder, newOperations, pickBag, reportShort, verifyReturn } from './model'
import type { Order } from './model'

const storageName = 'brickvault-m1-order'
const version = 1
const unavailable = 'Tab storage is unavailable. This order can be continued until the page is reloaded.'
type OrderSession = { order: Order; steps: string[]; notice: string }

function replayStep(order: Order, step: string): boolean {
  if (step.startsWith('bag:')) return Boolean(pickBag(order, step.slice(4)).next)
  if (step === 'short') return Boolean(reportShort(order).next)
  if (step === 'amend') return Boolean(amendOrder(order).next)
  if (step !== order.stage) return false
  if (step === 'return') return Boolean(verifyReturn(order, 'A-07', true).next)
  return Boolean(advanceOrder(order).next)
}

export function restoreOrderCheckpoint(): OrderSession {
  const fresh: OrderSession = { order: newOperations().order, steps: [], notice: '' }
  let raw: string | null
  try {
    raw = sessionStorage.getItem(storageName)
  } catch {
    return { ...fresh, notice: unavailable }
  }
  if (raw === null) return fresh
  try {
    const data: unknown = JSON.parse(raw)
    if (
      !data ||
      typeof data !== 'object' ||
      !('version' in data) ||
      data.version !== version ||
      !('steps' in data) ||
      !Array.isArray(data.steps) ||
      data.steps.length > 16
    ) {
      throw new Error('Invalid checkpoint')
    }
    const order = newOperations().order
    for (const step of data.steps) {
      if (typeof step !== 'string' || step.length > 16 || !replayStep(order, step))
        throw new Error('Invalid order step')
    }
    return {
      order,
      steps: data.steps,
      notice: 'Resumed order BV-401 from its last confirmed step in this tab.'
    }
  } catch {
    saveOrderCheckpoint([])
    return {
      ...fresh,
      notice: 'The saved demo order could not be restored. Order BV-401 has restarted at picking.'
    }
  }
}

export function saveOrderCheckpoint(steps: string[]): string {
  try {
    if (steps.length) sessionStorage.setItem(storageName, JSON.stringify({ version, steps }))
    else sessionStorage.removeItem(storageName)
    return ''
  } catch {
    return unavailable
  }
}
