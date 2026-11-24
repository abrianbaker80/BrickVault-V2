import type { components, operations } from './schema'
import * as valid from './validators.cjs'

type Schema = components['schemas']
export type Session = Schema['Session']
export type Workspace = Schema['Workspace']
export type Catalog = Schema['CatalogRow']
export type Location = Schema['LocationRow']
export type Diagnostic = Schema['Diagnostic']
export type CatalogInput = Omit<Schema['CatalogCommand'], 'commandId' | 'id' | 'expectedRevision'>
export type LocationInput = Pick<Schema['LocationCommand'], 'code' | 'name'>
export type StockPage = Schema['StockRegisterPage']
export type PositionMovementPage = Schema['PositionMovementPage']
export type StockPosition = Schema['StockRegisterRow']
export type StockQuery = NonNullable<operations['searchStockPositions']['parameters']['query']>
export type BinTransferSnapshot = Schema['BinTransferSnapshot']
export type CountSnapshot = Schema['BinCountSnapshot']
export type CountPosition = Schema['CountPosition']
export type CountObservation = Schema['StockCountObservation']
export type TransferPosition = BinTransferSnapshot['positions'][number]
export type TransferCommand = Schema['TransferCommand']
export type TransferControls = { enabled: boolean; pending: boolean; feedback: string; workspaceId: string; dispatch: Dispatch }
export type ReceivingWorkspace = Schema['CurrentReceivingWorkspace']
export type Purchase = Schema['CurrentPurchaseRow']
export type ReceivingCommand = { workspaceId: string } & (
  | { kind: 'purchase'; body: Schema['PurchaseCommand'] }
  | { kind: 'delivery'; body: Schema['DeliveryCommand'] }
  | { kind: 'putaway'; body: Schema['PutawayCommand'] }
  | { kind: 'offer'; body: Schema['OfferCommand'] }
  | { kind: 'transfer'; body: TransferCommand }
  | { kind: 'count'; body: Schema['StockCountCommand'] }
  | { kind: 'hold'; body: Schema['StockHoldCommand'] }
  | { kind: 'hold-release'; locationId: string; positionId: string; body: Schema['StockHoldReleaseCommand'] })
export type Command = ReceivingCommand | { workspaceId: string } & (
  | { kind: 'catalog'; body: Schema['CatalogCommand'] }
  | { kind: 'location'; body: Schema['LocationCommand'] })
export type Held = { workspaceId: string | null; commandId: string }
export type Dispatch = (command: ReceivingCommand) => Promise<Schema['Receipt'] | Schema['ReceivingReceipt'] | Schema['TransferReceipt'] | Schema['StockCountReceipt'] | Schema['StockHoldReceipt'] | undefined>

export function commandLabel(command: Command) {
  switch (command.kind) {
    case 'catalog': case 'location': return command.body.name
    case 'purchase': return 'Purchase ' + command.body.reference
    case 'delivery': return 'Delivery ' + command.body.reference
    case 'putaway': return 'Putaway to ' + command.body.confirmedLocationCode
    case 'offer': return 'Unpublished offer'
    case 'hold': return 'Build hold for ' + command.body.purpose
    case 'hold-release': return 'Release of ' + command.body.quantity + ' held units'
    case 'count': return 'Stock count of ' + command.body.countedQuantity + ' in ' + command.body.confirmedLocationCode
    case 'transfer': return 'Transfer of ' + command.body.quantity + ' from ' + command.body.confirmedSourceLocationCode + ' to ' + command.body.confirmedDestinationLocationCode
  }
}

const commands = {
  catalog: { path: 'catalog', valid: valid.CatalogCommand },
  location: { path: 'locations', valid: valid.LocationCommand },
  purchase: { path: 'receiving/purchases', valid: valid.PurchaseCommand },
  delivery: { path: 'receiving/deliveries', valid: valid.DeliveryCommand },
  putaway: { path: 'receiving/putaway', valid: valid.PutawayCommand },
  offer: { path: 'receiving/offers', valid: valid.OfferCommand },
  count: { path: 'receiving/counts', valid: valid.StockCountCommand },
  hold: { path: 'receiving/holds', valid: valid.StockHoldCommand },
  'hold-release': { path: 'receiving/holds/release', valid: valid.StockHoldReleaseCommand },
  transfer: { path: 'receiving/transfers', valid: valid.TransferCommand }
}

