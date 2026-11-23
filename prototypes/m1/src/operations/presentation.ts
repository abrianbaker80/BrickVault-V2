import { partImage } from '../art'
import { bags, orderCounts, receiptBin, receiptPart, receiptQuantity } from './model'
import type { EventEntry, Order, Receipt, Recovery } from './model'

function item(key: string, name: string, meta: string, qty: string) {
  return `<div class="op-item">
    <span class="op-art"><img src="${partImage(key)}" alt=""></span>
    <div class="op-item-copy"><strong>${name}</strong><small>${meta}</small></div>
    <strong class="op-item-qty">${qty}</strong>
  </div>`
}

function steps(labels: string[], active: number) {
  return `<ol class="op-steps" aria-label="Workflow progress">${labels
    .map((label, i) => {
      const state = i < active ? 'complete' : i === active ? 'current' : 'upcoming'
      return `<li class="op-step op-step-${state}" ${state === 'current' ? 'aria-current="step"' : ''}><span class="op-step-dot" aria-hidden="true">${state === 'complete' ? '&#10003;' : i + 1}</span>${label}<span class="op-sr"> · ${state}</span></li>`
    })
    .join('')}</ol>`
}

function panel(heading: string, content: string) {
  return `<section class="op-panel"><h3>${heading}</h3>${content}</section>`
}

function pairs(rows: [string, number | string][]) {
  return `<dl class="op-pairs">${rows.map(([label, value]) => `<div><dt>${label}</dt><dd>${value}</dd></div>`).join('')}</dl>`
}

function activity(events: EventEntry[]) {
  return panel(
    'Activity',
    `<ol class="op-activity">${events.map((e) => `<li><strong>${e.title}</strong><span>${e.detail}</span></li>`).join('')}</ol>`
  )
}

function tag(label: string, state: 'normal' | 'ready' | 'hold' = 'normal') {
  return `<span class="op-tag op-tag-${state}"><span aria-hidden="true">${state === 'ready' ? '&#10003;' : state === 'hold' ? '!' : '&#8226;'}</span>${label}</span>`
}

function task(context: string, heading: string, status: string, body: string) {
  return `<section class="op-task">
    <header class="op-task-header"><div><p class="op-reference">${context}</p><h2 id="op-task-heading" tabindex="-1">${heading}</h2></div>${status}</header>
    ${body}
  </section>`
}

export function receiptView(r: Receipt) {
  const staged = r.counted - r.stored
  const remaining = receiptQuantity - r.counted
  const state = staged ? 'putaway' : remaining ? 'intake' : 'done'
  const last = r.reversible.at(-1)
  const undo = last
    ? `<details class="op-details"><summary>Undo last receipt step</summary>
    <p>Step ${last.id} · ${last.kind === 'putaway' ? `Move ${last.quantity} units from ${receiptBin} back to the intake tray.` : `Remove ${last.quantity} units from the intake tray and return them to the expected count.`}</p>
    <button type="button" class="op-secondary" data-op="reverse-receipt" data-step="${last.id}">${last.kind === 'putaway' ? 'Reverse putaway' : 'Reverse receipt'}</button></details>`
    : ''
  const form =
    state === 'intake'
      ? `
    <form data-op="receive" class="op-form" novalidate>
      <div class="op-fields">
        <label>Item code<input id="receipt-item" name="item" type="text" placeholder="${receiptPart}" autocomplete="off" spellcheck="false"></label>
        <label>Units received<input id="receipt-count" name="count" type="number" min="1" max="${remaining}" value="${remaining}" inputmode="numeric"></label>
      </div><p class="op-hint">Scan or type the item code and count the units in front of you.</p>
      <button class="op-primary" type="submit">Receive units</button>
    </form>`
      : state === 'putaway'
        ? `
    <div class="op-route"><span>Intake tray</span><i aria-hidden="true">→</i><strong>Bin ${receiptBin}</strong><small>${staged} units to move</small></div>
    <form data-op="putaway" class="op-form" novalidate>
      <label>Destination bin<input id="receipt-bin" name="bin" type="text" placeholder="${receiptBin}" autocomplete="off" spellcheck="false"></label>
      <p class="op-hint">Confirm the bin after placing the units. Until then they remain unavailable.</p>
      <button class="op-primary" type="submit">Confirm putaway</button>
    </form>`
        : `
    <div class="op-result"><span class="op-result-icon" aria-hidden="true">✓</span><div><h3>Putaway complete</h3><p>All ${receiptQuantity} units are in bin ${receiptBin} and available for sale.</p></div></div>`
  const main = task(
    'Receipt RCV-104 · Purchase PO-208',
    'Blue plate delivery',
    tag(
      state === 'done'
        ? 'Stored'
        : state === 'intake'
          ? r.stored
            ? 'Partially received'
            : 'Awaiting receipt'
          : 'Awaiting putaway',
      state === 'done' ? 'ready' : 'normal'
    ),
    steps(['Receive', 'Put away', 'Stored'], state === 'done' ? 3 : state === 'intake' ? 0 : 1) +
      item('plate-blue', 'Plate 2 × 4', `Blue · New · ${receiptPart}`, `${receiptQuantity} units`) +
      form +
      undo
  )
  const eligibility = staged
    ? `${staged} in intake blocked until bin ${receiptBin} is confirmed.`
    : r.stored
      ? `Bin ${receiptBin} verified. Stored units qualify for the listing preview.`
      : 'Publication blocked until units are received and stored.'
  const side =
    panel(
      'Receipt balance',
      pairs([
        ['Expected', receiptQuantity],
        ['Still to receive', remaining],
        ['In intake tray', staged],
        [`In bin ${receiptBin}`, r.stored],
        ['Sellable', r.stored]
      ])
    ) +
    panel(
      'Price and publication',
      pairs([
        ['Preview price', '$0.18 USD / unit'],
        ['Eligible units', r.stored]
      ]) + `<p class="op-hint op-publication-note">${eligibility}</p>`
    ) +
    activity(r.events)
  return `<div class="op-grid"><div>${main}</div><aside class="op-side" aria-label="Receipt details">${side}</aside></div>`
}

