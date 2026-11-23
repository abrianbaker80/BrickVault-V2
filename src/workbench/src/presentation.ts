export function label(value: string) {
  const text = value.replaceAll('-', ' ')
  return text[0].toUpperCase() + text.slice(1)
}

const formatter = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' })
export function time(value: string) {
  return formatter.format(new Date(value))
}
