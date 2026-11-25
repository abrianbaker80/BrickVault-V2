<script setup lang="ts">
import type { RecountTarget } from './api'
defineProps<{ target: RecountTarget }>()
</script>
<template>
  <strong>{{ target.itemName || 'Unrecognized position' }}</strong>
  <p v-if="target.purchaseId">{{ target.locationCode }} · {{ target.purchaseReference }} · {{ target.deliveryReference }}</p>
  <small v-if="target.purchaseRevision !== null">{{ target.quantity }} units at preview · Purchase revision {{ target.purchaseRevision }} · Catalog {{ target.catalogRevision }} · Bin {{ target.locationRevision }}</small>
  <small>Position {{ target.positionId }}</small>
</template>
<style scoped>
p, small { margin: 3px 0; overflow-wrap: anywhere }
small { display: block; color: var(--muted) }
</style>
