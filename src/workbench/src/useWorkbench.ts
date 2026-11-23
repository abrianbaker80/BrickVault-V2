import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue'
import { api, ApiFailure, explain, forget, readJournal, remember } from './api'
import type { CatalogInput, Command, Held, LocationInput, Session, Workspace } from './api'

type ReadState = 'loading' | 'ready' | 'disconnected' | 'partial'
export function useWorkbench() {
  const session = shallowRef<Session | null>(null)
  const workspace = shallowRef<Workspace | null>(null)
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
        notice.value = null
        readState.value = 'loading'
      }
      session.value = authenticated
      signedOut.value = false
      loadPending()
      const result = await api.workspace()
      if (current !== generation) return
      workspace.value = result
      readState.value = result.complete ? 'ready' : 'partial'
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
      notice.value = { message: 'Saved ' + command.body.name + ' · revision ' + receipt.revision + '.',
        correlationId: receipt.correlationId }
      await refresh()
    } catch (reason) {
      if (expired(reason)) return
      if (reason instanceof ApiFailure && reason.detail?.code === 'workspace_mismatch') {
        await refresh()
        error.value = 'The workspace changed. The original command is held for its issuing workspace.'
      } else if (reason instanceof ApiFailure && reason.detail &&
          ['invalid_reference', 'duplicate_reference', 'revision_conflict'].includes(reason.detail.code)) {
        forget(command)
        loadPending()
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
    loadPending()
    if (!canCreate.value || workspaceId !== session.value?.workspaceId) return
    const identity = { commandId: crypto.randomUUID(), expectedRevision: 0 }
    const command: Command = input.kind === 'catalog'
      ? { workspaceId, kind: 'catalog', body: { ...input.fields, ...identity } }
      : { workspaceId, kind: 'location', body: { ...input.fields, ...identity } }
    try {
      remember(command)
      loadPending()
    } catch {
      journalError.value = 'This browser could not save the command for recovery. The request has not been sent.'
      return
    }
    await send(command)
  }

  return { session, workspace, signedOut, readState, error, notice, pending, held, journalError, authBusy, submitting,
    canSend, canCreate, refresh, signIn, signOut, create, send, expired }
}