export class ApiFailure extends Error {
  constructor(readonly status: number, readonly detail?: Schema['Error']) {
    super(detail?.message || 'The server could not complete the request.')
  }
}

export class ContractFailure extends Error {
  constructor() { super('This Workbench could not verify the server response. Check the application version.') }
}

export class InvalidCommand extends Error {}

async function request(path: string, options: RequestInit = {}) {
  if (window.location.protocol !== 'https:') throw new Error('Open the private HTTPS address to connect.')
  const response = await fetch('/api/v1/' + path, {
    ...options, cache: 'no-store', mode: 'same-origin', credentials: 'same-origin', redirect: 'error',
    signal: AbortSignal.timeout(15000),
    headers: { Accept: 'application/json', ...options.headers }
  })
  if (!response.ok) {
    const body: unknown = await response.json().catch(() => null)
    throw new ApiFailure(response.status, valid.Error(body) ? body : undefined)
  }
  return response
}

async function read<T>(path: string, guard: (value: unknown) => value is T, options?: RequestInit) {
  const response = await request(path, options)
  const data: unknown = await response.json().catch(() => null)
  if (!guard(data)) throw new ContractFailure()
  return data
}

export const api = {
  session: () => read('session', valid.Session),
  signIn(password: string) {
    const bytes = new TextEncoder().encode('operator:' + password)
    const basic = btoa(Array.from(bytes, byte => String.fromCharCode(byte)).join(''))
    return read('session', valid.Session, { method: 'POST', headers: { Authorization: 'Basic ' + basic } })
  },
  async signOut(csrf: string) {
    await request('session', { method: 'DELETE', headers: { 'X-CSRF-Token': csrf } })
  },
  workspace: (allAudit = false) => read(allAudit ? 'workspace?audit=all' : 'workspace', valid.Workspace),
  positions(query: StockQuery) {
    const params = new URLSearchParams()
    for (const [key, value] of Object.entries(query))
      if (value !== undefined && value !== '') params.set(key, String(value))
    return read('inventory/positions?' + params, valid.StockRegisterPage)
  },
  positionMovements(id: string, after: string) {
    const query = new URLSearchParams({ limit: '25' })
    if (after) query.set('after', after)
    return read('inventory/positions/' + encodeURIComponent(id) + '/movements?' + query, valid.PositionMovementPage)
  },
  async positionHolds(binId: string, positionId: string, after = '', commandId = '') {
    const params = new URLSearchParams({ limit: '25' })
    if (after) params.set('after', after)
    if (commandId) params.set('commandId', commandId)
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/positions/' +
      encodeURIComponent(positionId) + '/holds?' + params, valid.StockHoldPositionSnapshot)
    if (result.location.id !== binId || result.positionId !== positionId ||
        (commandId && (result.holds.length !== 1 || result.holds[0].commandId !== commandId))) throw new ContractFailure()
    return result
  },
  async holdReleases(binId: string, positionId: string, holdId: string, after = '', commandId = '') {
    const params = new URLSearchParams({ limit: '25' })
    if (after) params.set('after', after)
    if (commandId) params.set('commandId', commandId)
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/positions/' +
      encodeURIComponent(positionId) + '/holds/' + encodeURIComponent(holdId) + '/releases?' + params, valid.StockHoldReleasePage)
    if (result.holdId !== holdId || (commandId && (result.releases.length !== 1 ||
        result.releases[0].commandId !== commandId))) throw new ContractFailure()
    return result
  },
  async binCounts(binId: string, query: { positionId?: string; observationId?: string; commandId?: string; after?: string; limit?: string }) {
    const result = await read('receiving/bins/' + encodeURIComponent(binId) + '/counts?' + new URLSearchParams(query), valid.BinCountSnapshot)
    if (result.location.id !== binId || (query.positionId && result.positionId !== query.positionId) ||
        result.observations.some(o => o.locationId !== binId || o.positionId !== result.positionId ||
          (query.observationId && o.id !== query.observationId) || (query.commandId && o.commandId !== query.commandId)) ||
        result.positions.some(p => p.positionId !== result.positionId) || result.shortages.some(s => s.positionId !== result.positionId) ||
        ((query.observationId || query.commandId) && result.complete && result.observations.length !== 1)) throw new ContractFailure()
    return result
  },
  binTransfers: (id: string) => read('receiving/bins/' + encodeURIComponent(id) + '/movements', valid.BinTransferSnapshot),
  receiving: () => read('receiving/current?holds=true', valid.CurrentReceivingWorkspace),
  diagnostic: (id: string) => read('diagnostics/' + encodeURIComponent(id), valid.Diagnostic),
  async save(command: Command, csrf: string) {
    const options = {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Workspace-Id': command.workspaceId },
      body: JSON.stringify(command.body)
    }
    const path = commands[command.kind].path
    const receipt = command.kind === 'catalog' || command.kind === 'location'
      ? await read(path, valid.Receipt, options)
      : command.kind === 'hold' || command.kind === 'hold-release' ? await read(path, valid.StockHoldReceipt, options)
      : command.kind === 'count' ? await read(path, valid.StockCountReceipt, options)
      : command.kind === 'transfer' ? await read(path, valid.TransferReceipt, options) : await read(path, valid.ReceivingReceipt, options)
    if (receipt.commandId !== command.body.commandId || receipt.kind !== command.kind) throw new ContractFailure()
    return receipt
  }
}

