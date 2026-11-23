import './shell.css'
import { partImage } from './art'
import boxes from 'lucide-static/icons/boxes.svg?raw'
import receive from 'lucide-static/icons/package-open.svg?raw'
import orders from 'lucide-static/icons/clipboard-list.svg?raw'
import recovery from 'lucide-static/icons/arrow-left-right.svg?raw'
import guide from 'lucide-static/icons/book-open.svg?raw'
import arrow from 'lucide-static/icons/arrow-right.svg?raw'
import workbench from 'lucide-static/icons/panels-top-left.svg?raw'
import ledger from 'lucide-static/icons/columns-3.svg?raw'
import code from 'lucide-static/icons/code-xml.svg?raw'

const views = {
  inventory: {
    name: 'Inventory',
    nav: 'Inventory',
    icon: boxes,
    description: 'Inspect lots, check placements and review stock availability.'
  },
  receiving: {
    name: 'Receiving',
    nav: 'Receiving',
    icon: receive,
    description: 'Count incoming stock and confirm its storage location.'
  },
  orders: {
    name: 'Orders',
    nav: 'Orders',
    icon: orders,
    description: 'Follow a reserved order through picking, packing and handoff.'
  },
  recovery: {
    name: 'Reconciliation',
    nav: 'Reconciliation',
    icon: recovery,
    description: 'Resolve an uncertain marketplace update against a fresh observation.'
  }
}
type View = keyof typeof views
type Design = 'workbench' | 'ledger'
type Engine = 'react' | 'vue'
type Route = { view: View; design: Design; engine: Engine }

function readRoute(): Route {
  const params = new URLSearchParams(location.protocol === 'file:' ? location.hash.slice(1) : location.search)
  const view = params.get('view') as View
  return {
    view: Object.hasOwn(views, view) ? view : 'inventory',
    design: params.get('design') === 'ledger' ? 'ledger' : 'workbench',
    engine: params.get('engine') === 'react' ? 'react' : 'vue'
  }
}
function href(next: Route) {
  const params = new URLSearchParams(next)
  return (location.protocol === 'file:' ? '#' : '?') + params
}
function icon(svg: string) {
  return '<span class="shell-icon" aria-hidden="true">' + svg + '</span>'
}
const app = document.querySelector<HTMLElement>('#app')!
let route = readRoute()
let detach: (() => void) | undefined
let generation = 0
let operationModule: typeof import('./operations') | undefined
app.innerHTML = `
  <a class="skip-link" href="#workspace">Skip to workspace</a>
  <div class="frame">
    <header class="rail"><div class="brand"><img src="${partImage('brand')}" alt=""><b>BrickVault</b></div>
      <nav class="primary-nav" aria-label="Workspace">${Object.entries(views)
        .map(([id, v]) => '<a data-view="' + id + '">' + icon(v.icon) + '<span>' + v.nav + '</span></a>')
        .join('')}</nav>
      <div class="rail-tail"><button type="button" class="guide-button">${icon(guide)}<span>Review guide</span></button>
        <div class="sample"><i aria-hidden="true"></i> Synthetic workspace</div></div>
    </header>
    <div class="stage"><div class="utility"><div class="breadcrumb"><span>Workspace</span><span aria-hidden="true">/</span><strong id="current-view"></strong></div>
      <div class="review-controls"><div class="design-switch" role="group" aria-label="Visual direction">
      <button type="button" data-design="workbench">${icon(workbench)}Workbench</button>
      <button type="button" data-design="ledger">${icon(ledger)}Ledger</button></div>
      <label class="engine-label">${icon(code)}<select id="engine" aria-label="Inventory framework"><option value="react">React</option><option value="vue">Vue</option></select></label></div>
    </div>
    <main id="workspace" tabindex="-1"><div class="page-heading"><div><h1 tabindex="-1"></h1><p id="intro"></p></div><a class="shortcut" data-view="receiving">Receive stock ${icon(arrow)}</a></div><div id="module"></div></main></div>
  </div>
  <dialog class="guide" aria-labelledby="guide-title"><header><h2 id="guide-title">Review guide</h2><button type="button" class="guide-close" aria-label="Close review guide">×</button></header>
    <p>Choose a layout at the top. The framework selector changes the inventory register. Receiving, orders and reconciliation each use an independent fictional record.</p>
    <ol><li><strong>Inspect a lot.</strong> Search for BV-1024 and compare its two storage locations. Check another lot and preview a recount to see exactly which IDs it affects.</li>
    <li><strong>Receive and put away.</strong> Enter the item shown, count a partial receipt, then scan its destination.</li>
    <li><strong>Pick and pack.</strong> Scan the expected bags. Try a shortage or an order amendment and follow the next action.</li>
    <li><strong>Reconcile an uncertain send.</strong> Read the marketplace observation before deciding whether to issue a correction.</li></ol>
    <p>Order progress survives a reload in this tab. Receiving and reconciliation restart on reload. Returning to Inventory or changing framework starts a fresh register.</p>
    <footer><button class="reset" type="button">Reset all demo records</button><button class="guide-done" type="button">Done</button></footer></dialog>`

