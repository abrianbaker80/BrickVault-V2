import { nextTick, ref, shallowRef, watch } from 'vue'
import { ContractFailure, explain } from './api'

export type PositionReadState = 'ready' | 'loading' | 'unavailable'

export function usePositionPage<T extends { complete: boolean; nextAfter: string | null }>(
  props: { asOf: string; state: PositionReadState },
  read: (after: string, previous: T | null) => Promise<T>,
  onFailure: (reason: unknown) => void,
) {
  const page = shallowRef<T | null>(null)
  const confirmed = shallowRef<string[]>([''])
  const requested = shallowRef<string[]>([''])
  const attempt = ref(0)
  const loading = ref(false)
  const partial = ref(false)
  const error = ref('')
  const heading = ref<HTMLElement>()
  const retry = ref<HTMLButtonElement>()

  watch(() => [props.asOf, props.state, requested.value, attempt.value] as const,
    async ([, state, cursors], _, onCleanup) => {
      let active = true
      onCleanup(() => { active = false })
      error.value = ''
      loading.value = state === 'ready'
      if (!loading.value) return
      const moved = page.value && cursors.at(-1) !== confirmed.value.at(-1)
      try {
        const result = await read(cursors.at(-1)!, page.value)
        if (!active) return
        if (result.nextAfter !== null && cursors.includes(result.nextAfter)) throw new ContractFailure()
        partial.value = !result.complete
        if (result.complete || !page.value?.complete) {
          page.value = result
          confirmed.value = cursors
        }
      } catch (reason) {
        if (!active) return
        partial.value = false
        error.value = explain(reason)
        onFailure(reason)
      } finally {
        if (active) {
          loading.value = false
          if (moved) {
            await nextTick()
            if (active) (error.value || partial.value ? retry.value : heading.value)?.focus()
          }
        }
      }
    }, { immediate: true })
  return { page, confirmed, requested, attempt, loading, partial, error, heading, retry }
}
