<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { Dispatch, ReceivingWorkspace, Workspace } from './api'
import ReceivingTask from './ReceivingTask.vue'
import ReceivingBalance from './ReceivingBalance.vue'
import { parseMoney } from './receiving'

const props = defineProps<{ stock: ReceivingWorkspace; workspace: Workspace; workspaceId: string; enabled: boolean; uncertain: boolean; dispatch: Dispatch }>()
const selected = ref(window.location.hash.split('/')[1] || '')
const purchase = computed(() => props.stock.purchases.find(p => p.id === selected.value) || props.stock.purchases[0])
const adding = ref(false)
const reference = ref('')
const source = ref('')
const currency = ref('')
const ordered = ref<number>()
const charge = ref('')
const issue = ref('')
const first = ref<HTMLInputElement>()
function select(id: string) {
  selected.value = id
  history.replaceState(history.state, '', '#receiving/' + id)
  adding.value = false
}
async function openPurchase() {
  adding.value = true
  issue.value = ''; reference.value = ''; source.value = ''; currency.value = ''; ordered.value = undefined; charge.value = ''
  await nextTick()
  first.value?.focus()
}
watch(() => props.uncertain, pending => { if (pending) adding.value = false })
async function savePurchase() {
  issue.value = ''
  if (!props.enabled || !ordered.value) return
  const iso = currency.value.trim().toUpperCase()
  let pendingChargeMinor: number | null
  try { pendingChargeMinor = charge.value.trim() ? parseMoney(charge.value, iso) : null }
  catch (error) { issue.value = (error as Error).message; return }
  const result = await props.dispatch({ kind: 'purchase', workspaceId: props.workspaceId, body: {
    commandId: crypto.randomUUID(), expectedRevision: 0, reference: reference.value.trim(),
    source: source.value.trim(), currency: iso, orderedQuantity: ordered.value, pendingChargeMinor
  } })
  if (result) select(result.entityId)
}
</script>
<template>
  <section class="panel receiving-picker" aria-label="Purchase selection">
    <label v-if="stock.purchases.length">Purchase
      <select :value="purchase?.id" @change="select(($event.target as HTMLSelectElement).value)">
        <option v-for="p in stock.purchases" :key="p.id" :value="p.id">{{ p.reference }} · {{ p.source }}</option>
      </select>
    </label>
    <div v-else><h2>{{ stock.complete ? 'No purchases yet' : 'No purchases in this read' }}</h2><p>Record a purchase to start receiving its deliveries.</p></div>
    <button type="button" class="primary add" :disabled="!enabled || adding" @click="openPurchase"><span aria-hidden="true">+</span> New purchase</button>
  </section>
  <section v-if="adding" class="panel purchase-form" aria-labelledby="new-purchase">
    <header class="panel-head"><h2 id="new-purchase">New purchase</h2></header>
    <form class="form-body" @submit.prevent="savePurchase">
      <fieldset class="fields" :disabled="!enabled">
        <label>Purchase reference<input ref="first" v-model="reference" required maxlength="64" /></label>
        <label>Purchase source<input v-model="source" required maxlength="160" /></label>
        <label>Ordered quantity<input v-model="ordered" type="number" inputmode="numeric" min="1" max="1000000" required /></label>
        <label>Currency code<input v-model="currency" pattern="[A-Za-z]{3}" maxlength="3" placeholder="USD" required /></label>
        <label>Unallocated purchase charge<input v-model="charge" inputmode="decimal" aria-describedby="charge-hint" /></label>
        <p id="charge-hint" class="field-hint">Leave blank if unknown. The charge stays separate from item costs.</p>
      </fieldset>
      <p v-if="issue" role="alert" class="warning">{{ issue }}</p>
      <div class="actions"><button type="button" @click="adding = false">Cancel</button><button class="primary" :disabled="!enabled">Save purchase</button></div>
    </form>
  </section>
  <div v-else-if="purchase" class="receiving-grid">
    <ReceivingTask :key="purchase.id" :purchase="purchase" :workspace="workspace" :workspace-id="workspaceId" :enabled="enabled" :dispatch="dispatch" />
    <ReceivingBalance :purchase="purchase" :catalog="workspace.catalog" />
  </div>
</template>