const stageTitles: Record<Order['stage'], string> = {
  pick: 'Pick the order',
  short: 'Resolve the short count',
  pack: 'Pack the order',
  label: 'Prepare parcel label',
  printing: 'Verify the printed label',
  handoff: 'Confirm carrier handoff',
  shipped: 'Parcel handed to carrier',
  return: 'Inspect the return',
  restocked: 'Return verified'
}
const orderStatuses: Record<Order['stage'], string> = {
  pick: 'Picking',
  short: 'Blocked · short count',
  pack: 'Ready to pack',
  label: 'Packed',
  printing: 'Print pending',
  handoff: 'Ready for handoff',
  shipped: 'Handed off',
  return: 'Return hold',
  restocked: 'Restocked'
}

function pickForm(o: Order) {
  const short = o.stage === 'short'
  const missing = (bags(o).length - o.scanned.length) * 2
  const codes = bags(o)
    .map((b) => {
      const picked = o.scanned.includes(b)
      return `<li class="${picked ? 'is-picked' : ''}"><code>${b}</code><span>2 units</span><span>${picked ? '✓ In tote' : 'Awaiting scan'}</span></li>`
    })
    .join('')
  return `${short ? `<div class="op-warning"><strong>Packing blocked</strong><p>The order line is short by ${missing} units. Recount bin A-07 and scan each recovered bag.</p></div>` : ''}
    <div class="op-route"><span>Bin A-07</span><i aria-hidden="true">→</i><strong>Tote T-04</strong><small>${o.scanned.length * 2} of ${bags(o).length * 2} units picked</small></div>
    <ul class="op-bags" aria-label="Allocated bag labels">${codes}</ul>
    <form data-op="pick" class="op-form" novalidate>
      <label>${short ? 'Recovered bag label' : 'Bag label'}<input id="order-scan" name="bag" spellcheck="false" autocomplete="off" placeholder="${bags(o).find((b) => !o.scanned.includes(b))}"></label>
      <div class="op-actions"><button class="op-primary" type="submit">${short ? 'Confirm recovered bag' : 'Confirm bag'}</button>
      ${short ? '' : '<button type="button" class="op-secondary" data-op="short">Report short count</button>'}</div>
    </form>`
}

function stateAction(action: string, heading: string, description: string, label: string) {
  return `<div class="op-instruction"><h3>${heading}</h3><p>${description}</p><button type="button" class="op-primary" id="order-advance" data-op="${action}">${label}</button></div>`
}