const legacy = 'brickvault.v1.pending.'
const prefix = 'brickvault.v2.pending.'
const uuid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
const journalKey = (workspaceId: string, commandId: string) => prefix + workspaceId + '.' + commandId
export function readJournal(activeId: string) {
  const queued: Command[] = []
  const held: Held[] = []
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith(legacy)) {
      held.push({ workspaceId: null, commandId: key.slice(legacy.length) })
      continue
    }
    if (!key.startsWith(prefix)) continue
    const raw = localStorage.getItem(key)
    if (raw === null) continue
    const value = JSON.parse(raw)
    const { workspaceId, kind, body } = value
    if (!uuid.test(workspaceId) || key !== journalKey(workspaceId, body.commandId)) throw new Error('Invalid recovery identity')
    if (workspaceId !== activeId) held.push({ workspaceId, commandId: body.commandId })
    else if (Object.hasOwn(commands, kind) && commands[kind as Command['kind']].valid(body) &&
        (kind !== 'hold-release' || (uuid.test(value.locationId) && uuid.test(value.positionId)))) queued.push(value)
    else throw new Error('Invalid recovery record')
  }
  return { commands: queued, held }
}

export function remember(command: Command) {
  const validBody = commands[command.kind].valid(command.body)
  if (!validBody || (command.kind === 'hold-release' && (!uuid.test(command.locationId) || !uuid.test(command.positionId)))) throw new InvalidCommand('Check the required fields and quantities before saving.')
  localStorage.setItem(journalKey(command.workspaceId, command.body.commandId), JSON.stringify(command))
}
export function forget(command: Command) {
  localStorage.removeItem(journalKey(command.workspaceId, command.body.commandId))
}

export function explain(error: unknown) {
  if (error instanceof ApiFailure) {
    if (error.detail) return error.message + ' Reference ' + error.detail.correlationId + '.'
    if (error.status === 401) return 'Sign in to access the workspace.'
    if (error.status === 403) return 'Access was denied. Check your session and try again.'
    return 'The server returned HTTP ' + error.status + '.'
  }
  if (error instanceof ContractFailure) return error.message
  return 'The service could not be reached. Check the private connection and retry.'
}
