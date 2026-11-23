<script setup lang="ts">
import { partImage } from '../art'
import { onHand, sellable, stockStatus } from './model'
import type { Lot } from './model'
defineProps<{ lot: Lot }>()
defineEmits<{ close: [] }>()
</script>

<template>
  <div class="iv-panel-title">
    <h2>Lot inspector</h2>
    <button class="iv-icon-button" aria-label="Close inspector" @click="$emit('close')">×</button>
  </div>
  <div class="iv-detail-identity">
    <div class="iv-display-art"><img :src="partImage(lot.art)" alt="" /></div>
    <div>
      <span class="iv-code">{{ lot.id }}</span>
      <h3>{{ lot.name }}</h3>
      <p>{{ lot.color }} · {{ lot.condition }}</p>
      <span class="iv-badge" :class="stockStatus(lot)[1]"
        ><i aria-hidden="true" />{{ stockStatus(lot)[0] }}</span
      >
    </div>
  </div>
  <div class="iv-detail-section">
    <h4>Stock position</h4>
    <dl class="iv-pairs">
      <dt>Physical on hand</dt>
      <dd>{{ onHand(lot) }}</dd>
      <dt>Reserved</dt>
      <dd>{{ lot.reserved }}</dd>
      <dt>Unavailable</dt>
      <dd>{{ lot.unavailable }}</dd>
      <dt class="iv-em">Sellable</dt>
      <dd class="iv-em">{{ sellable(lot) }}</dd>
    </dl>
  </div>
  <div class="iv-detail-section">
    <h4>Storage locations</h4>
    <ul class="iv-placements">
      <li v-for="p in lot.placements" :key="p.bin">
        <span class="iv-bin">{{ p.bin }}</span
        ><span>{{ p.units }} units</span>
      </li>
    </ul>
  </div>
  <div class="iv-detail-section">
    <h4>Marketplace</h4>
    <dl class="iv-pairs">
      <dt>Desired quantity</dt>
      <dd>{{ lot.desired }}</dd>
      <dt>Desired at</dt>
      <dd>{{ lot.desiredAt }}</dd>
      <dt>Last observed</dt>
      <dd>{{ lot.observed?.units ?? 'Unknown' }}</dd>
      <dt>Observed at</dt>
      <dd>
        {{ lot.observed ? lot.observed.asOf + (lot.observed.stale ? ' · stale' : '') : 'No observation' }}
      </dd>
    </dl>
  </div>
  <div class="iv-detail-section">
    <h4>Provenance</h4>
    <ul class="iv-sources">
      <li v-for="source in lot.sources" :key="source.id">
        {{ source.id }} · {{ source.name }} · {{ source.units }} units
      </li>
    </ul>
    <p>Custody · {{ lot.custodian }}</p>
    <small>Revision {{ lot.revision }} · {{ lot.type }} {{ lot.part }}</small>
  </div>
</template>
