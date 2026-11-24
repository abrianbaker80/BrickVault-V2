<script setup lang="ts">
import { ref, shallowRef, watch } from 'vue'
import { api, ApiFailure, explain, type StockLoss } from './api'
import { lossReceiptLink } from './countLinks'
const props = defineProps<{ bin: string; position: string; observation: string; asOf: string; connected: boolean }>()
const emit = defineEmits<{ failure: [e: unknown] }>()
const posting = shallowRef<StockLoss>()
const absent = ref(false)
const error = ref('')
const loading = ref(false)
const retry = ref(0)
watch(() => [props.asOf, props.connected, retry.value] as const, async ([, online], _, cleanup) => {
  let active = true
  cleanup(() => { active = false })
  loading.value = online
  if (!online) return
  error.value = ''
  try {
    const result = await api.postedLoss(props.bin, props.position, { observationId: props.observation }, '', 1)
    if (!active) return
    if (!result.complete) { error.value = 'Incomplete stock-loss lookup.'; return }
    posting.value = result
    absent.value = false
  } catch (e) {
    if (!active) return
    if (e instanceof ApiFailure && e.status === 404 && e.detail?.code === 'unknown_stock_loss') { absent.value = true; posting.value = undefined }
    else { error.value = explain(e); emit('failure', e) }
  } finally { if (active) loading.value = false }
}, {immediate:true})
</script>
<template>
  <p v-if="error || !connected" class="warning" role="status">{{ error || 'The workspace is disconnected.' }} Stock-loss posting status could not be refreshed.
    <button v-if="connected" @click="retry++">Retry posting lookup</button></p>
  <p v-else-if="loading" role="status">Checking for a stock-loss posting…</p>
  <p v-else-if="absent" class="field-hint">Observation only. No stock adjustment posted.</p>
  <p v-if="posting">{{ posting.reviewedObservation.id === observation ? 'This observation was used to post a stock loss.' : 'A later witnessed count was used to post the stock loss for this case.' }}
    <a :href="lossReceiptLink(bin, position, posting.postingId)">View posted stock loss</a></p>
</template>
