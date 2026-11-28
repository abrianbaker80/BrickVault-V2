<script setup lang="ts">
import type { Presence } from './setContents'
const box = defineModel<Presence>('box', {required:true})
const instructions = defineModel<Presence>('instructions', {required:true})
</script>
<template>
  <label>Original box<select v-model="box" required>
    <option value="" disabled>Choose after inspection</option><option value="yes">Included</option><option value="no">Absent</option>
  </select></label>
  <label>Printed instructions<select v-model="instructions" required>
    <option value="" disabled>Choose after inspection</option><option value="yes">Included</option><option value="no">Absent</option>
  </select></label>
</template>
