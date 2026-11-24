import type { Catalog } from './api'

export function itemLabel(catalog: Catalog[], id: string, recordedRevision?: number | null) {
  const row = catalog.find(item => item.id === id)
  if (!row) return 'Catalog record ' + id + (recordedRevision ? ' · Revision ' + recordedRevision : '')
  const color = row.colorState === 'known' ? 'color ' + row.colorId : row.colorState
  const description = [row.itemNumber, row.variant, row.name, color, row.condition, row.completeness].filter(Boolean).join(' · ')
  if (recordedRevision && recordedRevision !== row.revision) {
    return `Recorded catalog ${id} · Revision ${recordedRevision}. Current catalog (revision ${row.revision}) · ${description}`
  }
  return description
}

function currencyFormat(currency: string) {
  return new Intl.NumberFormat('en', { style: 'currency', currency, currencyDisplay: 'code' })
}
function fractionDigits(currency: string) {
  return currencyFormat(currency).resolvedOptions().maximumFractionDigits ?? 2
}
export function money(minor: number | null, currency: string) {
  return minor === null ? 'Unknown' : currencyFormat(currency).format(minor / 10 ** fractionDigits(currency))
}
export function parseMoney(text: string, currency: string) {
  const digits = fractionDigits(currency)
  const parts = /^(\d+)(?:\.(\d+))?$/.exec(text.trim())
  if (!parts || (parts[2]?.length ?? 0) > digits) throw new Error('Enter a valid ' + currency + ' amount with up to ' + digits + ' decimal places.')
  const minor = Number(parts[1] + (parts[2] || '').padEnd(digits, '0'))
  if (!Number.isSafeInteger(minor) || minor > 100000000) throw new Error('Amount exceeds the allowed range.')
  return minor
}
