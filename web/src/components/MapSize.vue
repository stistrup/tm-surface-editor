<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useMapStore } from '../stores/map'
import { usePreviewStore } from '../stores/preview'
import FloatingPanel from './FloatingPanel.vue'
import { useHoverFocus } from '../hoverFocus'
import FocusEye from './FocusEye.vue'

// the converted map's estimated size beside its title; the pieces it embeds, largest first
const store = useMapStore()
const preview = usePreviewStore()

const open = ref(false)
const button = ref<HTMLElement>()

const size = computed(() => (preview.showOriginal ? null : preview.result?.size ?? null))
const over = computed(() => !!size.value && size.value.bytes > size.value.limit)
const MAX_NAME = 40
const short = (name: string) => (name.length > MAX_NAME ? `${name.slice(0, MAX_NAME - 1)}…` : name)
const mb = (bytes: number) => `${(bytes / 1048576).toFixed(1)} MB`

// first placement of each piece, as a scene index
const pieces = computed(() => {
  const at = new Map<string, number>()
  store.infos.forEach((info, i) => {
    if (typeof info.block === 'number') at.set(`b${info.block}`, i)
    else if (typeof info.item === 'number') at.set(`i${info.item}`, i)
  })
  return (size.value?.pieces ?? []).map((p) => ({
    ...p,
    count: p.blocks.length + p.items.length,
    index: at.get(p.blocks.length ? `b${p.blocks[0]}` : `i${p.items[0]}`) ?? null,
  }))
})

const { enter, leave, keep } = useHoverFocus()

watch(open, (now) => now || leave())
watch(size, (now) => now || (open.value = false))
</script>

<template>
  <button
    v-if="size"
    ref="button"
    type="button"
    :class="[$style.size, { [$style.over]: over }]"
    :title="over ? `Over the ${mb(size.limit)} room limit` : 'Estimated size'"
    :aria-expanded="open"
    @click="open = !open"
  >
    <span>{{ mb(size.bytes) }}{{ size.unbuilt ? '+' : '' }}</span>
    <span :class="[$style.chevron, { [$style.up]: open }]" />
  </button>
  <FloatingPanel v-if="size" v-model="open" :anchor="button" :title="`About ${mb(size.bytes)}`">
    <li :class="$style.note">Size is an estimate</li>
    <li v-for="piece in pieces" :key="piece.name" :title="piece.name" @click="keep(piece.index)">
      <span :class="$style.grow">{{ short(piece.name) }}</span>
      <span v-if="piece.count > 1" :class="$style.muted">×{{ piece.count }}</span>
      <span :class="$style.bytes">{{ piece.bytes < 0 ? 'building' : mb(piece.bytes) }}</span>
      <FocusEye v-if="piece.index !== null" @enter="enter(piece.index)" @leave="leave()" @keep="keep(piece.index)" />
    </li>
  </FloatingPanel>
</template>

<style module>
.size {
  display: flex;
  gap: 6px;
  align-items: center;
  height: 24px;
  padding: 0 8px;
  color: var(--muted);
  white-space: nowrap;
  cursor: default;
  background: var(--header);
  border: 0;
  border-right: 1px solid var(--line);
}

.size:hover {
  color: var(--text);
}

.over,
.over:hover {
  color: var(--accent);
}

.note {
  color: var(--muted);
  pointer-events: none;
}

.muted {
  flex: none;
  color: var(--muted);
}

.bytes {
  flex: none;
  min-width: 52px;
  color: var(--muted);
  text-align: right;
}

.grow {
  composes: grow from '../ui.module.css';
}

.chevron {
  composes: chevron from '../ui.module.css';
}

.up {
  composes: up from '../ui.module.css';
}
</style>