function orderAction(o: Order) {
  switch (o.stage) {
    case 'pick':
    case 'short':
      return pickForm(o)
    case 'pack':
      return stateAction(
        'advance',
        'Move the complete tote into parcel P-401',
        'Compare the line against the tote, check the count, and seal the parcel. Packed stock stays physically on hand.',
        'Confirm packed'
      )
    case 'label':
      return stateAction(
        'advance',
        'Parcel P-401 is sealed',
        'Queue its simulated parcel label. The order stays reserved until carrier handoff.',
        'Queue parcel label'
      )
    case 'printing':
      return (
        `<div class="op-warning"><strong>Handoff blocked · print pending</strong><p>A queue acknowledgement cannot confirm paper output. Check the parcel label before continuing.</p></div>` +
        stateAction(
          'advance',
          'Match label LBL-401 to parcel P-401',
          'Confirm the printed reference on the parcel.',
          'Confirm printed label'
        )
      )
    case 'handoff':
      return `<form data-op="handoff" class="op-form" novalidate>
      <label>Parcel reference<input id="handoff-parcel" name="parcel" type="text" placeholder="P-401" autocomplete="off" spellcheck="false"></label>
      <p class="op-hint">Confirm P-401 only once it is in the carrier’s custody. This releases the reservation and reduces physical stock.</p>
      <button class="op-primary" type="submit">Confirm carrier handoff</button>
    </form>`
    case 'shipped':
      return `<div class="op-result"><span class="op-result-icon" aria-hidden="true">✓</span><div><h3>Fulfillment complete</h3><p>The parcel has left custody and the order reservation has been released.</p></div></div>
      <details class="op-details"><summary>Process a full return</summary>${stateAction('advance', 'Receive the returned parcel', 'Receive every unit into the returns tray. Returned units are unavailable until inspected.', 'Receive returned units')}</details>`
    case 'return':
      return `<div class="op-warning"><strong>Held for inspection</strong><p>Returned units are in the returns tray. Verify their condition and final bin before release.</p></div>
      <form class="op-form" data-op="return" novalidate><label>Return bin<input id="return-bin" name="bin" placeholder="A-07" autocomplete="off"></label>
      <label class="op-check"><input id="return-condition" name="condition" type="checkbox">I verified the condition of every returned unit.</label>
      <button class="op-primary" type="submit">Release to sellable stock</button></form>`
    case 'restocked':
      return `<div class="op-result"><span class="op-result-icon" aria-hidden="true">✓</span><div><h3>Return complete</h3><p>Condition and bin A-07 are verified. The returned units are available again.</p></div></div>`
  }
}

export function orderView(o: Order) {
  const { onHand, reserved, unavailable, sellable, quantity } = orderCounts(o)
  const step =
    o.stage === 'pick' || o.stage === 'short'
      ? 0
      : o.stage === 'pack'
        ? 1
        : o.stage === 'label' || o.stage === 'printing'
          ? 2
          : o.stage === 'handoff'
            ? 3
            : 4
  const status = tag(
    orderStatuses[o.stage],
    ['short', 'printing', 'return'].includes(o.stage) ? 'hold' : step === 4 ? 'ready' : 'normal'
  )
  const main = task(
    'Order BV-401 · Parcel P-401',
    stageTitles[o.stage],
    status,
    steps(['Pick', 'Pack', 'Label', 'Handoff'], step) +
      item('brick-red', 'Brick 2 × 4', 'Red · New · BR-3001', `${quantity} units`) +
      orderAction(o)
  )
  const amend =
    !o.amended && (o.stage === 'pick' || o.stage === 'short')
      ? '<details class="op-details op-amend"><summary>Order amendment</summary><p>Apply the authorized fixture amendment for one additional two-unit bag.</p><button type="button" data-op="amend" class="op-secondary">Apply amendment (+2 units)</button></details>'
      : ''
  const side =
    panel(
      'Order details',
      pairs([
        ['Order', 'BV-401'],
        ['Payment', 'Paid'],
        ['Last marketplace stage', 'Processing'],
        ['Tote', 'T-04']
      ]) + amend
    ) +
    panel(
      'Lot custody',
      pairs([
        ['Physical on hand', onHand],
        ['Reserved', reserved],
        ['Unavailable', unavailable],
        ['Sellable', sellable]
      ])
    ) +
    activity(o.events)
  return `<div class="op-grid"><div>${main}</div><aside class="op-side" aria-label="Order details">${side}</aside></div>`
}

