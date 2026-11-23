const parts = import.meta.glob<string>('./art/*.webp', { eager: true, query: '?url', import: 'default' })

export function partImage(key: string): string {
  const file = parts[`./art/${key}.webp`]
  if (!file) throw new Error(`Missing illustration for ${key}`)
  return file
}
