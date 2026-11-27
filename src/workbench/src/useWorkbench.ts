import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch, type Ref } from 'vue'
import { api, ApiFailure, commandLabel, explain, forget, InvalidCommand, readJournal, remember } from './api'
import { recoverPartOut } from './recoverPartOut'
import {capacityLink,confirmsCapacityRejection} from './binCapacity'
import { registryLink, sameLabel } from './binLabels'
import { recoverInspection } from './recoverInspection'
import { partOutLink, recoveryLink, closureLink, countReceiptLink, lossReceiptLink, draftBackingLink, quarantineLink } from './countLinks'
import { recoverStockLoss } from './stockLoss'
import type { CatalogInput, Command, Held, LocationInput, ReceivingWorkspace, Session, Workspace } from './api'

type ReadState = 'loading' | 'ready' | 'disconnected' | 'partial'
export function useWorkbench(page: Ref<string>) {
  const session = shallowRef<Session | null>(null)
  const workspace = shallowRef<Workspace | null>(null)
  const receiving = shallowRef<ReceivingWorkspace | null>(null)
  const signedOut = ref(false)
  const readState = ref<ReadState>('loading')
  const error = ref('')
  const notice = ref<{ message: string; correlationId: string; sourceLocationId?: string; countLink?: string; lossLink?: string; recountLink?: string; quarantineReceipt?: string; withdrawalLink?: string; closureReceipt?:string; recoveryReceipt?:string; partOutReceipt?:string; registryReceipt?:string; capacityReceipt?:string } | null>(null)
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
  async function beginRefresh(background = false, verified?: { session: Session; references?: Workspace }) {
    const current = ++generation
    if (!background) {
      readState.value = 'loading'
      error.value = ''
    }
    try {
      const authenticated = verified?.session ?? await api.session()
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
      const finished = Promise.all([
        (page.value === 'catalog' || page.value === 'locations' ? verified?.references : undefined) ?? api.workspace(page.value === 'activity'),
        page.value === 'receiving' ? api.receiving() : null
      ]).then(([result, stock]) => {
        if (current !== generation) return
        workspace.value = result
        receiving.value = stock
        readState.value = result.complete && (!stock || stock.complete) ? 'ready' : 'partial'
      }).catch(reason => failedRefresh(current, reason))
      return { workspaceId: authenticated.workspaceId, finished }
    } catch (reason) { failedRefresh(current, reason) }
  }
  function failedRefresh(current: number, reason: unknown) {
    if (current !== generation) return
    if (reason instanceof ApiFailure && reason.status === 401) {
      clearSession(session.value ? 'Your session ended. Sign in to continue.' : '')
    } else {
      error.value = explain(reason)
      readState.value = 'disconnected'
    }
  }
  async function refresh(background = false) {
    const read = await beginRefresh(background)
    await read?.finished
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

  async function resolveSizeRejection(command: Command, reason: ApiFailure, firstAttempt: boolean) {
    // A previous attempt may already have committed before its response was lost.
    if (!firstAttempt) {
      try {
        const checking = generation
        let references: Workspace | undefined
        let committed: { correlationId: string } | undefined
        let partOutReceipt: string | undefined
        let registryReceipt:string|undefined
        let capacityReceipt:string|undefined
        let recoveryReceipt: string | undefined
        let closureReceipt: string | undefined
        let countLink: string | undefined
        let lossLink: string | undefined
        let recountLink: string | undefined
        let withdrawalLink: string | undefined
        let quarantineReceipt: string | undefined
        if(command.kind==='location-capacity'){
          const body=command.body
          try{
            const result=await api.capacityReceipt(body.locationId,{commandId:body.commandId}),event=result.event
            if(!result.complete || event.revision!==body.expectedRevision+1 || event.pieceLimit!==body.pieceLimit ||
                event.reason!==body.reason)throw new Error('Piece limit receipt mismatch')
            committed=event;capacityReceipt=capacityLink(result.locationId,event.id)
          }catch(e){if(!(e instanceof ApiFailure && e.status===404 && e.detail?.code==='unknown_location_capacity_event'))throw e}
        } else if (command.kind==='location-label' || command.kind==='location-archive') {
          try {
            const result=await api.registryReceipt(command.body.locationId,{commandId:command.body.commandId})
            const event=result.event
            if (!result.complete || event.revision!==command.body.expectedRevision+1 ||
                (command.kind==='location-label' ? event.kind!=='label-added' || event.label!==command.body.label :
                  event.kind!=='archived' || !event.label || !sameLabel(event.label,command.body.confirmedLabel) ||
                  event.reason!==command.body.reason || event.physicalQuantity!==command.body.expectedPhysicalQuantity ||
                  event.positionCount!==command.body.expectedPositionCount)) throw new Error('Registry receipt mismatch')
            committed=event
            registryReceipt=registryLink(result.locationId,event.id)
          } catch(e) {if(!(e instanceof ApiFailure && e.status===404 && e.detail?.code==='unknown_location_registry_event')) throw e}
        } else if (command.kind === 'quarantine' || command.kind === 'quarantine-release') {
          const body = command.body
          try {
            const row = await api.quarantineReceipt(body.locationId, body.positionId, { commandId:body.commandId })
            if (!row.complete || row.kind !== command.kind || row.purchaseId !== body.purchaseId ||
                row.revision !== body.expectedRevision + 1 || !sameLabel(row.confirmedLocationCode,body.confirmedLocationCode) ||
                row.catalogId !== body.catalogId || row.catalogRevision !== body.catalogRevision || row.quantity !== body.quantity ||
                row.reason !== body.reason || (command.kind === 'quarantine-release' &&
                (row.quarantineId !== command.body.quarantineId || row.inspectionResult !== command.body.inspectionResult))) throw new Error('Quarantine receipt mismatch')
            committed = row
            quarantineReceipt = quarantineLink(body.locationId, body.positionId, row.id)
          } catch (failure) {
            if (!(failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_quarantine_receipt')) throw failure
          }
        } else if (command.kind === 'offer-withdrawal') {
          const body = command.body
          try {
            const row = await api.withdrawalReceipt(body.locationId,body.positionId,{commandId:body.commandId})
            if (!row.complete || row.revision !== body.expectedRevision + 1 || row.purchaseId !== body.purchaseId ||
                row.offerId !== body.offerId || !sameLabel(row.confirmedLocationCode,body.confirmedLocationCode) ||
                row.quantity !== body.quantity || row.reason !== body.reason) throw new Error('Withdrawal receipt mismatch')
            committed = row
            withdrawalLink = draftBackingLink(body.locationId,body.positionId,'receipts',row.id)
          } catch (failure) {
            if (!(failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_draft_backing_withdrawal')) throw failure
          }
        } else if (command.kind === 'recount') {
          try {
            const result = await api.recountBatch(command.body.previewId)
            if (!result.complete || result.commandId !== command.body.commandId) throw new Error('Recount receipt mismatch')
            committed = result
            recountLink = '#inventory/recounts/' + result.batchId
          } catch (failure) {
            if (!(failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_recount_batch')) throw failure
          }
        } else if (command.kind === 'part-out') {
          const row = await recoverPartOut(command.body)
          if (row) {
            committed = row
            partOutReceipt = partOutLink(row.sourcePositionId,row.id)
          }
        } else if (command.kind === 'stock-loss-recovery') {
          const row=await recoverInspection(command.body)
          if (row) {
            committed=row
            recoveryReceipt=recoveryLink(row.location.id,row.positionId,row.postingId,row.id)
          }
        } else if (command.kind === 'shortage-closure') {
          const body=command.body
          try {
            const row=await api.closure(body.locationId,body.positionId,body.caseId,{commandId:body.commandId})
            if (!row.complete || row.purchaseId!==body.purchaseId || row.revision!==body.expectedRevision+1 ||
                row.recountObservation.id!==body.expectedObservationId || !sameLabel(row.confirmedLocationCode,body.confirmedLocationCode) ||
                row.reason!==body.reason) throw new Error('Shortage closure receipt mismatch')
            committed=row
            closureReceipt=closureLink(body.locationId,body.positionId,body.caseId,'receipt',row.id)
          } catch (failure) {
            if (!(failure instanceof ApiFailure && failure.status===404 && failure.detail?.code==='unknown_shortage_closure')) throw failure
          }
        } else if (command.kind === 'stock-loss') {
          const recovered = await recoverStockLoss(command.body)
          if (recovered) {
            committed = recovered
            lossLink = lossReceiptLink(command.body.locationId,command.body.positionId,recovered.postingId)
          }
        } else if (command.kind === 'hold' || command.kind === 'hold-release') {
          try {
            if (command.kind === 'hold') {
              const data = await api.positionHolds(command.body.locationId, command.body.positionId, '', command.body.commandId)
              const hold = data.holds[0]
              if (!data.complete || data.purchaseId !== command.body.purchaseId || hold.purpose !== command.body.purpose ||
                  hold.reason !== command.body.reason || hold.quantity !== command.body.quantity ||
                  hold.expectedPurchaseRevision !== command.body.expectedRevision || !sameLabel(hold.confirmedLocationCode,command.body.confirmedLocationCode)) throw new Error('Hold receipt mismatch')
              committed = hold
            } else {
              const data = await api.holdReleases(command.locationId, command.positionId, command.body.holdId, '', command.body.commandId)
              const release = data.releases[0]
              if (!data.complete || release.reason !== command.body.reason || release.quantity !== command.body.quantity ||
                  release.purchaseId !== command.body.purchaseId || release.expectedPurchaseRevision !== command.body.expectedRevision ||
                  release.shortageId !== (command.body.shortageId ?? null) ||
                  release.expectedObservationId !== (command.body.expectedObservationId ?? null)) throw new Error('Release receipt mismatch')
              committed = release
            }
          } catch (failure) {
            if (!(failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_hold_command')) throw failure
          }
        } else if (command.kind === 'count') {
          try {
            const result = await api.binCounts(command.body.locationId, { commandId: command.body.commandId, positionId: command.body.positionId })
            if (!result.complete) throw new Error('Incomplete count history')
            const observation = result.observations[0]
            if (observation.purchaseId !== command.body.purchaseId || observation.countedQuantity !== command.body.countedQuantity ||
                observation.expectedPurchaseRevision !== command.body.expectedRevision || !sameLabel(observation.locationCode,command.body.confirmedLocationCode) ||
                observation.reason !== command.body.reason) throw new Error('Count receipt mismatch')
            committed = observation
            countLink = countReceiptLink(command.body.locationId, observation.id)
          } catch (failure) {
            if (!(failure instanceof ApiFailure && failure.status === 404 && failure.detail?.code === 'unknown_count_observation')) throw failure
          }
        } else if (command.kind === 'location' || command.kind === 'catalog') {
          references = await api.workspace()
          if (!references.complete) throw new Error('Incomplete reference history')
          const entry = references.audit.find(row => row.commandId === command.body.commandId)
          if (entry && (entry.kind !== command.kind || entry.revision !== command.body.expectedRevision + 1 ||
              (command.body.id && entry.entityId !== command.body.id))) throw new Error('Reference receipt mismatch')
          committed = entry
        } else {
          const snapshot = command.kind === 'transfer'
            ? await api.binTransfers(command.body.sourceLocationId) : await api.workspace(true)
          if (!snapshot.complete) throw new Error('Incomplete command history')
          const history = 'movements' in snapshot ? snapshot.movements : snapshot.audit
          committed = history.find(row => row.commandId === command.body.commandId)
        }
        const authenticated = await api.session()
        if (checking !== generation || authenticated.workspaceId !== command.workspaceId) throw new Error('Workspace changed')
        if (committed) {
          forget(command)
          loadPending()
          notice.value = { message: 'Confirmed the original ' + commandLabel(command) + '.',
            correlationId: committed.correlationId, capacityReceipt, registryReceipt, countLink, lossLink, recountLink, quarantineReceipt, withdrawalLink, closureReceipt, recoveryReceipt, partOutReceipt, sourceLocationId: command.kind === 'transfer' ? command.body.sourceLocationId : undefined }
          const read = await beginRefresh(false, { session: authenticated, references })
          await read?.finished
          return
        }
      } catch (failure) {
        if (expired(failure)) return
        error.value = 'The server rejected the retry size. Reconnect to verify the original command before continuing.'
        readState.value = 'disconnected'
        return
      }
    }
    forget(command)
    loadPending()
    error.value = explain(reason)
  }

  async function send(command: Command, firstAttempt = false) {
    if (!canSend.value || !session.value || command.workspaceId !== session.value.workspaceId) return
    ++generation
    submitting.value = true
    notice.value = null
    error.value = ''
    try {
      const receipt = await api.save(command, session.value.csrfToken)
      forget(command)
      loadPending()
      const savedNotice = { message: 'Saved ' + commandLabel(command) + ' · revision ' + receipt.revision + '.',
        capacityReceipt: command.kind==='location-capacity' ? capacityLink(command.body.locationId,receipt.entityId) : undefined,
        registryReceipt: command.kind==='location-label' || command.kind==='location-archive' ? registryLink(command.body.locationId,receipt.entityId) : undefined,
        quarantineReceipt: command.kind === 'quarantine' || command.kind === 'quarantine-release' ? quarantineLink(command.body.locationId, command.body.positionId, receipt.entityId) : undefined,
        withdrawalLink: command.kind === 'offer-withdrawal' ? draftBackingLink(command.body.locationId,command.body.positionId,'receipts',receipt.entityId) : undefined,
        partOutReceipt: command.kind==='part-out' ? partOutLink(command.body.sourcePositionId,receipt.entityId) : undefined,
        recoveryReceipt: command.kind==='stock-loss-recovery' ? recoveryLink(command.body.locationId,command.body.positionId,command.body.postingId,receipt.entityId) : undefined,
        closureReceipt: command.kind==='shortage-closure' ? closureLink(command.body.locationId,command.body.positionId,command.body.caseId,'receipt',receipt.entityId) : undefined,
        recountLink: command.kind === 'recount' ? '#inventory/recounts/' + receipt.entityId : undefined,
        correlationId: receipt.correlationId, lossLink: command.kind === 'stock-loss' ? lossReceiptLink(command.body.locationId,command.body.positionId,receipt.entityId) : undefined,
        countLink: command.kind === 'count' ? countReceiptLink(command.body.locationId, receipt.entityId) : undefined,
        sourceLocationId: command.kind === 'transfer' ? command.body.sourceLocationId : undefined }
      if (command.kind === 'count' || command.kind === 'stock-loss' || command.kind === 'stock-loss-recovery' || command.kind === 'quarantine' || command.kind === 'quarantine-release') {
        // These flows open an authenticated, exact receipt. Keep writes fenced while the directory refreshes.
        const read = await beginRefresh()
        if (read?.workspaceId !== command.workspaceId) return
        notice.value = savedNotice
      } else {
        notice.value = savedNotice
        await refresh()
      }
      return receipt
    } catch (reason) {
      if (expired(reason)) return
      if (reason instanceof ApiFailure && reason.status === 413 && reason.detail?.code === 'request_too_large') {
        await resolveSizeRejection(command, reason, firstAttempt)
      } else if (reason instanceof ApiFailure && reason.detail?.code === 'workspace_mismatch') {
        await refresh()
        error.value = 'The workspace changed. The original command is held for its issuing workspace.'
      } else if (reason instanceof ApiFailure && reason.detail && (confirmsCapacityRejection(command,reason) ||
          ['invalid_quarantine','unknown_quarantine_receipt','quarantine_position_mismatch','quarantine_bin_conflict','quarantine_catalog_conflict',
            'quarantine_shortage_open','quarantine_quantity_unavailable','quarantine_capacity_unavailable','stock_loss_quarantine_unresolved','invalid_draft_backing_withdrawal','draft_backing_bin_conflict','draft_backing_position_mismatch','draft_backing_offer_mismatch',
            'draft_backing_authority_unverified','draft_backing_quantity_unavailable','invalid_recount', 'empty_recount_preview', 'recount_preview_used', 'unknown_recount_preview',
            'invalid_location_capacity','invalid_location_label','invalid_location_archive','invalid_location_registry_command','duplicate_location_label','location_destination_archived',
            'location_archive_stock_changed','location_already_archived','location_label_mismatch','invalid_reference', 'duplicate_reference', 'invalid_receiving', 'duplicate_receiving_reference',
            'revision_conflict', 'command_mismatch', 'catalog_ineligible', 'catalog_reconfirmation_required', 'bin_confirmation_mismatch',
            'quantity_unavailable', 'delivery_mismatch', 'backing_mismatch', 'invalid_transfer', 'transfer_source_mismatch',
            'transfer_bin_confirmation_mismatch', 'transfer_quantity_unavailable', 'transfer_backing_unavailable', 'transfer_all_backing_requires_full', 'unknown_location',
            'invalid_count', 'count_position_mismatch', 'count_bin_confirmation_mismatch', 'stock_shortage_open', 'invalid_hold', 'hold_position_mismatch',
            'hold_bin_confirmation_mismatch', 'hold_catalog_ineligible', 'hold_quantity_unavailable', 'hold_shortage_frontier_conflict',
            'reserved_stock_unavailable','invalid_stock_loss','stock_loss_position_mismatch','stock_loss_bin_confirmation_mismatch',
            'stock_loss_case_conflict','stock_loss_observation_conflict','stock_loss_no_loss','stock_loss_protected_shortfall',
            'stock_loss_offer_reduction_mismatch','stock_loss_quarantine_attribution_mismatch','invalid_shortage_closure','shortage_closure_position_mismatch',
            'shortage_closure_bin_conflict','shortage_closure_case_conflict','shortage_closure_observation_conflict',
            'shortage_closure_count_not_exact','shortage_closure_commitments_inconsistent','invalid_stock_loss_recovery','stock_loss_recovery_scope',
            'stock_loss_recovery_bin','stock_loss_recovery_catalog','stock_loss_recovery_amount','invalid_stock_part_out','part_out_scope','part_out_bin',
            'part_out_catalog','part_out_amount','part_out_identity_gain','part_out_cost_mismatch'].includes(reason.detail.code))) {
        forget(command)
        loadPending()
        if (reason.status === 409) await refresh()
        error.value = explain(reason,workspace.value?.locations)
      } else {
        // The gateway, transport or client may fail after commit. Keep the exact payload for replay.
        const message=reason instanceof ApiFailure && reason.detail?.code==='location_capacity_exceeded'
          ? 'The capacity response could not be verified for this command.' : explain(reason)
        error.value = 'Command confirmation is pending. ' + message
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
      if (reason instanceof DOMException && reason.name === 'QuotaExceededError') {
        error.value = 'The request has not been sent. This browser has insufficient recovery storage for this command.'
        return
      }
      journalError.value = 'This browser could not save the command for recovery. The request has not been sent.'
      return
    }
    return send(command, true)
  }

  return { session, workspace, receiving, signedOut, readState, error, notice, pending, held, journalError, authBusy, submitting,
    canSend, canCreate, refresh, signIn, signOut, create, record, send, expired }
}
