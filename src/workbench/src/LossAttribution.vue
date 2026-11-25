<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import type { QuarantineSnapshot } from './api'
import { quarantineLink } from './countLinks'
import { time } from './presentation'

type RecordRow = QuarantineSnapshot['quarantines'][number]
const props = defineProps<{ stock:QuarantineSnapshot; enabled:boolean; pageNumber:number }>()
const mode = defineModel<'' | 'none' | 'records'>('mode', {required:true})
const entries = defineModel<Record<string,{row:RecordRow; amount:string}>>('entries', {required:true})
const emit = defineEmits<{ previous:[]; next:[] }>()
const heading = ref<HTMLElement>()
watch(() => props.pageNumber,async () => {await nextTick(); heading.value?.focus()})
function choose(value:'none'|'records') {
  if (value === 'none') entries.value = {}
  mode.value = value
}
function update(row:RecordRow,event:Event) {
  const amount=(event.target as HTMLInputElement).value
  if (!amount || /^0+$/.test(amount)) delete entries.value[row.id]
  else entries.value[row.id]={row,amount}
}
</script>
<template>
  <fieldset :disabled="!enabled" class="attribution">
    <legend>Identify the missing pieces</legend>
    <p>The count does not identify which pieces are missing. Select what you found in this bin.</p>
    <label class="choice"><input type="radio" name="loss-attribution" :checked="mode==='none'" @change="choose('none')" />
      None of the missing pieces were in quarantine</label>
    <label class="choice"><input type="radio" name="loss-attribution" :checked="mode==='records'" @change="choose('records')" />
      Some or all came from named quarantine records</label>
    <template v-if="mode==='records'">
      <h3 ref="heading" tabindex="-1">Quarantine records</h3>
      <ul class="receipt-lines" aria-label="Quarantine records to attribute">
        <li v-for="q in stock.quarantines" :key="q.id"><strong :id="'damage-reason-'+q.id">{{ q.reason }}</strong>
          <p :id="'damage-remaining-'+q.id">{{ q.remainingQuantity }} remaining of {{ q.quantity }} · {{ time(q.recordedAt) }}</p>
          <small>Quarantine {{ q.id }}</small>
          <label v-if="q.remainingQuantity">Missing from record {{ q.id }}
            <input type="text" inputmode="numeric" pattern="[0-9]+" :aria-describedby="'damage-reason-'+q.id+' damage-remaining-'+q.id" :value="entries[q.id]?.amount || ''" @input="update(q,$event)" /></label>
          <a :href="quarantineLink(stock.position.location.id,stock.position.layer.placementId,q.id)">Original quarantine receipt</a>
        </li></ul>
      <nav v-if="pageNumber>1 || stock.nextAfter" aria-label="Quarantine attribution pages">
        <button type="button" :disabled="pageNumber===1" @click="emit('previous')">Newer quarantine records</button>
        <span>Page {{ pageNumber }}</span><button type="button" :disabled="!stock.nextAfter" @click="emit('next')">Older quarantine records</button></nav>
      <section aria-label="Selected quarantine records"><h3>Selected records</h3>
        <p v-if="!Object.keys(entries).length">Enter the missing quantity for each affected record.</p>
        <ul v-else><li v-for="entry in entries" :key="entry.row.id">
          {{ entry.row.reason }} · {{ entry.amount }} of {{ entry.row.remainingQuantity }} remaining
          <small>Quarantine {{ entry.row.id }}</small>
          <button type="button" :aria-label="'Remove attribution for '+entry.row.id" @click="delete entries[entry.row.id]">Remove</button>
        </li></ul>
      </section>
    </template>
  </fieldset>
</template>
<style scoped>
.attribution {display:grid; gap:10px; min-width:0; padding:12px; border:1px solid var(--line); border-radius:6px}
legend {font-weight:650; padding:0 4px}
.attribution :is(h3,p,ul) {margin:0}
.attribution .choice {display:flex; align-items:baseline; gap:6px}
.choice input {flex:0 0 auto; width:16px}
.receipt-lines {padding:0}
label {display:grid; gap:4px}
nav {display:flex; align-items:center; gap:8px; flex-wrap:wrap}
.attribution small {display:block; color:var(--muted)}
</style>
