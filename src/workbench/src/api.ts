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
export type Command = { workspaceId: string } & (
  | { kind: 'catalog'; body: Schema['CatalogCommand'] }
  | { kind: 'location'; body: Schema['LocationCommand'] })
export type Held = { workspaceId: string | null; commandId: string }

export class ApiFailure extends Error {
  constructor(readonly status: number, readonly detail?: Schema['Error']) {
    super(detail?.message || 'The server could not complete the request.')
  }
}

export class ContractFailure extends Error {
  constructor() { super('This Workbench could not verify the server response. Check the application version.') }
}

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
  workspace: () => read('workspace', valid.Workspace),
  diagnostic: (id: string) => read('diagnostics/' + encodeURIComponent(id), valid.Diagnostic),
  async save(command: Command, csrf: string) {
    const path = command.kind === 'catalog' ? 'catalog' : 'locations'
    const receipt = await read(path, valid.Receipt, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Workspace-Id': command.workspaceId },
      body: JSON.stringify(command.body)
    })
    if (receipt.commandId !== command.body.commandId || receipt.kind !== command.kind) throw new ContractFailure()
    return receipt
  }
}

const legacy = 'brickvault.v1.pending.'
const prefix = 'brickvault.v2.pending.'
const uuid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i
const journalKey = (workspaceId: string, commandId: string) => prefix + workspaceId + '.' + commandId
export function readJournal(activeId: string) {
  const commands: Command[] = []
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
    else if (kind === 'catalog' && valid.CatalogCommand(body)) commands.push({ workspaceId, kind, body })
    else if (kind === 'location' && valid.LocationCommand(body)) commands.push({ workspaceId, kind, body })
    else throw new Error('Invalid recovery record')
  }
  return { commands, held }
}

export function remember(command: Command) {
  const validBody = command.kind === 'catalog' ? valid.CatalogCommand(command.body) : valid.LocationCommand(command.body)
  if (!validBody) throw new Error('Invalid command')
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
