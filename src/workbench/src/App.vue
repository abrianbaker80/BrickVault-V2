<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import boxes from 'lucide-static/icons/boxes.svg?raw'
import locations from 'lucide-static/icons/layout-grid.svg?raw'
import stockIcon from 'lucide-static/icons/package-search.svg?raw'
import activity from 'lucide-static/icons/clipboard-list.svg?raw'
import intake from 'lucide-static/icons/package-open.svg?raw'
import brand from './assets/brand.webp'
import Register from './Register.vue'
import Activity from './Activity.vue'
import RecordForm from './RecordForm.vue'
import { useWorkbench } from './useWorkbench'
import type { CatalogInput, LocationInput } from './api'
import { commandLabel } from './api'
import { readCountLink } from './countLinks'
import { time } from './presentation'

const Receiving = defineAsyncComponent(() => import('./Receiving.vue'))
const Inventory = defineAsyncComponent(() => import('./Inventory.vue'))
const pages = {
  catalog: { title: 'Catalog', icon: boxes, description: 'Inspect item identity, source rights and catalog mappings.' },
  inventory: { title: 'Inventory', icon: stockIcon, description: 'Find receipt-layer stock across bins and trace its custody.' },
  locations: { title: 'Locations', icon: locations, description: 'Register storage locations and inspect the stock confirmed in each bin.' },
  receiving: { title: 'Receiving', icon: intake, description: 'Receive deliveries, confirm bin counts and prepare unpublished offers.' },
  activity: { title: 'Activity', icon: activity, description: 'Trace recorded changes and check background work.' }
}
type Page = keyof typeof pages
const route = () => {
  const hash = window.location.hash.slice(1)
  if (hash === 'inventory' || hash.startsWith('inventory/')) return 'inventory'
  if (hash === 'receiving' || hash.startsWith('receiving/')) return 'receiving'
  if (hash === 'locations' || hash.startsWith('locations/')) return 'locations'
  return hash === 'activity' ? hash : 'catalog'
}
const page = ref<Page>(route())
const hash = ref(window.location.hash)
const countLink = computed(() => readCountLink(hash.value))
const { session, workspace, receiving, signedOut, readState, error, notice, pending, held, journalError, authBusy, submitting,
  canSend, canCreate, refresh, signIn, signOut, create, record, send, expired } = useWorkbench(page)
