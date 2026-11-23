export type ReadMode = 'Normal' | 'Loading' | 'Empty' | 'Partial' | 'Stale' | 'Failed'
export type Scope = 'Selected lots' | 'Matching lots' | 'Entire inventory'
type Placement = { bin: string; units: number }
type Observation = { units: number; asOf: string; stale?: boolean }
type Source = { id: string; name: string; units: number }
export type Lot = {
  id: string
  part: string
  name: string
  type: 'Part' | 'Minifigure' | 'Set'
  color: string
  condition: 'New' | 'Used'
  art: string
  placements: Placement[]
  reserved: number
  unavailable: number
  sources: Source[]
  custodian: string
  desired: number
  desiredAt: string
  observed: Observation | null
  revision: number
  queued: boolean
}

type Seed = [
  string,
  string,
  string,
  Lot['type'],
  string,
  Lot['condition'],
  string,
  Placement[],
  number,
  number,
  number | null,
  boolean?
]
const seeds: Seed[] = [
  [
    'BV-1024',
    '3001',
    'Brick 2 × 4',
    'Part',
    'Red',
    'New',
    'brick-red',
    [
      { bin: 'A-01', units: 84 },
      { bin: 'B-04', units: 36 }
    ],
    18,
    2,
    100
  ],
  [
    'BV-1025',
    '3001',
    'Brick 2 × 4',
    'Part',
    'Bright Yellow',
    'New',
    'brick-yellow',
    [{ bin: 'A-02', units: 48 }],
    4,
    0,
    44
  ],
  [
    'BV-1026',
    '3020',
    'Plate 2 × 4',
    'Part',
    'Blue',
    'Used',
    'plate-blue',
    [{ bin: 'B-01', units: 26 }],
    6,
    0,
    20
  ],
  [
    'BV-1027',
    '3068b',
    'Tile 2 × 2 with Groove',
    'Part',
    'Tan',
    'New',
    'tile-sand',
    [{ bin: 'C-03', units: 35 }],
    5,
    0,
    null
  ],
  [
    'BV-1028',
    '3039',
    'Slope 45° 2 × 2',
    'Part',
    'Dark Green',
    'Used',
    'slope-green',
    [{ bin: 'B-04', units: 9 }],
    5,
    0,
    6,
    true
  ],
  [
    'BV-1029',
    '3023',
    'Plate 1 × 2',
    'Part',
    'Black',
    'New',
    'plate-black',
    [{ bin: 'A-01', units: 62 }],
    8,
    0,
    54
  ],
  [
    'BV-1030',
    '6143',
    'Brick, Round 2 × 2',
    'Part',
    'White',
    'New',
    'round-white',
    [{ bin: 'C-02', units: 7 }],
    7,
    0,
    0
  ],
  [
    'BV-1031',
    'FIG-07a',
    'Harbor Mechanic with Printed Utility Vest and Dark Blue Cap',
    'Minifigure',
    'Multicolor',
    'Used',
    'figure',
    [{ bin: 'D-02', units: 3 }],
    1,
    1,
    null
  ],
  [
    'BV-1032',
    'SET-011b',
    'Harbor Workshop · alternate box',
    'Set',
    'Multicolor',
    'New',
    'set',
    [{ bin: 'D-01', units: 2 }],
    0,
    2,
    0
  ],
  [
    'BV-1033',
    '3003',
    'Brick 2 × 2',
    'Part',
    'White',
    'New',
    'brick-white',
    [{ bin: 'A-03', units: 55 }],
    5,
    0,
    50
  ],
  [
    'BV-1034',
    '3001',
    'Brick 2 × 4',
    'Part',
    'Blue',
    'Used',
    'brick-blue',
    [{ bin: 'A-02', units: 0 }],
    0,
    0,
    0
  ],
  [
    'BV-1035',
    '3070b',
    'Tile 1 × 1 with Groove',
    'Part',
    'Yellow',
    'New',
    'tile-yellow',
    [{ bin: 'C-03', units: 31 }],
    2,
    0,
    29
  ]
]
export const batches = [
  { id: 'IN-024', name: 'North Quay intake' },
  { id: 'IN-026', name: 'Workshop delivery' },
  { id: 'IN-027', name: 'Sorting bench' }
]
export function initialLots(): Lot[] {
  return seeds.map(
    ([id, part, name, type, color, condition, art, placements, reserved, unavailable, remote, stale], i) => {
      const units = placements.reduce((n, p) => n + p.units, 0)
      return {
        id,
        part,
        name,
        type,
        color,
        condition,
        art,
        placements,
        reserved,
        unavailable,
        revision: 1,
        queued: false,
        desired: units - reserved - unavailable,
        desiredAt: '18 Apr · 09:30 UTC',
        observed:
          remote === null
            ? null
            : { units: remote, asOf: stale ? '17 Apr · 07:20 UTC' : '18 Apr · 09:42 UTC', stale },
        custodian: 'Harbor stockroom',
        sources:
          i === 0
            ? [
                { ...batches[0], units: 75 },
                { ...batches[1], units: 45 }
              ]
            : [{ ...batches[i % 3], units }]
      }
    }
  )
}
export const onHand = (l: Lot) => l.placements.reduce((n, p) => n + p.units, 0)
export const sellable = (l: Lot) => onHand(l) - l.reserved - l.unavailable
export function stockStatus(l: Lot) {
  if (l.queued) return ['Queued', 'queued'] as const
  if (onHand(l) === 0) return ['No stock', 'neutral'] as const
  if (sellable(l) === 0)
    return l.unavailable === onHand(l)
      ? (['On hold', 'warning'] as const)
      : (['Reserved', 'neutral'] as const)
  if (sellable(l) < 6) return ['Low stock', 'warning'] as const
  return ['Available', 'positive'] as const
}
export const readModes: ReadMode[] = ['Normal', 'Loading', 'Empty', 'Partial', 'Stale', 'Failed']
export const statuses = ['Available', 'Low stock', 'Reserved', 'On hold', 'No stock', 'Queued']
export const scopes: Scope[] = ['Selected lots', 'Matching lots', 'Entire inventory']
export const locations = [...new Set(seeds.flatMap((seed) => seed[7].map((p) => p.bin)))].sort()
type Target = { id: string; revision: number }
export type Receipt = { queued: string[]; changed: string[]; alreadyQueued: string[] }
export type Register = {
  lots: Lot[]
  query: string
  condition: string
  location: string
  batch: string
  status: string
  read: ReadMode
  visible: string[]
  pending: boolean
  selected: string[]
  inspected: string | null
  scope: Scope
  preview: Target[] | null
  receipt: Receipt | null
  simulated: boolean
  compact: boolean
}

