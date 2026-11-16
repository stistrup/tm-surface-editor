<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useHistoryStore } from '../stores/history'
import { usePreviewStore } from '../stores/preview'
import ConfirmDialog from './ConfirmDialog.vue'

// the open map's settings next to its title: unsaved changes, save, discard, back to the original map
const preview = usePreviewStore()
const history = useHistoryStore()

const asking = ref<'save' | 'discard' | 'original' | null>(null)
const shown = (which: 'save' | 'discard' | 'original') =>
  computed({ get: () => asking.value === which, set: (on) => (asking.value = on ? which : null) })
const saveOpen = shown('save')
const discardOpen = shown('discard')
const originalOpen = shown('original')

const targets = computed(() => Object.keys(preview.working.pieces ?? {}).length)
const anything = computed(() => Object.keys(preview.working).length > 0)

const toOriginal = () => history.change(() => (preview.working = {}))

// discard and revert under the dots
const more = ref(false)
const moreBox = ref<HTMLElement>()
const outside = (event: Event) => {
  if (!moreBox.value?.contains(event.target as Node)) more.value = false
}
const listen = (on: boolean) => window[on ? 'addEventListener' : 'removeEventListener']('pointerdown', outside)
watch(more, listen)
onBeforeUnmount(() => listen(false))
const ask = (which: 'discard' | 'original') => {
  more.value = false
  asking.value = which
}
</script>

<template>
  <div v-if="preview.state && (preview.dirty || anything)" :class="$style.actions">
    <span v-if="preview.dirty" :class="$style.unsaved">Unsaved changes *</span>
    <button v-if="preview.dirty" type="button" :class="$style.action" @click="asking = 'save'">Save</button>
    <div ref="moreBox" :class="$style.more">
      <button type="button" :class="$style.dots" title="More" aria-label="More" :aria-expanded="more" @click="more = !more">
        <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
          <circle cx="3.5" cy="8" r="1.3" fill="currentColor" />
          <circle cx="8" cy="8" r="1.3" fill="currentColor" />
          <circle cx="12.5" cy="8" r="1.3" fill="currentColor" />
        </svg>
      </button>
      <ul v-if="more" :class="$style.menu" role="menu">
        <li v-if="preview.dirty" :class="$style.row" role="menuitem" @click="ask('discard')">Discard changes</li>
        <li v-if="anything" :class="$style.row" role="menuitem" @click="ask('original')">Revert to original</li>
      </ul>
    </div>
  </div>

  <ConfirmDialog v-model="saveOpen" title="Save" confirm="Save" @confirmed="preview.save()">
    <p>Save this map's settings and conversion targets.</p>
  </ConfirmDialog>
  <ConfirmDialog v-model="discardOpen" title="Discard changes" confirm="Discard" @confirmed="history.revert()">
    <p>Go back to this map's last save.</p>
    <p>You can still undo (Ctrl + Z).</p>
  </ConfirmDialog>
  <ConfirmDialog v-model="originalOpen" title="Revert to original" confirm="Revert" @confirmed="toOriginal()">
    <p>Take away every setting and conversion target{{ targets ? ` (${targets})` : '' }}.</p>
    <p>Not saved until you save.</p>
  </ConfirmDialog>
</template>

<style module>
.actions {
  display: flex;
  gap: 2px;
  align-items: center;
  height: 24px;
  padding: 0 6px;
  background: var(--header);
  border-right: 1px solid var(--line);
}

.unsaved {
  padding: 0 6px;
  color: var(--accent);
  white-space: nowrap;
}

.action {
  height: 18px;
  padding: 0 6px;
  color: var(--text);
  white-space: nowrap;
  cursor: default;
  background: none;
  border: 0;
  border-radius: 2px;
}

.action:hover {
  color: var(--strong);
  background: var(--line);
}

.more {
  position: relative;
}

.dots {
  composes: action;
  display: grid;
  place-items: center;
  padding: 0 3px;
}

.menu {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 10;
  min-width: 150px;
  margin: 0;
  padding: 2px 0;
  list-style: none;
  background: var(--field);
  border: 1px solid var(--line);
  border-radius: 2px;
  box-shadow: 0 4px 12px rgb(0 0 0 / 40%);
}

.row {
  min-height: 20px;
  padding: 0 10px;
  line-height: 20px;
  white-space: nowrap;
  cursor: default;
}

.row:hover {
  background: var(--hover);
}
</style>
