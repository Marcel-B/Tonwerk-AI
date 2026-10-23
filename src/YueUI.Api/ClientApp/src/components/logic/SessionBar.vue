<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { StoredSession } from '../../logic/api'
import { t } from '../../logic/i18n'
import { locale } from '../../i18n'

/**
 * The Logic page's sessions: the list to open one again, and the open one's name and delete button. Saving needs no
 * button, the page saves as it changes.
 */
const props = defineProps<{
  sessions: StoredSession[]
  /** The session the page saves into, null while it has none. */
  current: StoredSession | null
  error: string | null
  /** Whether the page shows a song or file at all; without one there is nothing to save. */
  canSave: boolean
}>()

const emit = defineEmits<{
  open: [id: number]
  rename: [name: string]
  remove: []
}>()

const options = computed(() =>
  props.sessions.map((session) => ({
    label: `${session.name} · ${new Date(session.updatedAt).toLocaleString(locale.value, { dateStyle: 'short', timeStyle: 'short' })}`,
    value: session.id,
  })),
)

const name = ref(props.current?.name ?? '')
watch(
  () => props.current?.name,
  (value) => (name.value = value ?? ''),
)

function rename(): void {
  const trimmed = name.value.trim()
  if (trimmed) {
    emit('rename', trimmed)
  } else {
    name.value = props.current?.name ?? ''
  }
}
</script>

<template>
  <div class="flex flex-col gap-2">
    <label for="logic-session" class="text-sm text-muted-color">{{ t('session') }}</label>
    <div class="flex flex-wrap items-center gap-2">
      <Select
        :model-value="current?.id ?? null"
        input-id="logic-session"
        :options="options"
        option-label="label"
        option-value="value"
        :placeholder="t('sessionPick')"
        :empty-message="t('sessionNone')"
        :filter="sessions.length > 8"
        class="flex-[1_1_14rem] min-w-0"
        @update:model-value="(id: number | null) => id !== null && emit('open', id)"
      />
      <template v-if="current">
        <InputText
          v-model="name"
          :aria-label="t('sessionName')"
          spellcheck="false"
          class="flex-[1_1_10rem] min-w-0"
          @change="rename"
          @keydown.enter="rename"
        />
        <Button
          icon="pi pi-trash"
          severity="secondary"
          text
          rounded
          :aria-label="t('sessionDelete')"
          v-tooltip.bottom="t('sessionDelete')"
          @click="emit('remove')"
        />
      </template>
    </div>
    <p v-if="error" class="hint danger mt-0" role="alert">{{ error }}</p>
    <p v-else-if="current" class="hint muted mt-0">{{ t('sessionSaved') }}</p>
    <p v-else-if="canSave" class="hint muted mt-0">{{ t('sessionWillSave') }}</p>
  </div>
</template>