function results(s: Register): string[] {
  if (s.read === 'Failed' || s.read === 'Empty' || s.read === 'Loading') return []
  const query = s.query.toLowerCase().trim()
  const source = s.read === 'Partial' ? s.lots.slice(0, 4) : s.lots
  return source
    .filter(
      (l) =>
        (!s.condition || l.condition === s.condition) &&
        (!s.location || l.placements.some((p) => p.bin === s.location)) &&
        (!s.batch || l.sources.some((source) => source.id === s.batch)) &&
        (!s.status || stockStatus(l)[0] === s.status) &&
        (!query ||
          [l.name, l.id, l.part, l.color, l.type, ...l.placements.map((p) => p.bin)]
            .join(' ')
            .toLowerCase()
            .includes(query))
    )
    .map((l) => l.id)
}

export const authoritative = (s: Register) => s.read === 'Normal' && !s.pending
export function resultCount(s: Register) {
  if (s.read === 'Loading' || s.read === 'Failed') return 'Count unknown'
  return s.visible.length + (s.visible.length === 1 ? ' lot' : ' lots')
}
export function scopeIds(s: Register, scope: Scope) {
  if (scope === 'Selected lots') return s.selected
  if (scope === 'Matching lots') return s.visible
  return s.lots.map((l) => l.id)
}
export function initialRegister(width: number): Register {
  const lots = initialLots()
  const compact = width < 920
  return {
    lots,
    query: '',
    condition: '',
    location: '',
    batch: '',
    status: '',
    read: 'Normal',
    visible: lots.map((l) => l.id),
    pending: false,
    selected: [],
    inspected: compact ? null : lots[0].id,
    compact,
    scope: 'Selected lots',
    preview: null,
    receipt: null,
    simulated: false
  }
}

