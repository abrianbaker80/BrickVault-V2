<script setup lang="ts">
import { api } from './api'
import type { RecountBatchList, RecountBatchPage, RecountQueuePage } from './api'
import { countPositionLink, countReceiptLink } from './countLinks'
import { time } from './presentation'
import RecountTarget from './RecountTarget.vue'
import { usePositionPage, type PositionReadState } from './usePositionPage'

const props=defineProps<{ route:string; asOf:string; state:PositionReadState }>()
const emit=defineEmits<{ failure:[reason:unknown] }>()
const batch = (id:string) => '#inventory/recounts/'+id
const title = props.route === 'queue' ? 'Recount queue' : props.route === 'receipts' ? 'Recount receipts' : 'Recount receipt'
const outcome = {queued:'Queued', changed:'Changed since preview', 'already-queued':'Already queued', unavailable:'Unavailable'}
const {page,confirmed,requested,attempt,loading,partial,error,heading,retry} =
  usePositionPage<RecountBatchList|RecountBatchPage|RecountQueuePage>(props, after => {
    if (props.route==='queue') return api.recountQueue(after)
    if (props.route==='receipts') return api.recountBatches(after)
    return api.recountBatch(props.route,after)
  }, reason => emit('failure',reason))
</script>
<template>
  <section class="panel recount-work" role="region" :aria-label="title">
    <header class="panel-head"><h2 ref="heading" tabindex="-1">{{ title }}</h2></header>
    <nav aria-label="Recount navigation"><a href="#inventory">Stock register</a>
      <a v-if="route!=='queue'" href="#inventory/recounts">Recount queue</a><a v-if="route!=='receipts'" href="#inventory/recounts/receipts">All recount receipts</a></nav>
    <p v-if="error || state==='unavailable' || partial" class="warning" role="status">{{ error || (partial ? 'Partial recount read.' : 'The workspace is disconnected.') }}
      {{ page?.complete ? 'Showing the last confirmed read.' : 'Recount records are unavailable.' }}</p>
    <p v-if="loading" role="status">Reading recount records…</p>
    <button ref="retry" v-if="(error || partial) && state==='ready'" @click="attempt++">Retry recount read</button>
    <template v-if="page?.complete">
      <small>Read {{ time(page.asOf) }}</small>
      <template v-if="page.scope==='recount-batch'">
        <p class="scope">{{ page.total }} reviewed · {{ page.queued }} queued · {{ page.changed }} changed · {{ page.alreadyQueued }} already queued · {{ page.unavailable }} unavailable</p>
        <details><summary>Receipt identifiers</summary><p>Batch {{ page.batchId }}</p><p>Command {{ page.commandId }}</p><p>Correlation {{ page.correlationId }}</p></details>
        <ul class="receipt-lines" aria-label="Recount outcomes"><li v-for="row in page.rows" :key="row.target.positionId">
          <RecountTarget :target="row.target"/><b>{{ outcome[row.outcome] }}</b>
          <small v-if="row.taskState==='depleted'">Depleted before witness · This position has no stock left to count.</small>
          <a v-if="row.taskState==='pending' && row.target.locationId" :href="countPositionLink(row.target.locationId,row.target.positionId)">Record witnessed count</a>
          <a v-if="row.observationId && row.target.locationId" :href="countReceiptLink(row.target.locationId,row.observationId)">View witnessed count</a>
          <a v-if="row.outcome==='already-queued' && row.queueBatchId" :href="batch(row.queueBatchId)">Original queue receipt</a>
          <a v-if="row.target.purchaseId" :href="'#inventory/'+row.target.positionId">Inspect position timeline</a>
        </li></ul>
      </template>
      <template v-else-if="page.scope==='recount-queue'">
        <p class="scope">{{ page.total }} positions awaiting a witnessed count</p>
        <ul class="receipt-lines" aria-label="Queued positions"><li v-for="row in page.rows" :key="row.target.positionId">
          <RecountTarget :target="row.target"/>
          <a v-if="row.available && row.target.locationId" :href="countPositionLink(row.target.locationId,row.target.positionId)">Record witnessed count</a>
          <a :href="'#inventory/'+row.target.positionId">Inspect position timeline</a><a :href="batch(row.batchId)">View queue receipt</a>
        </li></ul>
      </template>
      <ul v-else class="receipt-lines" aria-label="Recount batches"><li v-for="row in page.rows" :key="row.batchId">
        <strong>{{ time(row.queuedAt) }}</strong><p>{{ row.total }} reviewed · {{ row.queued }} queued · {{ row.changed }} changed · {{ row.alreadyQueued }} already queued · {{ row.unavailable }} unavailable</p>
        <a :href="batch(row.batchId)">Open recount receipt {{ row.batchId }}</a>
      </li></ul>
      <p v-if="!page.rows.length && !error && !partial && state==='ready' && !loading">No recount records on this page.</p>
      <div v-if="page.nextAfter || confirmed.length>1" class="paging">
        <span aria-live="polite">Page {{ confirmed.length }} · {{ page.rows.length }} shown</span>
        <button :disabled="loading || state!=='ready' || confirmed.length===1" @click="requested=confirmed.slice(0,-1)">Previous page</button>
        <button :disabled="loading || state!=='ready' || !!error || partial || !page.nextAfter" @click="page.nextAfter && (requested=[...confirmed,page.nextAfter])">Next page</button>
      </div>
    </template>
  </section>
</template>
<style scoped>
.recount-work { padding-bottom: 12px; overflow-wrap: anywhere }
.recount-work > p, .recount-work > small, .recount-work > details, nav, .paging { display:block; margin: 8px 16px; font-size: 13px }
.recount-work a { color: #355340; text-underline-offset: 2px; display:inline-block; margin-right: 12px; padding-block: 4px }
.recount-work li b, .recount-work li strong { display:block }
.recount-work li b { margin-top: 6px }
.scope { font-weight: 600 }
.paging button { margin-left:8px }
</style>
