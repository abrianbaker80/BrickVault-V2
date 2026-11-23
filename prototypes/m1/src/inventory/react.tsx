import { useEffect, useLayoutEffect, useRef, useSyncExternalStore } from 'react'
import { flushSync } from 'react-dom'
import { createRoot } from 'react-dom/client'
import { partImage } from '../art'
import {
  authoritative,
  batches,
  createRegister,
  displayDialog,
  locations,
  metricRows,
  onHand,
  readModes,
  readText,
  registerInteractions,
  resultCount,
  scopes,
  sellable,
  statuses,
  stockStatus
} from './model'
import type { Controller, Lot, ReadMode, Scope } from './model'
import './inventory.css'

function Badge({ lot }: { lot: Lot }) {
  const [label, tone] = stockStatus(lot)
  return (
    <span className={'iv-badge ' + tone}>
      <i aria-hidden="true" />
      {label}
    </span>
  )
}

function Detail({ lot, close }: { lot: Lot; close: () => void }) {
  return (
    <>
      <div className="iv-panel-title">
        <h2>Lot inspector</h2>
        <button className="iv-icon-button" aria-label="Close inspector" onClick={close}>
          ×
        </button>
      </div>
      <div className="iv-detail-identity">
        <div className="iv-display-art">
          <img src={partImage(lot.art)} alt="" />
        </div>
        <div>
          <span className="iv-code">{lot.id}</span>
          <h3>{lot.name}</h3>
          <p>
            {lot.color} · {lot.condition}
          </p>
          <Badge lot={lot} />
        </div>
      </div>
      <div className="iv-detail-section">
        <h4>Stock position</h4>
        <dl className="iv-pairs">
          <dt>Physical on hand</dt>
          <dd>{onHand(lot)}</dd>
          <dt>Reserved</dt>
          <dd>{lot.reserved}</dd>
          <dt>Unavailable</dt>
          <dd>{lot.unavailable}</dd>
          <dt className="iv-em">Sellable</dt>
          <dd className="iv-em">{sellable(lot)}</dd>
        </dl>
      </div>
      <div className="iv-detail-section">
        <h4>Storage locations</h4>
        <ul className="iv-placements">
          {lot.placements.map((p) => (
            <li key={p.bin}>
              <span className="iv-bin">{p.bin}</span>
              <span>{p.units} units</span>
            </li>
          ))}
        </ul>
      </div>
      <div className="iv-detail-section">
        <h4>Marketplace</h4>
        <dl className="iv-pairs">
          <dt>Desired quantity</dt>
          <dd>{lot.desired}</dd>
          <dt>Desired at</dt>
          <dd>{lot.desiredAt}</dd>
          <dt>Last observed</dt>
          <dd>{lot.observed?.units ?? 'Unknown'}</dd>
          <dt>Observed at</dt>
          <dd>
            {lot.observed ? lot.observed.asOf + (lot.observed.stale ? ' · stale' : '') : 'No observation'}
          </dd>
        </dl>
      </div>
      <div className="iv-detail-section">
        <h4>Provenance</h4>
        <ul className="iv-sources">
          {lot.sources.map((source) => (
            <li key={source.id}>
              {source.id} · {source.name} · {source.units} units
            </li>
          ))}
        </ul>
        <p>Custody · {lot.custodian}</p>
        <small>
          Revision {lot.revision} · {lot.type} {lot.part}
        </small>
      </div>
    </>
  )
}

