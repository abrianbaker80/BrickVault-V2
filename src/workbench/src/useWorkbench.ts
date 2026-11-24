import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch, type Ref } from 'vue'
import { api, ApiFailure, commandLabel, explain, forget, InvalidCommand, readJournal, remember } from './api'
import type { CatalogInput, Command, Held, LocationInput, ReceivingWorkspace, Session, Workspace } from './api'

type ReadState = 'loading' | 'ready' | 'disconnected' | 'partial'
export function useWorkbench(page: Ref<string>) {
  const session = shallowRef<Session | null>(null)
  const workspace = shallowRef<Workspace | null>(null)
  const receiving = shallowRef<ReceivingWorkspace | null>(null)
  const signedOut = ref(false)
  const readState = ref<ReadState>('loading')
  const error = ref('')
  const notice = ref<{ message: string; correlationId: string } | null>(null)
  const pending = shallowRef<Command[]>([])
  const held = shallowRef<Held[]>([])
  const journalError = ref('')
  const authBusy = ref(false)
  const submitting = ref(false)
  let generation = 0
  let timer: number | undefined

  function loadPending() {
    if (!session.value) return
    try {
      const journal = readJournal(session.value.workspaceId)
      pending.value = journal.commands
      held.value = journal.held
      journalError.value = ''
    } catch {
      journalError.value = 'Command recovery storage is unavailable or damaged. New commands are paused.'
    }
  }
  function clearSession(message = '') {
    ++generation
    session.value = null
    workspace.value = null
    receiving.value = null
    pending.value = []
    held.value = []
    signedOut.value = true
    readState.value = 'ready'
    notice.value = null
    error.value = message
  }
  function expired(reason: unknown) {
    if (!(reason instanceof ApiFailure) || reason.status !== 401) return false
    clearSession('Your session ended. Sign in to continue.')
    return true
  }
  async function refresh(background = false) {
    const current = ++generation
    if (!background) {
      readState.value = 'loading'
      error.value = ''
    }
    try {
      const authenticated = await api.session()
      if (current !== generation) return
      if (session.value?.workspaceId !== authenticated.workspaceId) {
        workspace.value = null
        receiving.value = null
        notice.value = null
        readState.value = 'loading'
      }
      session.value = authenticated
      signedOut.value = false
      loadPending()
      const [result, stock] = await Promise.all([
        api.workspace(page.value === 'activity'), page.value === 'receiving' ? api.receiving() : null
      ])
      if (current !== generation) return
      workspace.value = result
      receiving.value = stock
      readState.value = result.complete && (!stock || stock.complete) ? 'ready' : 'partial'
    } catch (reason) {
      if (current !== generation) return
      if (reason instanceof ApiFailure && reason.status === 401) {
        clearSession(session.value ? 'Your session ended. Sign in to continue.' : '')
      } else {
        error.value = explain(reason)
        readState.value = 'disconnected'
      }
    }
  }
  async function signIn(password: string) {
    if (authBusy.value) return
    authBusy.value = true
    error.value = ''
    try {
      session.value = await api.signIn(password)
      signedOut.value = false
      await refresh()
    } catch (reason) {
      error.value = reason instanceof ApiFailure && reason.status === 401
        ? 'Sign-in failed. Check the operator password and try again.' : explain(reason)
    } finally { authBusy.value = false }
  }
  async function signOut() {
    if (!session.value || submitting.value || authBusy.value) return
    authBusy.value = true
    try {
      await api.signOut(session.value.csrfToken)
      clearSession()
    } catch (reason) {
      if (!expired(reason)) error.value = 'Sign-out was not confirmed. ' + explain(reason)
    } finally { authBusy.value = false }
  }
  function disconnected() {
    ++generation
    readState.value = 'disconnected'
    error.value = 'The private connection is offline. Reconnect and refresh to continue.'
  }
  function reconnect() {
    if (!signedOut.value && !authBusy.value && !submitting.value) void refresh()
  }
  function checkVisible() {
    if (document.visibilityState === 'visible' && session.value && readState.value === 'ready' &&
        !submitting.value && !authBusy.value) void refresh(true)
  }
  watch(() => page.value === 'receiving' || page.value === 'activity' ? page.value : 'references',
    () => { if (session.value) void refresh() })
  onMounted(() => {
    loadPending()
    void refresh()
    window.addEventListener('offline', disconnected)
    window.addEventListener('online', reconnect)
    window.addEventListener('storage', loadPending)
    document.addEventListener('visibilitychange', checkVisible)
    timer = window.setInterval(checkVisible, 15000)
  })
  onBeforeUnmount(() => {
    ++generation
    window.removeEventListener('offline', disconnected)
    window.removeEventListener('online', reconnect)
    window.removeEventListener('storage', loadPending)
    document.removeEventListener('visibilitychange', checkVisible)
    window.clearInterval(timer)
  })

  const canSend = computed(() => Boolean(session.value && readState.value === 'ready' && !journalError.value &&
    !authBusy.value && !submitting.value))
  const canCreate = computed(() => canSend.value && !pending.value.length)

  async function send(command: Command) {
    if (!canSend.value || !session.value || command.workspaceId !== session.value.workspaceId) return
    ++generation
    submitting.value = true
    notice.value = null
    error.value = ''
    try {
      const receipt = await api.save(command, session.value.csrfToken)
      forget(command)
      loadPending()
      notice.value = { message: 'Saved ' + commandLabel(command) + ' · revision ' + receipt.revision + '.',
        correlationId: receipt.correlationId }
      await refresh()
      return receipt
    } catch (reason) {
      if (expired(reason)) return
      if (reason instanceof ApiFailure && reason.detail?.code === 'workspace_mismatch') {
        await refresh()
        error.value = 'The workspace changed. The original command is held for its issuing workspace.'
      } else if (reason instanceof ApiFailure && reason.detail &&
          ['invalid_reference', 'duplicate_reference', 'invalid_receiving', 'duplicate_receiving_reference',
            'revision_conflict', 'command_mismatch', 'catalog_ineligible', 'catalog_reconfirmation_required', 'bin_confirmation_mismatch',
            'quantity_unavailable', 'delivery_mismatch', 'backing_mismatch'].includes(reason.detail.code)) {
        forget(command)
        loadPending()
        if (reason.status === 409) await refresh()
        error.value = explain(reason)
      } else {
        // The gateway, transport or client may fail after commit. Keep the exact payload for replay.
        error.value = 'Command confirmation is pending. ' + explain(reason)
        readState.value = 'disconnected'
      }
    } finally {
      submitting.value = false
    }
  }

  async function create(input: { kind: 'catalog'; fields: CatalogInput } | { kind: 'location'; fields: LocationInput }, workspaceId: string) {
    const identity = { commandId: crypto.randomUUID(), expectedRevision: 0 }
    const command: Command = input.kind === 'catalog'
      ? { workspaceId, kind: 'catalog', body: { ...input.fields, ...identity } }
      : { workspaceId, kind: 'location', body: { ...input.fields, ...identity } }
    return record(command)
  }
  async function record(command: Command) {
    loadPending()
    if (!canCreate.value || command.workspaceId !== session.value?.workspaceId) return
    try {
      remember(command)
      loadPending()
    } catch (reason) {
      if (reason instanceof InvalidCommand) { error.value = reason.message; return }
      journalError.value = 'This browser could not save the command for recovery. The request has not been sent.'
      return
    }
    return send(command)
  }

  return { session, workspace, receiving, signedOut, readState, error, notice, pending, held, journalError, authBusy, submitting,
    canSend, canCreate, refresh, signIn, signOut, create, record, send, expired }
}
