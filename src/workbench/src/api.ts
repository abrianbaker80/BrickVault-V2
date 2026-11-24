import type { components } from './schema'
import * as valid from './validators.cjs'

type Schema = components['schemas']
export type Session = Schema['Session']
export type Workspace = Schema['Workspace']
export type Catalog = Schema['CatalogRow']
export type Location = Schema['LocationRow']
export type Diagnostic = Schema['Diagnostic']
export type CatalogInput = Omit<Schema['CatalogCommand'], 'commandId' | 'id' | 'expectedRevision'>
export type LocationInput = Pick<Schema['LocationCommand'], 'code' | 'name'>
export type BinTransferSnapshot = Schema['BinTransferSnapshot']
export type TransferPosition = BinTransferSnapshot['positions'][number]
export type TransferCommand = Schema['TransferCommand']
export type TransferControls = { enabled: boolean; pending: boolean; feedback: string; workspaceId: string; dispatch: Dispatch }
export type ReceivingWorkspace = Schema['ReceivingWorkspace']
export type Purchase = Schema['PurchaseRow']
export type ReceivingCommand = { workspaceId: string } & (
  | { kind: 'purchase'; body: Schema['PurchaseCommand'] }
  | { kind: 'delivery'; body: Schema['DeliveryCommand'] }
  | { kind: 'putaway'; body: Schema['PutawayCommand'] }
  | { kind: 'offer'; body: Schema['OfferCommand'] }
  | { kind: 'transfer'; body: TransferCommand })
export type Command = ReceivingCommand | { workspaceId: string } & (
  | { kind: 'catalog'; body: Schema['CatalogCommand'] }
  | { kind: 'location'; body: Schema['LocationCommand'] })
export type Held = { workspaceId: string | null; commandId: string }
export type Dispatch = (command: ReceivingCommand) => Promise<Schema['Receipt'] | Schema['ReceivingReceipt'] | Schema['TransferReceipt'] | undefined>

export function commandLabel(command: Command) {
  switch (command.kind) {
    case 'catalog': case 'location': return command.body.name
    case 'purchase': return 'Purchase ' + command.body.reference
    case 'delivery': return 'Delivery ' + command.body.reference
    case 'putaway': return 'Putaway to ' + command.body.confirmedLocationCode
    case 'offer': return 'Unpublished offer'
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
  binTransfers: (id: string) => read('receiving/bins/' + encodeURIComponent(id) + '/movements', valid.BinTransferSnapshot),
  receiving: () => read('receiving', valid.ReceivingWorkspace),
  diagnostic: (id: string) => read('diagnostics/' + encodeURIComponent(id), valid.Diagnostic),
  async save(command: Command, csrf: string) {
    const options = {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Workspace-Id': command.workspaceId },
      body: JSON.stringify(command.body)
    }
    const path = commands[command.kind].path
    const receipt = command.kind === 'catalog' || command.kind === 'location'
      ? await read(path, valid.Receipt, options)
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
    else if (Object.hasOwn(commands, kind) && commands[kind as Command['kind']].valid(body)) queued.push(value)
    else throw new Error('Invalid recovery record')
  }
  return { commands: queued, held }
}

export function remember(command: Command) {
  const validBody = commands[command.kind].valid(command.body)
  if (!validBody) throw new InvalidCommand('Check the required fields and quantities before saving.')
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
