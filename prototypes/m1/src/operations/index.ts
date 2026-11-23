import {
  advanceOrder,
  amendOrder,
  finishRead,
  newOperations,
  pickBag,
  publishCorrection,
  putAway,
  receiveUnits,
  reportShort,
  reverseReceiptStep,
  verifyReturn
} from './model'
import type { OperationView, Outcome } from './model'
import { restoreOrderCheckpoint, saveOrderCheckpoint } from './order-checkpoint'
import { orderView, receiptView, recoveryView, reviewMarkup } from './presentation'
import './operations.css'

let orderSession = restoreOrderCheckpoint()
let records = { ...newOperations(), order: orderSession.order }
let readTimer: ReturnType<typeof setTimeout> | undefined
const listeners = new Set<(outcome?: Outcome, view?: OperationView) => void>()

export function resetOperations(): void {
  clearTimeout(readTimer)
  records = newOperations()
  orderSession = { order: records.order, steps: [], notice: saveOrderCheckpoint([]) }
  listeners.forEach((update) => update())
}

export function mountOperations(host: HTMLElement, view: OperationView): () => void {
  const frame = document.createElement('div')
  frame.className = 'op-workspace'
  const content = document.createElement('div')
  const message = document.createElement('p')
  message.className = 'op-feedback'
  message.id = 'op-message'
  message.setAttribute('role', 'status')
  message.setAttribute('aria-live', 'polite')
  message.setAttribute('aria-atomic', 'true')
  frame.append(message, content)
  host.replaceChildren(frame)
  let dialog: HTMLDialogElement | undefined
  let pointerTask: Element | null = null

  function render() {
    content.innerHTML =
      view === 'receiving'
        ? receiptView(records.receipt)
        : view === 'orders'
          ? orderView(records.order)
          : recoveryView(records.recovery)
  }

  function report(outcome: Outcome, moveFocus = true) {
    if (outcome.invalid) {
      for (const field of content.querySelectorAll('[aria-describedby~="op-message"]')) {
        field.removeAttribute('aria-invalid')
        const remaining = field.getAttribute('aria-describedby')!.split(/\s+/).filter((id) => id && id !== 'op-message')
        if (remaining.length) field.setAttribute('aria-describedby', remaining.join(' '))
        else field.removeAttribute('aria-describedby')
      }
      const input = content.querySelector<HTMLInputElement>('#' + outcome.invalid)
      input?.setAttribute('aria-invalid', 'true')
      if (input) {
        const descriptions = [input.getAttribute('aria-describedby'), 'op-message'].filter(Boolean)
        input.setAttribute('aria-describedby', descriptions.join(' '))
      }
      input?.focus()
      if (input?.type === 'text') input.select()
    } else {
      render()
      if (outcome.next && moveFocus) {
        const next =
          content.querySelector<HTMLElement>('#' + outcome.next) ??
          content.querySelector<HTMLElement>('#op-task-heading')
        next?.focus({ preventScroll: true })
      }
    }
    message.classList.toggle('op-error', Boolean(outcome.invalid))
    message.textContent = outcome.message
  }

  function recordOrderStep(step: string, outcome: Outcome) {
    if (outcome.next) {
      orderSession.steps.push(step)
      const notice = saveOrderCheckpoint(orderSession.steps)
      if (notice) outcome = { ...outcome, message: outcome.message + ' ' + notice }
    }
    report(outcome)
  }

  function confirmStage(stage: 'pack' | 'label' | 'printing' | 'handoff' | 'shipped') {
    recordOrderStep(stage, advanceOrder(records.order, stage))
  }

  function onUpdate(outcome?: Outcome, targetView?: OperationView) {
    if (targetView && targetView !== view) return
    if (outcome) report(outcome, frame.contains(document.activeElement))
    else {
      dialog?.close()
      message.textContent = ''
      render()
    }
  }

  function onSubmit(event: SubmitEvent) {
    const form = event.target
    if (!(form instanceof HTMLFormElement) || !form.dataset.op) return
    event.preventDefault()
    const data = new FormData(form)
    const value = (key: string) => String(data.get(key) ?? '')
    switch (form.dataset.op) {
      case 'receive':
        report(receiveUnits(records.receipt, value('item'), Number(value('count'))))
        break
      case 'putaway':
        report(putAway(records.receipt, value('bin')))
        break
      case 'pick':
        recordOrderStep('bag:' + value('bag').trim().toUpperCase(), pickBag(records.order, value('bag')))
        break
      case 'handoff':
        if (value('parcel').trim().toUpperCase() !== 'P-401') {
          report({
            message: 'Parcel reference must match P-401 before confirming handoff.',
            invalid: 'handoff-parcel'
          })
        } else if (records.order.stage === 'handoff') {
          confirmStage('handoff')
        }
        break
      case 'return':
        recordOrderStep('return', verifyReturn(records.order, value('bin'), data.has('condition')))
        break
    }
  }

  function startRead() {
    if (records.recovery.read === 'reading') return
    records.recovery.read = 'reading'
    report({ message: 'Reading three simulated marketplace quantities.', next: 'read-observations' })
    readTimer = setTimeout(() => {
      const outcome = finishRead(records.recovery)
      listeners.forEach((update) => update(outcome, 'recovery'))
    }, 350)
  }

  function openReview(trigger: HTMLButtonElement) {
    if (records.recovery.read !== 'current' || records.recovery.sent || dialog) return
    records.recovery.reviewed = true
    const current = document.createElement('dialog')
    dialog = current
    current.className = 'op-dialog'
    current.setAttribute('aria-labelledby', 'op-review-title')
    current.innerHTML = reviewMarkup
    current.addEventListener(
      'close',
      () => {
        records.recovery.reviewed = false
        dialog = undefined
        current.remove()
        if (trigger.isConnected) trigger.focus()
      },
      { once: true }
    )
    frame.append(current)
    current.showModal()
  }

  function onMouseDown(event: MouseEvent) {
    // Keep focus on the intended next field if a later press in the same gesture hits the replaced task.
    if (event.detail === 1) pointerTask = content.firstElementChild
    else if (event.detail > 1 && pointerTask !== content.firstElementChild) event.preventDefault()
  }

  function onClick(event: MouseEvent) {
    const target = event.target
    if (!(target instanceof Element)) return
    // The second click can land on a different control after the first replaces the task.
    if (event.detail > 1 && target.closest('button, summary, input[type=checkbox], label:has(input[type=checkbox])')) {
      event.preventDefault()
      return
    }
    const button = target.closest<HTMLButtonElement>('button[data-op]')
    if (!button || button.disabled || !frame.contains(button)) return
    switch (button.dataset.op) {
      case 'reverse-receipt':
        report(reverseReceiptStep(records.receipt, Number(button.dataset.step)))
        break
      case 'short':
        recordOrderStep('short', reportShort(records.order))
        break
      case 'amend':
        recordOrderStep('amend', amendOrder(records.order))
        break
      case 'confirm-pack':
        confirmStage('pack')
        break
      case 'queue-label':
        confirmStage('label')
        break
      case 'confirm-print':
        confirmStage('printing')
        break
      case 'receive-return':
        confirmStage('shipped')
        break
      case 'read':
        startRead()
        break
      case 'review':
        openReview(button)
        break
      case 'publish': {
        if (!dialog?.open) return
        const outcome = publishCorrection(records.recovery)
        dialog.close()
        report(outcome)
        break
      }
    }
  }

  function onChange(event: Event) {
    const select = event.target
    if (select instanceof HTMLSelectElement && select.name === 'read-result') {
      records.recovery.response = select.value === 'failure' ? 'failure' : 'success'
    }
  }

  function onKeyDown(event: KeyboardEvent) {
    if (!event.repeat) return
    const target = event.target
    const spaceActivates =
      event.key === ' ' && target instanceof Element && target.closest('button, summary, input[type=checkbox]')
    if (event.key === 'Enter' || spaceActivates) event.preventDefault()
  }

  frame.addEventListener('mousedown', onMouseDown)
  frame.addEventListener('click', onClick)
  frame.addEventListener('keydown', onKeyDown)
  frame.addEventListener('submit', onSubmit)
  frame.addEventListener('change', onChange)
  listeners.add(onUpdate)
  render()
  if (view === 'orders' && orderSession.notice) {
    message.textContent = orderSession.notice
    orderSession.notice = ''
  }
  return () => {
    listeners.delete(onUpdate)
    dialog?.close()
    frame.removeEventListener('mousedown', onMouseDown)
    frame.removeEventListener('click', onClick)
    frame.removeEventListener('keydown', onKeyDown)
    frame.removeEventListener('submit', onSubmit)
    frame.removeEventListener('change', onChange)
    frame.remove()
  }
}
