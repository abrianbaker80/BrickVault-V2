import type { Catalog } from './api'
import legacyCurrencies from '../../BrickVault.Api/legacy-offer-price-currencies.json'

const compatibleCurrencies = new Set(legacyCurrencies)
export function legacyPriceCompatible(currency: string) {
  return compatibleCurrencies.has(currency)
}

export function recordedOfferPrice(offer: { currency: string; unitPriceMinor: number; priceScaleVerified?: boolean }) {
  if (offer.priceScaleVerified === true || legacyPriceCompatible(offer.currency)) {
    return money(offer.unitPriceMinor, offer.currency) + ' per unit'
  }
  return offer.currency + ' · ' + offer.unitPriceMinor + ' recorded minor units per unit. Verify the original price.'
}

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

// BrickVault persists integer minor units. Keep this scale stable for existing records.
// Non-default minor units from ISO 4217 List One, published 2026-09-17.
const currencyScales: Readonly<Record<string, number>> = {
  BIF: 0, CLP: 0, DJF: 0, GNF: 0, ISK: 0, JPY: 0, KMF: 0, KRW: 0, PYG: 0,
  RWF: 0, UGX: 0, UYI: 0, VND: 0, VUV: 0, XAF: 0, XOF: 0, XPF: 0,
  BHD: 3, IQD: 3, JOD: 3, KWD: 3, LYD: 3, OMR: 3, TND: 3,
  CLF: 4, UYW: 4
}

function fractionDigits(currency: string) {
  return currencyScales[currency.toUpperCase()] ?? 2
}
export function money(minor: number | string | null, currency: string) {
  if (minor === null) return 'Unknown'
  const digits = fractionDigits(currency)
  const format = new Intl.NumberFormat('en', {
    style: 'currency', currency, currencyDisplay: 'code', minimumFractionDigits: digits, maximumFractionDigits: digits
  })
  if (typeof minor === 'number') return format.format(minor / 10 ** digits)
  const amount = minor.padStart(digits+1, '0')
  const whole = digits ? amount.slice(0,-digits) : amount
  return format.formatToParts(BigInt(whole)).map(part=>part.type==='fraction' ? amount.slice(-digits) : part.value).join('')
}

export function receiptCost(minor: number | string | null, currency: string) {
  const display = money(minor, currency)
  if (minor === null || /^0+$/.test(String(minor)) || legacyPriceCompatible(currency)) return display
  return display + ' at current precision (' + minor + ' recorded minor units)'
}

export function parseMoney(text: string, currency: string) {
  const digits = fractionDigits(currency)
  const parts = /^(\d+)(?:\.(\d+))?$/.exec(text.trim())
  if (!parts || (parts[2]?.length ?? 0) > digits) throw new Error('Enter a valid ' + currency + ' amount with up to ' + digits + ' decimal places.')
  const minor = Number(parts[1] + (parts[2] || '').padEnd(digits, '0'))
  if (!Number.isSafeInteger(minor) || minor > 100000000) throw new Error('Amount exceeds the allowed range.')
  return minor
}