function Inventory({ api, host }: { api: Controller; host: HTMLElement }) {
  const s = useSyncExternalStore(api.subscribe, api.get)
  const detailDialog = useRef<HTMLDialogElement>(null)
  const previewDialog = useRef<HTMLDialogElement>(null)
  const search = useRef<HTMLInputElement>(null)
  const inspected = s.lots.find((l) => l.id === s.inspected)
  const rows = s.visible.map((id) => s.lots.find((l) => l.id === id)!)
  const canAct = authoritative(s)
  const filtered = Boolean(s.query || s.location || s.condition || s.batch || s.status)
  const showReadState = s.read !== 'Normal'
  useEffect(() => registerInteractions(host, api), [host, api])
  useLayoutEffect(() => {
    displayDialog(detailDialog.current, Boolean(inspected && s.compact))
    displayDialog(previewDialog.current, Boolean(s.preview || s.receipt))
    if (s.receipt) previewDialog.current?.querySelector<HTMLButtonElement>('[data-receipt-done]')?.focus()
  }, [inspected, s.compact, s.preview, s.receipt])
  function clear() {
    api.filter({ query: '', location: '', condition: '', batch: '', status: '' })
    search.current?.focus()
  }
  function inspect(id: string) {
    flushSync(() => api.inspect(id))
    if (!s.compact) host.querySelector<HTMLButtonElement>('.iv-inspector .iv-icon-button')?.focus()
  }
  function closeInspector() {
    if (!s.compact) host.querySelector<HTMLButtonElement>('[data-inspect="' + s.inspected + '"]')?.focus()
    api.inspect(null)
  }
  return (
    <div className="iv-root">
      <div className="iv-metrics" aria-label="Inventory totals">
        {metricRows(s).map((m) => (
          <div className="iv-metric" key={m.label}>
            <span>{m.label}</span>
            <div>
              <strong>{m.display}</strong> <small>{m.unit}</small>
            </div>
          </div>
        ))}
      </div>
      <div className={'iv-workspace' + (inspected && !s.compact ? ' has-inspector' : '')}>
        <section className="iv-register" aria-labelledby="iv-register-title">
          <div className="iv-register-head">
            <div className="iv-title-group">
              <h2 id="iv-register-title">Lot register</h2>
              <span className="iv-result-count" aria-live="polite">
                {resultCount(s)}
              </span>
            </div>
            <button
              className="iv-action"
              disabled={!canAct}
              onClick={() => api.openPreview(s.selected.length ? 'Selected lots' : 'Matching lots')}
            >
              Preview recount
            </button>
          </div>
          <div className="iv-toolbar">
            <label className="iv-search">
              <i className="iv-lens" aria-hidden="true" />
              <input
                type="search"
                data-inventory-search=""
                aria-label="Search inventory"
                placeholder="Search lots, parts, bins…"
                value={s.query}
                onChange={(e) => api.filter({ query: e.target.value })}
                ref={search}
              />
              <kbd aria-hidden="true">/</kbd>
            </label>
            <select
              aria-label="Filter condition"
              value={s.condition}
              onChange={(e) => api.filter({ condition: e.target.value })}
            >
              <option value="">Condition</option>
              <option>New</option>
              <option>Used</option>
            </select>
            <select
              aria-label="Filter location"
              value={s.location}
              onChange={(e) => api.filter({ location: e.target.value })}
            >
              <option value="">Location</option>
              {locations.map((x) => (
                <option key={x}>{x}</option>
              ))}
            </select>
            <select
              aria-label="Filter batch"
              value={s.batch}
              onChange={(e) => api.filter({ batch: e.target.value })}
            >
              <option value="">Batch</option>
              {batches.map((b) => (
                <option key={b.id}>{b.id}</option>
              ))}
            </select>
            <select
              aria-label="Filter stock status"
              value={s.status}
              onChange={(e) => api.filter({ status: e.target.value })}
            >
              <option value="">Status</option>
              {statuses.map((x) => (
                <option key={x}>{x}</option>
              ))}
            </select>
            {filtered && (
              <button className="iv-text-button" onClick={clear}>
                Clear filters
              </button>
            )}
          </div>
          {s.selected.length > 0 && (
            <div className="iv-selection">
              <strong>{s.selected.length} selected</strong>
              <span>{s.selected.filter((id) => !s.visible.includes(id)).length} outside current results</span>
              <button className="iv-text-button" onClick={api.clearSelection}>
                Clear selection
              </button>
            </div>
          )}
          {showReadState && (
            <div className="iv-read-banner" role="status">
              <span>{readText[s.read]}</span>
              <button className="iv-text-button" onClick={() => api.changeRead('Normal')}>
                Retry read
              </button>
            </div>
          )}
          <div className="iv-table-shell">
            <table aria-label="Inventory lots" aria-busy={s.pending || s.read === 'Loading'}>
              <thead>
                <tr>
                  <th scope="col" className="iv-check-col">
                    <span className="iv-sr">Select</span>
                  </th>
                  <th scope="col">Item / lot</th>
                  <th scope="col" className="iv-location">
                    Location
                  </th>
                  <th scope="col" className="iv-number iv-physical">
                    On hand
                  </th>
                  <th scope="col" className="iv-number">
                    Sellable
                  </th>
                  <th scope="col" className="iv-state-col">
                    Status
                  </th>
                </tr>
              </thead>
              <tbody>
                {rows.map((lot) => (
                  <tr key={lot.id} className={s.inspected === lot.id ? 'iv-inspected' : ''}>
                    <td className="iv-check-col">
                      <input
                        type="checkbox"
                        aria-label={'Select ' + lot.id}
                        checked={s.selected.includes(lot.id)}
                        disabled={!canAct}
                        onChange={() => api.toggle(lot.id)}
                      />
                    </td>
                    <td>
                      <button
                        className="iv-item"
                        data-inspect={lot.id}
                        onClick={() => inspect(lot.id)}
                        disabled={s.pending}
                      >
                        <span className="iv-sr">Inspect</span>
                        <span className="iv-thumb">
                          <img src={partImage(lot.art)} alt="" />
                        </span>
                        <span className="iv-item-name">
                          <strong>{lot.name}</strong>
                          <small>
                            {lot.color} · {lot.id}
                          </small>
                        </span>
                      </button>
                    </td>
                    <td className="iv-location">
                      <span className="iv-bin">{lot.placements[0].bin}</span>
                      {lot.placements.length > 1 && (
                        <small className="iv-more">+{lot.placements.length - 1}</small>
                      )}
                    </td>
                    <td className="iv-number iv-physical">{onHand(lot)}</td>
                    <td className="iv-number iv-free">{sellable(lot)}</td>
                    <td className="iv-state-col">
                      <Badge lot={lot} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!rows.length && !showReadState && !s.pending && (
              <div className="iv-empty">
                <strong>No matching lots</strong>
                <button className="iv-text-button" onClick={clear}>
                  Clear filters
                </button>
              </div>
            )}
          </div>
          <div className="iv-footer">
            <span role="status">
              {s.pending ? 'Updating results…' : s.read === 'Normal' ? 'Snapshot · 09:42' : s.read + ' read'}
            </span>
            <label>
              Read simulation{' '}
              <select
                value={s.read}
                onChange={(e) => api.changeRead(e.target.value as ReadMode)}
                aria-label="Read simulation"
              >
                {readModes.map((x) => (
                  <option key={x}>{x}</option>
                ))}
              </select>
            </label>
          </div>
        </section>
        {!s.compact && inspected && (
          <aside
            className="iv-inspector"
            aria-label="Lot inspector"
            onKeyDown={(e) => {
              if (e.key === 'Escape') closeInspector()
            }}
          >
            <Detail lot={inspected} close={closeInspector} />
          </aside>
        )}
      </div>
      <dialog
        ref={detailDialog}
        className="iv-dialog iv-inspector-dialog"
        aria-label="Lot inspector"
        onCancel={(e) => {
          e.preventDefault()
          closeInspector()
        }}
      >
        {s.compact && inspected && <Detail lot={inspected} close={closeInspector} />}
      </dialog>
      <dialog
        ref={previewDialog}
        className="iv-dialog iv-preview"
        aria-labelledby="iv-action-title"
        onCancel={(e) => {
          e.preventDefault()
          api.closePreview()
        }}
      >
        <div className="iv-panel-title">
          <h2 id="iv-action-title">{s.receipt ? 'Recount receipt' : 'Preview recount'}</h2>
          <button className="iv-icon-button" aria-label="Close recount" onClick={api.closePreview}>
            ×
          </button>
        </div>
        {s.preview && (
          <div className="iv-dialog-body">
            <label className="iv-field">
              Action scope
              <select value={s.scope} onChange={(e) => api.openPreview(e.target.value as Scope)}>
                {scopes.map((x) => (
                  <option key={x}>{x}</option>
                ))}
              </select>
            </label>
            <p>
              <strong>
                {s.preview.length} {s.preview.length === 1 ? 'lot' : 'lots'}
              </strong>{' '}
              in this preview. Queue a stock recount for these IDs. Quantities stay the same.
            </p>
            <ul className="iv-targets" aria-label="Frozen targets" tabIndex={0}>
              {s.preview.map((t) => (
                <li key={t.id}>
                  <b>{t.id}</b>
                  <span>{s.lots.find((l) => l.id === t.id)?.name}</span>
                  <small>rev {t.revision}</small>
                </li>
              ))}
            </ul>
            {!!s.preview.length && (
              <button className="iv-text-button" disabled={s.simulated} onClick={api.simulateChange}>
                Simulate revision change
              </button>
            )}
            {s.simulated && <p role="status">The first target changed after preview. It will be skipped.</p>}
            <div className="iv-dialog-actions">
              <button className="iv-action" onClick={api.closePreview}>
                Cancel
              </button>
              <button className="iv-primary" disabled={!s.preview.length} onClick={api.confirm}>
                Queue recount
              </button>
            </div>
          </div>
        )}
        {s.receipt && (
          <div className="iv-dialog-body">
            <p role="status">
              <strong>
                {s.receipt.queued.length} queued · {s.receipt.changed.length} changed ·{' '}
                {s.receipt.alreadyQueued.length} already queued
              </strong>
            </p>
            {s.receipt.queued.length > 0 && <p>Queued: {s.receipt.queued.join(', ')}</p>}
            {s.receipt.alreadyQueued.length > 0 && (
              <p>Already queued: {s.receipt.alreadyQueued.join(', ')}</p>
            )}
            {s.receipt.changed.length > 0 && (
              <p>
                Skipped: {s.receipt.changed.join(', ')}. Open a new preview to review the latest revision.
              </p>
            )}
            <button className="iv-primary" data-receipt-done="" onClick={api.closePreview}>
              Done
            </button>
          </div>
        )}
      </dialog>
    </div>
  )
}

export function mountInventory(host: HTMLElement) {
  const api = createRegister(host.clientWidth)
  const root = createRoot(host)
  root.render(<Inventory api={api} host={host} />)
  return () => {
    root.unmount()
    api.dispose()
  }
}
