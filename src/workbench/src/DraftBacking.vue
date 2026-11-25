<script setup lang="ts">
import { computed, nextTick, onMounted, ref, shallowRef } from 'vue'
import { api, ApiFailure, ContractFailure } from './api'
import type { DraftBackingPage, DraftOfferHistory, Location, TransferControls, WithdrawalEvidence } from './api'
import { draftBackingLink } from './countLinks'
import { itemLabel, money } from './receiving'
import { time } from './presentation'
import { usePositionPage } from './usePositionPage'

const props = defineProps<{ bin: Location; position: string; offer?: string; receipt?: string; asOf: string; connected: boolean; controls: TransferControls }>()
const emit = defineEmits<{ close: []; failure: [reason:unknown]; recover: [] }>()
const dialog = ref<HTMLDialogElement>()
const formTitle = ref<HTMLElement>()
const beginButton = ref<HTMLButtonElement>()
const readState = { get asOf() { return props.asOf }, get state() { return props.connected ? 'ready' as const : 'unavailable' as const } }
const staleCursor = ref(false)
type Page = DraftBackingPage | DraftOfferHistory | (WithdrawalEvidence & { nextAfter: null })
const { page, confirmed, requested, attempt, loading, partial, error, heading, retry } = usePositionPage<Page>(readState, async after => {
  staleCursor.value = false
  if (props.receipt) return { ...await api.withdrawalReceipt(props.bin.id, props.position, {receiptId:props.receipt}), nextAfter:null }
  const snapshot = props.offer ? await api.draftOfferHistory(props.bin.id,props.position,props.offer,after) :
    await api.draftBacking(props.bin.id,props.position,after)
  if (snapshot.position.location.revision < props.bin.revision) throw new ContractFailure()
  return snapshot
}, reason => {
  staleCursor.value = reason instanceof ApiFailure && reason.detail?.code === 'stale_draft_backing_page'
  emit('failure',reason)
})
const confirmedPage = computed(() => page.value?.complete ? page.value : undefined)
const snapshot = computed(() => confirmedPage.value && 'position' in confirmedPage.value ? confirmedPage.value : undefined)
const history = computed(() => snapshot.value && 'offer' in snapshot.value ? snapshot.value : undefined)
const receipt = computed(() => confirmedPage.value && 'priorBackedQuantity' in confirmedPage.value ? confirmedPage.value : undefined)
const ready = computed(() => props.connected && props.controls.enabled && !loading.value && !partial.value && !error.value && !sending.value)
const quantity = ref<number | ''>('')
const binCode = ref('')
const reason = ref('')
const feedback = ref('')
const sending = ref(false)
const frozen = shallowRef<{ data:DraftOfferHistory; workspaceId:string }>()
const stale = computed(() => {
  const original = frozen.value
  const current = history.value
  if (!original || !current) return false
  const prior = original.data
  return original.workspaceId !== props.controls.workspaceId || prior.purchaseRevision !== current.purchaseRevision ||
    prior.position.location.revision !== current.position.location.revision ||
    prior.offer.offerId !== current.offer.offerId || prior.offer.backedQuantity !== current.offer.backedQuantity ||
    prior.offer.totalOfferBackedQuantity !== current.offer.totalOfferBackedQuantity ||
    prior.offer.canWithdraw !== current.offer.canWithdraw || prior.hasOpenShortage !== current.hasOpenShortage
})
const validQuantity = computed(() => Number.isInteger(quantity.value) && Number(quantity.value)>0 &&
  Number(quantity.value)<=(frozen.value?.data.offer.backedQuantity ?? 0))
const writable = computed(() => ready.value && !stale.value && frozen.value && history.value?.offer.canWithdraw && validQuantity.value &&
  binCode.value===frozen.value.data.position.location.code && reason.value.trim().length>0)
const titles = { 'offer-backing':'Offer backing added', 'transfer-in':'Backing transferred in', 'transfer-out':'Backing transferred out',
  'stock-loss':'Reduced with posted loss', 'offer-withdrawal':'Draft backing withdrawn' }
