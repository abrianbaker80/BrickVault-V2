<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { api, ContractFailure, explain, type SetPositionContents, type SetInspectionEvidence, type Workspace, type TransferControls } from './api'
import { contentKey, contentLabel, inspectionLink, recordedContents, type Presence } from './setContents'
import { itemLabel } from './receiving'
import SetContentsFields from './SetContentsFields.vue'
import { useBinRegistry } from './useBinRegistry'

const props = defineProps<{ position: string; receiptId?: string; workspace: Workspace; connected: boolean; controls: TransferControls }>()
const emit = defineEmits<{ failure: [reason: unknown] }>()
const review = shallowRef<SetPositionContents>()
const receipt = shallowRef<SetInspectionEvidence>()
const loading = ref(false), saving = ref(false), stale = ref(false), error = ref(''), retry = ref(0)
const quantity = ref<number | ''>(''), code = ref(''), reason = ref(''), box = ref<Presence>(''), instructions = ref<Presence>('')
const dirty = computed(() => Boolean(quantity.value || code.value || reason.value || box.value || instructions.value))
const contents = computed(() => recordedContents(box.value, instructions.value))
const bins = useBinRegistry(() => [code.value])
const bin = computed(() => props.workspace.locations.find(b => b.id === review.value?.locationId))
const item = computed(() => props.workspace.catalog.find(c => c.id === review.value?.catalogId))
const ready = computed(() => props.workspace.complete && props.connected && props.controls.enabled && !loading.value &&
  !saving.value && !error.value && !stale.value && review.value?.complete && !review.value.setContents &&
  !review.value.hasOpenShortage && item.value?.itemType === 'set' && item.value.revision === review.value.catalogRevision)
const allowedQuantity = computed(() => {
  const data = review.value, amount = quantity.value
  return Boolean(data && typeof amount === 'number' && Number.isInteger(amount) && amount > 0 &&
    (amount <= data.freeToClassifyQuantity || amount === data.physicalQuantity))
})
watch(() => [props.workspace.asOf, props.workspace.complete, props.connected, retry.value] as const,
  async ([, complete, connected], _, cancel) => {
    let active = true
    cancel(() => { active = false })
    loading.value = complete && connected
    error.value = ''
    if (!loading.value) return
    try {
      if (props.receiptId) {
        const data = await api.setInspection(props.position, {inspectionId:props.receiptId})
        if (!data.complete) throw new ContractFailure()
        if (active) receipt.value = data
      } else {
        const data = await api.setPosition(props.position)
        if (!data.complete) throw new ContractFailure()
        if (!active) return
        const previous = review.value
        if (dirty.value && previous && (previous.purchaseRevision !== data.purchaseRevision ||
          previous.catalogRevision !== data.catalogRevision || previous.locationId !== data.locationId ||
          previous.physicalQuantity !== data.physicalQuantity || previous.freeToClassifyQuantity !== data.freeToClassifyQuantity ||
          previous.hasOpenShortage !== data.hasOpenShortage ||
          contentKey(previous.setContents) !== contentKey(data.setContents))) stale.value = true
        review.value = data
      }
    } catch (failure) {
      if (active) { error.value = explain(failure); emit('failure',failure) }
    } finally { if (active) loading.value = false }
  }, { immediate:true })