const referencePage = computed(() => page.value === 'catalog' || page.value === 'locations')
const password = ref('')
const dialogKind = ref<'catalog' | 'location' | null>(null)
const formWorkspace = ref('')
const heading = ref<HTMLElement>()
watch(() => session.value?.workspaceId, async () => {
  dialogKind.value = null
  await nextTick()
  heading.value?.focus()
})
const addButton = ref<HTMLButtonElement>()
const status = computed(() => ({ loading: 'Loading', ready: 'Connected', disconnected: 'Disconnected', partial: 'Partial read' })[readState.value])
async function navigate() {
  hash.value = window.location.hash
  page.value = route()
  dialogKind.value = null
  await nextTick()
  heading.value?.focus()
}
watch([page, session], () => {
  document.title = (session.value ? pages[page.value].title : 'Sign in') + ' · BrickVault'
  if (!session.value) { dialogKind.value = null; password.value = '' }
}, { immediate: true })
onMounted(() => window.addEventListener('hashchange', navigate))
onBeforeUnmount(() => window.removeEventListener('hashchange', navigate))
function login() {
  const value = password.value
  password.value = ''
  void signIn(value)
}
async function closeForm() {
  dialogKind.value = null
  await nextTick()
  if (addButton.value && !addButton.value.disabled) addButton.value.focus()
  else heading.value?.focus()
}
async function submit(input: { kind: 'catalog'; fields: CatalogInput } | { kind: 'location'; fields: LocationInput }) {
  await create(input, formWorkspace.value)
  if (notice.value || pending.value.length || !session.value) await closeForm()
}
function openForm() {
  if (!session.value) return
  formWorkspace.value = session.value.workspaceId
  error.value = ''
  notice.value = null
  dialogKind.value = page.value === 'catalog' ? 'catalog' : 'location'
}
</script>
<template>
  <a href="#workspace" class="skip" @click.prevent="heading?.focus()">Skip to workspace</a>
  <div class="frame">
    <aside class="rail">
      <div class="brand"><img :src="brand" alt="" width="42" height="39" /><b>BrickVault</b></div>
      <nav v-if="session" aria-label="Workspace">
        <a v-for="(view, key) in pages" :key="key" :href="'#' + key" :aria-current="key === page ? 'page' : undefined">
          <span class="icon" aria-hidden="true" v-html="view.icon"></span>{{ view.title }}
        </a>
      </nav>
      <div class="rail-tail">Workbench</div>
    </aside>
    <div class="stage">
      <header class="utility">
        <div class="breadcrumb">Workspace <span aria-hidden="true">/</span><strong>{{ session ? pages[page].title : 'Sign in' }}</strong></div>
        <div v-if="session" class="session"><span>{{ session.actor }}</span><button :disabled="authBusy || submitting" @click="signOut">Sign out</button></div>
      </header>
      <main id="workspace">
        <template v-if="!session">
          <div class="page-heading"><h1 ref="heading" tabindex="-1">Operator sign-in</h1></div>
          <section v-if="signedOut" class="panel login" aria-label="Sign in">
            <header class="panel-head"><h2>Private workspace</h2></header>
            <form class="form-body" @submit.prevent="login">
              <label>Account<input value="operator" name="username" readonly autocomplete="username" /></label>
              <label>Password<input v-model="password" type="password" name="password" required autocomplete="current-password" /></label>
              <p v-if="error" role="alert" class="warning">{{ error }}</p>
              <button class="primary" :disabled="authBusy">{{ authBusy ? 'Signing in…' : 'Sign in' }}</button>
            </form>
          </section>
          <section v-else class="message" role="status">
            <strong>{{ readState === 'loading' ? 'Checking session…' : 'Disconnected' }}</strong>
            <p v-if="error">{{ error }}</p>
            <button v-if="readState !== 'loading'" @click="refresh()">Retry connection</button>
          </section>
        </template>
        <template v-else>
          <div class="page-heading">
            <div><h1 ref="heading" tabindex="-1">{{ pages[page].title }}</h1><p>{{ pages[page].description }}</p></div>
            <button v-if="referencePage" ref="addButton" class="primary add" :disabled="!canCreate" @click="openForm"><span aria-hidden="true">+</span> {{ page === 'catalog' ? 'New catalog item' : 'New location' }}</button>
          </div>
          <div class="read-bar">
            <span class="read-state" role="status"><i :class="{ live: readState === 'ready' }" aria-hidden="true"></i>{{ status }}</span>
            <span v-if="page === 'receiving' && receiving">Last read {{ time(receiving.asOf) }}</span>
            <span v-else-if="workspace && page !== 'receiving' && page !== 'inventory'">Last read {{ time(workspace.asOf) }}</span>
            <button :disabled="readState === 'loading' || submitting || authBusy" @click="refresh()">Refresh</button>
          </div>
          <p v-if="error && !dialogKind" class="warning" role="alert">{{ error }}</p>
          <p v-if="journalError" class="warning" role="alert">{{ journalError }}</p>
          <div v-if="notice" class="success" role="status"><strong>{{ notice.message }}</strong>
            <div>Correlation ID {{ notice.correlationId }}</div>
            <a v-if="notice.withdrawalLink" :href="notice.withdrawalLink">View withdrawal receipt</a>
            <a v-if="notice.recountLink" :href="notice.recountLink">View recount receipt</a>
            <a v-if="notice.lossLink" :href="notice.lossLink">View posted stock loss</a>
            <a v-if="notice.countLink" :href="notice.countLink">View count receipt</a></div>
          <p v-if="readState === 'partial'" class="warning" role="status">The service returned a partial register. Refresh to verify the complete workspace before making a change.</p>
          <section v-if="pending.length && !submitting" class="message recovery" aria-label="Pending confirmations">
            <h2>Pending confirmation</h2>
            <p>Confirm the original command with the server. A retry keeps its original ID.</p>
            <ul><li v-for="command in pending" :key="command.body.commandId">
              <div><strong>{{ commandLabel(command) }}</strong><small>{{ command.body.commandId }}</small></div>
              <button :disabled="!canSend" @click="send(command)">Retry command</button>
            </li></ul>
          </section>
          <section v-if="held.length" class="message recovery" aria-label="Held commands">
            <h2>Held commands</h2><p>These commands require their original workspace. Review the original audit before recording the change again.</p>
            <ul><li v-for="item in held" :key="item.commandId + item.workspaceId">
              <div><strong>{{ item.workspaceId ? 'Other workspace ' + item.workspaceId : 'Original workspace unknown' }}</strong>
                <small>Command {{ item.commandId }}</small></div>
            </li></ul>
          </section>
          <p v-if="!workspace && readState === 'loading'" role="status">Loading workspace…</p>
          <div v-if="workspace">
            <div v-if="referencePage" class="metrics" role="group" :aria-label="readState === 'ready' ? 'Reference totals' : 'Last received reference counts'">
              <div><span>Catalog items</span><strong>{{ workspace.catalog.length }}</strong></div>
              <div><span>Locations</span><strong>{{ workspace.locations.length }}</strong></div>
              <div><span>Integration blocked</span><strong>{{ workspace.catalog.filter(row => !row.integrationEligible).length }}</strong></div>
            </div>
            <Activity v-if="page === 'activity'" :workspace="workspace" :connected="readState === 'ready'" @failure="expired" />
            <Inventory v-else-if="page === 'inventory'" :key="session.workspaceId" :workspace="workspace" :connected="readState !== 'disconnected'"
              :csrf="session.csrfToken" :controls="{ enabled: canCreate, pending: pending.length > 0, feedback: error || journalError, workspaceId: session.workspaceId, dispatch: record }" @failure="expired" />
            <template v-else-if="page === 'receiving'">
              <Receiving v-if="receiving" :key="session.workspaceId" :stock="receiving" :workspace="workspace" :workspace-id="session.workspaceId" :enabled="canCreate" :uncertain="pending.length > 0 && !submitting" :dispatch="record" />
              <p v-else role="status">{{ readState === 'loading' ? 'Loading receiving records…' : 'Receiving records are unavailable.' }}</p>
            </template>
            <Register v-else :key="session.workspaceId + page" :page="page" :workspace="workspace" :connected="readState !== 'disconnected'"
              :transfers="{ enabled: canCreate, pending: pending.length > 0, feedback: error || journalError, workspaceId: session.workspaceId, dispatch: record }"
              :count-link="countLink" :confirmed-source-id="notice?.sourceLocationId" @failure="expired" />
          </div>
          <RecordForm v-if="dialogKind" :kind="dialogKind" :enabled="canCreate" :saving="submitting"
            :error="error || journalError || (readState === 'disconnected' ? 'Reconnect before sending.' : '')" @close="closeForm" @save="submit" />
        </template>
      </main>
    </div>
  </div>
</template>
