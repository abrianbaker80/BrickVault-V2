<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { label } from './presentation'
import type { CatalogInput, LocationInput } from './api'

const props = defineProps<{ kind: 'catalog' | 'location'; enabled: boolean; saving: boolean; error: string }>()
const emit = defineEmits<{
  close: []
  save: [input: { kind: 'catalog'; fields: CatalogInput } | { kind: 'location'; fields: LocationInput }]
}>()
const dialog = ref<HTMLDialogElement>()
onMounted(() => dialog.value?.showModal())
const name = ref('')
const code = ref('')
const itemNumber = ref('')
const variant = ref('')
const source = ref('')
const itemType = ref<CatalogInput['itemType']>('part')
const colorState = ref<CatalogInput['colorState']>('unknown')
const colorId = ref<number | ''>('')
const condition = ref<CatalogInput['condition']>('unknown')
const completeness = ref<CatalogInput['completeness']>('unknown')
const license = ref<CatalogInput['license']>('unknown')
const mapping = ref<CatalogInput['mapping']>('unmapped')
function submit() {
  if (!props.enabled) return
  if (props.kind === 'location') {
    emit('save', { kind: 'location', fields: { code: code.value.trim(), name: name.value.trim() } })
    return
  }
  emit('save', { kind: 'catalog', fields: {
    name: name.value.trim(), itemNumber: itemNumber.value.trim(), variant: variant.value.trim() || null,
    source: source.value.trim(), itemType: itemType.value, colorState: colorState.value,
    colorId: colorState.value === 'known' && typeof colorId.value === 'number' ? colorId.value : null,
    condition: condition.value, completeness: completeness.value, mapping: mapping.value, license: license.value
  } })
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="create-title" @cancel.prevent="emit('close')">
    <header class="panel-head">
      <h2 id="create-title">{{ kind === 'catalog' ? 'New catalog item' : 'New location' }}</h2>
      <button class="close" aria-label="Close form" @click="emit('close')">×</button>
    </header>
    <form class="form-body" @submit.prevent="submit">
      <div v-if="kind === 'location'" class="fields">
        <label>Location code<input v-model="code" required maxlength="48" pattern="[A-Za-z0-9][A-Za-z0-9._\-]*" title="Use letters, numbers, periods, hyphens or underscores. Begin with a letter or number." autofocus spellcheck="false" /></label>
        <label>Name<input v-model="name" required pattern=".*\S.*" maxlength="160" /></label>
      </div>
      <template v-else>
        <fieldset class="fields">
          <legend>Item identity</legend>
          <label>Item number<input v-model="itemNumber" required pattern=".*\S.*" maxlength="64" autofocus spellcheck="false" /></label>
          <label>Item type<select v-model="itemType"><option v-for="v in ['part','set','minifigure','book','gear','instruction','original-box','catalog']" :key="v" :value="v">{{ label(v) }}</option></select></label>
          <label>Name<input v-model="name" required pattern=".*\S.*" maxlength="160" /></label>
          <label>Variant (optional)<input v-model="variant" maxlength="64" /></label>
          <label>Color state<select v-model="colorState"><option v-for="v in ['unknown','known','not-applicable']" :key="v" :value="v">{{ label(v) }}</option></select></label>
          <label v-if="colorState === 'known'">Color ID<input v-model.number="colorId" required type="number" min="0" max="2147483647" step="1" /></label>
          <label>Condition<select v-model="condition"><option v-for="v in ['unknown','new','used']" :key="v" :value="v">{{ label(v) }}</option></select></label>
          <label>Completeness<select v-model="completeness"><option v-for="v in ['unknown','complete','incomplete','not-applicable']" :key="v" :value="v">{{ label(v) }}</option></select></label>
        </fieldset>
        <fieldset class="fields">
          <legend>Source and mapping</legend>
          <label class="span">Catalog source<input v-model="source" required pattern=".*\S.*" maxlength="80" /></label>
          <label>Source license<select v-model="license"><option v-for="v in ['unknown','operator-owned','permitted','restricted']" :key="v" :value="v">{{ label(v) }}</option></select></label>
          <label>Catalog mapping<select v-model="mapping"><option v-for="v in ['unmapped','ambiguous','confirmed']" :key="v" :value="v">{{ label(v) }}</option></select></label>
        </fieldset>
      </template>
      <p v-if="error" role="alert" class="warning">{{ error }}</p>
      <footer class="actions">
        <button type="button" @click="emit('close')">Cancel</button>
        <button class="primary" type="submit" :disabled="!enabled">{{ saving ? 'Saving…' : kind === 'catalog' ? 'Save catalog item' : 'Save location' }}</button>
      </footer>
    </form>
  </dialog>
</template>