export function recoveryView(r: Recovery) {
  const prior = r.successfulReads > 0
  const blocked = r.read === 'failed' || r.read === 'reading'
  const unknown = !prior
  const last =
    r.read === 'reading'
      ? 'Read in progress'
      : r.read === 'failed'
        ? 'Read failed · prior values stale'
        : prior
          ? `Successful read ${r.successfulReads}`
          : 'Prior observations'
  const entries = [
    {
      id: 'RC-01',
      name: 'Plate 1 × 2',
      key: 'plate-black',
      desired: 0,
      observed: prior ? 0 : 4,
      status: r.read === 'failed' ? 'Stale' : unknown ? 'Send unknown' : 'Verified zero'
    },
    {
      id: 'RC-02',
      name: 'Brick 2 × 4',
      key: 'brick-yellow',
      desired: 12,
      observed: 12,
      status: r.read === 'failed' ? 'Stale' : 'Matches'
    },
    {
      id: 'RC-03',
      name: 'Slope 45° 2 × 2',
      key: 'slope-green',
      desired: 6,
      observed: r.verified ? 6 : 9,
      status:
        r.read === 'failed' ? 'Stale' : r.verified ? 'Matches' : r.sent ? 'Awaiting read-back' : 'Mismatch'
    }
  ]
  const list = `<div class="op-table-wrap"><table class="op-recovery-table"><caption class="op-sr">Publication targets for batch SY-09</caption><thead><tr><th scope="col">Lot</th><th scope="col">Intended</th><th scope="col">Last read</th><th scope="col">State</th></tr></thead><tbody>${entries
    .map(
      (e) => `<tr>
    <th scope="row"><span class="op-table-item"><img alt="" src="${partImage(e.key)}"><span>${e.name}<small>${e.id}</small></span></span></th>
    <td>${e.desired}</td><td>${e.observed}</td><td>${tag(e.status, ['Matches', 'Verified zero'].includes(e.status) ? 'ready' : 'hold')}</td></tr>`
    )
    .join('')}</tbody></table></div>`
  const reason =
    r.read === 'reading'
      ? 'Reading the three target quantities…'
      : r.read === 'failed'
        ? 'The read failed. Prior quantities are displayed as stale. Read again before publishing.'
        : !prior
          ? 'A response was lost after send. Refresh the observations before deciding whether a correction is needed.'
          : r.verified
            ? 'All three target quantities match the fresh read.'
            : r.sent
              ? 'The correction was acknowledged. Read the target again to verify its resulting quantity.'
              : 'RC-03 differs from its confirmed sellable quantity. Review the effect before publishing.'
  const mismatch =
    r.read === 'current' && !r.sent
      ? '<button type="button" id="review-mismatch" data-op="review" class="op-secondary">Review mismatch</button>'
      : ''
  const main = task(
    'Publication batch SY-09 · 3 lots',
    'Reconcile stock quantities',
    tag(r.verified && !blocked ? 'Verified' : 'Needs review', r.verified && !blocked ? 'ready' : 'hold'),
    list +
      `<div class="op-recovery-action"><h3>${last}</h3><p>${reason}</p><div class="op-actions"><button type="button" id="read-observations" class="op-primary" data-op="read" ${r.read === 'reading' ? 'aria-disabled="true"' : ''}>${r.read === 'reading' ? 'Reading…' : 'Read observations'}</button>${mismatch}</div></div>`
  )
  const side =
    panel(
      'Batch details',
      pairs([
        ['Target scope', '3 explicit lots'],
        ['Channel', 'Simulated storefront'],
        ['Read result', last]
      ])
    ) +
    panel(
      'Read simulation',
      `<label class="op-label">Next read<select id="read-result" name="read-result" ${r.read === 'reading' ? 'disabled' : ''}><option value="success" ${r.response === 'success' ? 'selected' : ''}>Return observations</option><option value="failure" ${r.response === 'failure' ? 'selected' : ''}>Return read error</option></select></label>`
    ) +
    activity(r.events)
  return `<div class="op-grid"><div>${main}</div><aside class="op-side" aria-label="Recovery details">${side}</aside></div>`
}

export const reviewMarkup = `<form class="op-review-body" method="dialog"><p class="op-reference">Publication preview · SY-09</p><h2 id="op-review-title">Correct one lot</h2>
  <p>Set RC-03 to its confirmed sellable quantity. The latest observation differs by 3 units.</p>
  ${pairs([
    ['Lot', 'RC-03'],
    ['Last marketplace read', 9],
    ['Intended quantity', 6],
    ['Impact', '−3 units']
  ])}
  <p class="op-hint">Only RC-03 is included. A fresh read will verify the correction.</p>
  <div class="op-actions"><button class="op-primary" type="button" data-op="publish">Publish 6 units</button><button class="op-secondary" autofocus value="cancel">Cancel</button></div></form>`