/** Each mount is an isolated fictional session. The timer models an interrupted read. */
export function createRegister(width: number) {
  let state = initialRegister(width)
  let request = 0
  const listeners = new Set<() => void>()
  const timers = new Set<ReturnType<typeof setTimeout>>()
  function update(changes: Partial<Register>) {
    state = { ...state, ...changes }
    listeners.forEach((fn) => fn())
  }
  function filter(changes: Partial<Pick<Register, 'query' | 'location' | 'condition' | 'batch' | 'status'>>) {
    const token = ++request
    const next = { ...state, ...changes }
    update({ ...changes, pending: true, inspected: null })
    const timer = setTimeout(
      () => {
        timers.delete(timer)
        if (token === request) update({ visible: results(next), pending: false })
      },
      next.query.length === 1 ? 360 : 80
    )
    timers.add(timer)
  }
  function changeRead(read: ReadMode) {
    ++request
    update({ read, pending: false, inspected: null, visible: results({ ...state, read }) })
  }
  function openPreview(scope: Scope) {
    if (!authoritative(state)) return
    const ids = scopeIds(state, scope)
    update({
      scope,
      preview: state.lots.filter((l) => ids.includes(l.id)).map((l) => ({ id: l.id, revision: l.revision })),
      receipt: null,
      simulated: false
    })
  }
  return {
    get: () => state,
    subscribe(fn: () => void) {
      listeners.add(fn)
      return () => {
        listeners.delete(fn)
      }
    },
    dispose() {
      ++request
      timers.forEach(clearTimeout)
      timers.clear()
      listeners.clear()
    },
    filter,
    changeRead,
    resize(width: number) {
      const compact = width < 920
      if (compact !== state.compact) update({ compact, inspected: null })
    },
    inspect(id: string | null) {
      update({ inspected: id })
    },
    toggle(id: string) {
      if (!authoritative(state)) return
      update({
        selected: state.selected.includes(id)
          ? state.selected.filter((x) => x !== id)
          : [...state.selected, id]
      })
    },
    clearSelection() {
      update({ selected: [] })
    },
    openPreview,
    simulateChange() {
      if (!state.preview?.length || state.simulated) return
      const first = state.preview[0].id
      update({
        lots: state.lots.map((l) => (l.id === first ? { ...l, revision: l.revision + 1 } : l)),
        simulated: true
      })
    },
    confirm() {
      if (!state.preview?.length || !authoritative(state)) return
      const receipt: Receipt = { queued: [], changed: [], alreadyQueued: [] }
      const lots = state.lots.map((l) => {
        const target = state.preview?.find((t) => t.id === l.id)
        if (!target) return l
        if (l.revision !== target.revision) {
          receipt.changed.push(l.id)
          return l
        }
        if (l.queued) {
          receipt.alreadyQueued.push(l.id)
          return l
        }
        receipt.queued.push(l.id)
        return { ...l, queued: true, revision: l.revision + 1 }
      })
      update({ lots, receipt, preview: null, visible: results({ ...state, lots }) })
    },
    closePreview() {
      update({ preview: null, receipt: null, simulated: false })
    }
  }
}
export type Controller = ReturnType<typeof createRegister>

export const readText: Record<ReadMode, string> = {
  Normal: '',
  Loading: 'Loading inventory…',
  Empty: 'Verified empty result. No inventory lots were returned.',
  Partial: 'Partial result. Only the first four lots were verified.',
  Stale: 'Stale result. Showing the last verified inventory snapshot.',
  Failed: 'Inventory could not be read. Stock quantities are unknown.'
}

export function metricRows(s: Register) {
  const unavailableRead = s.read === 'Failed' || s.read === 'Loading'
  const lots = s.read === 'Partial' ? s.lots.slice(0, 4) : s.read === 'Empty' ? [] : s.lots
  const values = [
    { label: 'On hand', value: lots.reduce((n, l) => n + onHand(l), 0), unit: 'units' },
    { label: 'Sellable', value: lots.reduce((n, l) => n + sellable(l), 0), unit: 'units' },
    { label: 'Reserved', value: lots.reduce((n, l) => n + l.reserved, 0), unit: 'units' },
    { label: 'Unavailable', value: lots.reduce((n, l) => n + l.unavailable, 0), unit: 'units' }
  ]
  return values.map((m) => ({ ...m, display: unavailableRead ? '—' : String(m.value) }))
}

export function registerInteractions(host: HTMLElement, api: Controller) {
  const resize = new ResizeObserver(() => api.resize(host.clientWidth))
  resize.observe(host)
  function keyboard(e: KeyboardEvent) {
    const target = e.target as HTMLElement
    const editing = target.closest(
      'input:not([type=checkbox]):not([type=radio]),textarea,select,[contenteditable="true"]'
    )
    if (
      e.key === '/' &&
      !e.altKey &&
      !e.ctrlKey &&
      !e.metaKey &&
      !editing &&
      !document.querySelector('dialog[open]')
    ) {
      e.preventDefault()
      host.querySelector<HTMLInputElement>('[data-inventory-search]')?.focus()
    }
  }
  document.addEventListener('keydown', keyboard)
  return () => {
    resize.disconnect()
    document.removeEventListener('keydown', keyboard)
  }
}

export function displayDialog(dialog: HTMLDialogElement | null, open: boolean) {
  if (!dialog || dialog.open === open) return
  if (open) dialog.showModal()
  else dialog.close()
}