const link = (view?:'offers'|'receipts',id?:string) => draftBackingLink(props.bin.id,props.position,view,id)
onMounted(() => dialog.value?.showModal())
async function begin() {
  if (!ready.value || !history.value?.offer.canWithdraw) return
  frozen.value={data:history.value,workspaceId:props.controls.workspaceId}
  binCode.value=''; quantity.value=''; reason.value=''; feedback.value=''
  await nextTick()
  formTitle.value?.focus()
}
async function cancel() {
  frozen.value=undefined
  await nextTick()
  if (beginButton.value && !beginButton.value.disabled) beginButton.value.focus()
  else heading.value?.focus()
}
async function save() {
  const baseline=frozen.value
  if (!writable.value || !baseline) return
  const {data,workspaceId}=baseline, source=window.location.hash, target=dialog.value
  const body={commandId:crypto.randomUUID(),purchaseId:data.position.layer.purchaseId,expectedRevision:data.purchaseRevision,
    positionId:props.position,offerId:data.offer.offerId,locationId:props.bin.id,confirmedLocationCode:binCode.value,
    quantity:Number(quantity.value),reason:reason.value.trim()}
  sending.value=true
  try {
    const ack=await props.controls.dispatch({kind:'offer-withdrawal',workspaceId,body})
    await nextTick()
    if (!target?.isConnected || !target.open || source!==window.location.hash) return
    if (ack) window.location.hash=link('receipts',ack.entityId)
    else if (props.controls.pending) emit('recover')
    else feedback.value=props.controls.feedback
  } finally {sending.value=false}
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="backing-title" @cancel.prevent="!sending && emit('close')" @keydown.esc.stop>
    <header class="panel-head"><h2 id="backing-title" ref="heading" tabindex="-1">{{ props.receipt ? 'Withdrawal receipt' : 'Draft offer backing' }}</h2>
      <button class="close" aria-label="Close draft backing" :disabled="sending" @click="emit('close')">×</button></header>
    <div class="draft-body">
      <p v-if="error || partial || !connected" class="warning" role="status">{{ error || (partial ? 'Partial backing read.' : 'The workspace is offline.') }}
        {{ confirmedPage ? 'Showing the last confirmed read.' : 'Backing details are unavailable.' }}</p>
      <p v-else-if="loading" role="status">Reading offer backing…</p>
      <button ref="retry" v-if="connected && (error || partial)" :disabled="loading" @click="staleCursor ? (requested=['']) : attempt++">{{ staleCursor ? 'Read from first page' : 'Retry backing read' }}</button>
      <template v-if="receipt">
        <h3>{{ receipt.quantity }} {{ receipt.quantity===1 ? 'unit withdrawn' : 'units withdrawn' }} from draft backing</h3>
        <p>{{ money(receipt.unitPriceMinor,receipt.currency) }} per unit · Confirmed bin {{ receipt.confirmedLocationCode }}</p>
        <dl class="details"><dt>Offer at this position</dt><dd>{{ receipt.priorBackedQuantity }} → {{ receipt.remainingBackedQuantity }}</dd>
          <dt>Offer across all positions</dt><dd>{{ receipt.priorTotalOfferBackedQuantity }} → {{ receipt.remainingTotalOfferBackedQuantity }}</dd></dl>
        <p>{{ receipt.reason }}</p><small>Recorded {{ time(receipt.recordedAt) }} · Purchase revision {{ receipt.revision }}</small>
        <dl class="details"><dt>Offer ID</dt><dd>{{ receipt.offerId }}</dd><dt>Position</dt><dd>{{ receipt.positionId }}</dd>
          <dt>Receipt ID</dt><dd>{{ receipt.id }}</dd><dt>Command ID</dt><dd>{{ receipt.commandId }}</dd><dt>Correlation ID</dt><dd>{{ receipt.correlationId }}</dd></dl>
        <nav aria-label="Withdrawal receipt navigation"><a :href="link('offers',receipt.offerId)">Review this offer at this position</a>
          <a :href="'#inventory/'+position">Position timeline</a><a :href="'#receiving/'+receipt.purchaseId">View purchase</a></nav>
      </template>
      <template v-if="snapshot">
        <section aria-label="Selected position">
          <strong>{{ snapshot.position.layer.purchaseReference }} · {{ snapshot.position.layer.deliveryReference }}</strong>
          <p>{{ itemLabel([snapshot.position.layer.currentCatalog],snapshot.position.layer.catalogId,snapshot.position.layer.catalogRevision) }}</p>
          <small>Bin {{ snapshot.position.location.code }} · Position {{ position }}</small>
        </section>
        <dl class="details" aria-label="Position balances">
          <dt>Physical on hand</dt><dd>{{ snapshot.physicalOnHand }}</dd><dt>Held for builds</dt><dd>{{ snapshot.heldQuantity }}</dd>
          <dt>Saleable</dt><dd>{{ snapshot.sellableQuantity }}</dd><dt>Offer-backed</dt><dd>{{ snapshot.offerBackedQuantity }}</dd>
          <dt>Withheld from offers</dt><dd>{{ snapshot.withheldQuantity }}</dd><dt>Available for new backing</dt><dd>{{ snapshot.freeToBackQuantity }}</dd></dl>
        <small>Read {{ time(snapshot.asOf) }} · Purchase revision {{ snapshot.purchaseRevision }}</small>
        <p v-if="snapshot.hasOpenShortage" role="status" class="warning">This position has an open shortage. Review its witnessed count before changing backing.</p>
        <template v-if="'offers' in snapshot">
          <h3>Offers at this position</h3>
          <ul v-if="snapshot.offers.length" class="receipt-lines" aria-label="Offers at this position"><li v-for="o in snapshot.offers" :key="o.offerId">
            <strong>{{ money(o.unitPriceMinor,o.currency) }} per unit</strong><p>{{ o.backedQuantity }} backed here · {{ o.totalOfferBackedQuantity }} across all positions</p>
            <small>Offer {{ o.offerId }}</small><p v-if="o.authority==='unverified-origin'" class="warning">Internal origin unverified</p>
            <a :href="link('offers',o.offerId)" :aria-label="'Review offer '+o.offerId">Review offer and history</a></li></ul>
          <p v-else-if="!loading && !error && !partial && connected">No offers recorded on this page.</p>
        </template>
        <template v-if="history">
          <section aria-label="Selected offer"><h3>{{ money(history.offer.unitPriceMinor,history.offer.currency) }} per unit</h3>
            <p>{{ history.offer.authority==='internal-unpublished' ? 'Internal unpublished offer' : 'Internal origin unverified' }}</p>
            <small>Offer {{ history.offer.offerId }}</small>
            <p>{{ history.offer.backedQuantity }} backed at this position · {{ history.offer.totalOfferBackedQuantity }} across all positions</p></section>
          <form v-if="frozen" @submit.prevent="save">
            <h3 ref="formTitle" tabindex="-1">Withdraw draft backing</h3>
            <p>Pieces stay in {{ frozen.data.position.location.code }}. Withdrawing backing preserves their cost and the offer price.</p>
            <p v-if="stale" class="warning" role="alert">The position or offer changed. Cancel and review current backing before confirming.</p>
            <fieldset :disabled="!ready || stale">
              <label>Quantity to withdraw<input type="number" v-model="quantity" min="1" :max="frozen.data.offer.backedQuantity" inputmode="numeric" required /></label>
              <label>Confirm physical bin code<input v-model="binCode" required maxlength="48" autocomplete="off" spellcheck="false" /></label>
              <small v-if="binCode && binCode!==frozen.data.position.location.code" class="warning">Enter the code on the selected bin.</small>
              <label>Reason for withdrawal<input v-model="reason" required maxlength="200" /></label>
            </fieldset>
            <dl v-if="validQuantity" class="details" aria-label="Proposed withdrawal">
              <dt>This offer here</dt><dd>{{ frozen.data.offer.backedQuantity }} → {{ frozen.data.offer.backedQuantity-Number(quantity) }}</dd>
              <dt>This offer across all positions</dt><dd>{{ frozen.data.offer.totalOfferBackedQuantity }} → {{ frozen.data.offer.totalOfferBackedQuantity-Number(quantity) }}</dd>
              <dt>Withheld here</dt><dd>{{ frozen.data.withheldQuantity }} → {{ frozen.data.withheldQuantity+Number(quantity) }}</dd></dl>
            <small v-else-if="quantity!==''" class="warning">Enter 1–{{ frozen.data.offer.backedQuantity }} whole units.</small>
            <p v-if="feedback" class="warning" role="alert">{{ feedback }}</p>
            <div class="actions"><button type="button" :disabled="sending" @click="cancel">Cancel</button>
              <button class="primary" :disabled="!writable">Confirm withdrawal</button></div>
          </form>
          <template v-else>
            <p v-if="history.offer.authority==='unverified-origin'" class="warning">The origin of this offer has not been verified. Its backing is protected.</p>
            <button ref="beginButton" class="primary" :disabled="!ready || !history.offer.canWithdraw" @click="begin">Withdraw backing</button>
            <h3>Backing history at this position</h3><ol class="receipt-lines" aria-label="Offer backing history">
              <li v-for="c in history.contributions" :key="c.kind+'/'+c.id"><strong>{{ titles[c.kind] }}</strong>
                <p>{{ c.backingDelta>0 ? '+' : '' }}{{ c.backingDelta }} backed · Revision {{ c.revision }}</p>
                <small>{{ c.recordedAt ? time(c.recordedAt) : 'Time not recorded' }}</small><p v-if="c.reason">{{ c.reason }}</p>
                <a v-if="c.kind==='offer-withdrawal'" :href="link('receipts',c.id)">Withdrawal receipt</a>
                <a v-if="c.relatedPositionId" :href="'#inventory/'+c.relatedPositionId">Inspect related position</a></li></ol>
            <a :href="link()">All offers at this position</a>
          </template>
        </template>
        <nav v-if="!frozen && (confirmed.length>1 || snapshot.nextAfter)" aria-label="Backing pages">
          <span>Page {{ confirmed.length }}</span>
          <button :disabled="loading || !connected || confirmed.length===1" @click="requested=confirmed.slice(0,-1)">Previous page</button>
          <button :disabled="loading || !connected || partial || !!error || !snapshot.nextAfter" @click="snapshot.nextAfter && (requested=[...confirmed,snapshot.nextAfter])">Next page</button></nav>
        <nav v-if="!frozen" aria-label="Position references"><a :href="'#inventory/'+position">Position timeline</a>
          <a :href="'#inventory/lot/'+snapshot.position.layer.deliveryId">Receipt lot</a></nav>
      </template>
    </div>
  </dialog>
</template>
<style scoped>
.draft-body {display:grid; gap:10px; padding:16px; overflow-wrap:anywhere}
.draft-body :is(h3,p,dl,ol,ul) {margin:0}
.draft-body small {display:block; color:var(--muted)}
.draft-body a {color:#355340; text-underline-offset:2px}
.draft-body > button {justify-self:start}
.draft-body .receipt-lines {padding:0}
.draft-body :is(form,fieldset,label) {display:grid; gap:6px}
.draft-body fieldset {padding:0; border:0; min-width:0; gap:10px}
@media (min-width:600px) {
  .draft-body form fieldset {grid-template-columns:1fr 1fr}
  .draft-body fieldset label:last-child, .draft-body fieldset .warning {grid-column:1/-1}
}
.draft-body nav {display:flex; flex-wrap:wrap; gap:8px; align-items:center}
</style>
