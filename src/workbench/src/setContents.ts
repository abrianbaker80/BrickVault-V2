export type Contents = { includesBox: boolean; includesInstructions: boolean }
export type Presence = '' | 'yes' | 'no'

export function recordedContents(box: Presence, instructions: Presence): Contents | undefined {
  if (!box || !instructions) return undefined
  return { includesBox: box === 'yes', includesInstructions: instructions === 'yes' }
}

export function contentKey(value?: Contents | null) {
  if (!value) return ''
  return (value.includesBox ? 'box' : 'no-box') + '/' + (value.includesInstructions ? 'instructions' : 'no-instructions')
}

export function contentLabel(value?: Contents | null) {
  if (!value) return 'Box and instructions unrecorded'
  return (value.includesBox ? 'Box included' : 'No box') + ' · ' +
    (value.includesInstructions ? 'Instructions included' : 'No instructions')
}

export function positionContents(layer: { currentCatalog: { itemType: string }; setContents?: Contents }) {
  return layer.setContents || layer.currentCatalog.itemType === 'set' ? contentLabel(layer.setContents) : ''
}

export function inspectionLink(position: string, receipt?: string) {
  return '#inventory/set-contents/' + position + (receipt ? '/' + receipt : '')
}