function reset() {
  stale.value = false; quantity.value = ''; box.value = ''; instructions.value = ''; code.value = ''; reason.value = ''; retry.value++
}
async function submit() {
  const data = review.value, observed = contents.value
  if (!ready.value || !data || !observed || !allowedQuantity.value || !bin.value ||
      !bins.matches(bin.value,code.value) || !reason.value.trim()) return
  const route = location.hash, workspaceId = props.controls.workspaceId
  saving.value = true
  try {
    const result = await props.controls.dispatch({kind:'set-inspection',workspaceId,body:{
      commandId:crypto.randomUUID(), expectedRevision:data.purchaseRevision, purchaseId:data.purchaseId,
      positionId:data.positionId, catalogId:data.catalogId, catalogRevision:data.catalogRevision, locationId:data.locationId,
      confirmedLocationCode:code.value, quantity:Number(quantity.value), reason:reason.value.trim(), setContents:observed
    }})
    if (route !== location.hash || workspaceId !== props.controls.workspaceId) return
    if (result) location.hash = inspectionLink(data.positionId,result.entityId)
  } finally { saving.value = false }
}
</script>
<template>
  <section class="panel set-inspection" :aria-label="receiptId ? 'Set inspection receipt' : 'Inspect set contents'">
    <header class="panel-head"><h2>{{ receiptId ? 'Set inspection receipt' : 'Inspect set contents' }}</h2>
      <a :href="'#inventory/' + (receipt?.positionId ?? position)">Source position</a></header>
    <div class="form-body">
      <p v-if="error || !connected || !workspace.complete" class="warning" role="status">{{ error || 'The workspace is unavailable.' }}
        <button type="button" :disabled="loading || !connected" @click="retry++">Retry inspection read</button></p>
      <p v-else-if="loading && !review && !receipt" role="status">Reading set contents…</p>
      <template v-if="receipt">
        <p><strong>{{ contentLabel(receipt.setContents) }}</strong></p>
        <dl class="details"><dt>Quantity inspected</dt><dd>{{ receipt.quantity }}</dd><dt>Confirmed bin</dt><dd>{{ receipt.confirmedLocationCode }}</dd>
          <dt>Inspection reason</dt><dd>{{ receipt.reason }}</dd><dt>Purchase revision</dt><dd>{{ receipt.revision }}</dd></dl>
        <a :href="'#inventory/' + receipt.resultPositionId">Inspect resulting position</a>
      </template>
      <template v-else-if="review">
        <p><strong>{{ itemLabel(workspace.catalog,review.catalogId,review.catalogRevision) }}</strong>
          <small>Bin {{ bin?.code }} · {{ review.physicalQuantity }} on hand</small></p>
        <p>{{ contentLabel(review.setContents) }}</p>
        <p v-if="review.hasOpenShortage" class="warning">Resolve the open stock shortage before recording set contents.</p>
        <p v-else-if="!review.physicalQuantity">No sets remain in this position.</p>
        <form v-else-if="!review.setContents" @submit.prevent="submit">
          <p v-if="stale" class="warning" role="status">The inspected position changed.
            <button type="button" @click="reset">Review updated position</button></p>
          <fieldset class="fields" :disabled="!ready">
            <SetContentsFields v-model:box="box" v-model:instructions="instructions" />
            <label>Quantity inspected<input v-model="quantity" type="number" inputmode="numeric" min="1" :max="review.physicalQuantity" required /></label>
            <label>Confirm bin code<input v-model="code" maxlength="48" autocomplete="off" required /></label>
            <label class="span">Inspection reason<input v-model="reason" maxlength="200" required /></label>
          </fieldset>
          <p class="field-hint">Count the sets that share these contents. {{ review.freeToClassifyQuantity }} free units can be separated in this bin.</p>
          <p v-if="quantity && !allowedQuantity" class="warning">Choose a quantity within the available free stock, or inspect every unit together.</p>
          <p v-if="code && bin && !bins.pending(code) && !bins.matches(bin,code)" class="warning">Enter the code from the physical bin label.</p>
          <button class="primary" :disabled="!ready || !allowedQuantity || !contents || !bin || !bins.matches(bin,code) || !reason.trim()">Record set inspection</button>
        </form>
      </template>
    </div>
  </section>
</template>
<style scoped>
.set-inspection{max-width:760px;overflow-wrap:anywhere}.set-inspection small{display:block;color:var(--muted)}
.set-inspection form{display:grid;gap:12px}.set-inspection form p{margin:0}
</style>