const host = app.querySelector<HTMLElement>('#module')!
const heading = app.querySelector<HTMLHeadingElement>('h1')!
const dialog = app.querySelector<HTMLDialogElement>('.guide')!
function updateChrome() {
  const view = views[route.view]
  document.documentElement.dataset.direction = route.design
  document.title = view.name + ' · BrickVault'
  heading.textContent = view.name
  app.querySelector('#intro')!.textContent = view.description
  app.querySelector('#current-view')!.textContent = view.name
  app.querySelector<HTMLAnchorElement>('.shortcut')!.hidden = route.view !== 'inventory'
  app.querySelectorAll<HTMLAnchorElement>('[data-view]').forEach((link) => {
    const target = link.dataset.view as View
    link.href = href({ ...route, view: target })
    if (route.view === target) link.setAttribute('aria-current', 'page')
    else link.removeAttribute('aria-current')
  })
  app
    .querySelectorAll<HTMLButtonElement>('[data-design]')
    .forEach((button) => button.setAttribute('aria-pressed', String(button.dataset.design === route.design)))
  app.querySelector<HTMLSelectElement>('#engine')!.value = route.engine
}

async function mount() {
  const id = ++generation
  detach?.()
  detach = undefined
  host.replaceChildren()
  host.setAttribute('aria-busy', 'true')
  try {
    let render: (host: HTMLElement) => () => void
    if (route.view === 'inventory') {
      const module =
        route.engine === 'react' ? await import('./inventory/react') : await import('./inventory/vue')
      render = module.mountInventory
    } else {
      const view = route.view
      operationModule = await import('./operations')
      const { mountOperations } = operationModule
      render = (target) => mountOperations(target, view)
    }
    if (id !== generation) return
    detach = render(host)
  } catch (error) {
    if (id !== generation) return
    host.textContent = 'The workspace could not load. Reload the preview to retry.'
    console.error(error)
  } finally {
    if (id === generation) host.removeAttribute('aria-busy')
  }
}
function navigate(next: Route, push = true) {
  const previous = route
  route = next
  if (push) history.pushState(null, '', href(route))
  updateChrome()
  if (route.view !== previous.view || (route.view === 'inventory' && previous.engine !== route.engine))
    void mount()
  if (route.view !== previous.view) {
    heading.focus({ preventScroll: true })
    window.scrollTo(0, 0)
  }
}
app.querySelectorAll<HTMLButtonElement>('[data-design]').forEach((button) =>
  button.addEventListener('click', () => {
    if (route.design !== button.dataset.design)
      navigate({ ...route, design: button.dataset.design as Design })
  })
)
app.querySelectorAll<HTMLAnchorElement>('[data-view]').forEach((link) =>
  link.addEventListener('click', (event) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    if (route.view !== link.dataset.view) navigate({ ...route, view: link.dataset.view as View })
  })
)
app
  .querySelector<HTMLSelectElement>('#engine')!
  .addEventListener('change', (event) =>
    navigate({ ...route, engine: (event.target as HTMLSelectElement).value as Engine })
  )
app.querySelector('.skip-link')!.addEventListener('click', (event) => {
  event.preventDefault()
  app.querySelector<HTMLElement>('main')!.focus()
})
window.addEventListener('popstate', () => navigate(readRoute(), false))
app.querySelector('.guide-button')!.addEventListener('click', () => dialog.showModal())
for (const name of ['.guide-close', '.guide-done'])
  app.querySelector(name)!.addEventListener('click', () => dialog.close())
app.querySelector('.reset')!.addEventListener('click', async () => {
  operationModule ??= await import('./operations')
  operationModule.resetOperations()
  dialog.close()
  await mount()
})
updateChrome()
void mount()
