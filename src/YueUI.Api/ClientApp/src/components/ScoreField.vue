<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Checkbox from 'primevue/checkbox'
import Panel from 'primevue/panel'
import { exampleScore, planningFor, type FormState } from '../form'
import { abcKey, keyChoices, keyParts, transposeAbc, transposeToKey, vocalRange } from '../abcTranspose'
import { midiToAbc } from '../api'
import { t } from '../i18n'
import { pickMidiFile } from '../midi'
import FieldHelp from './FieldHelp.vue'
import TextActions from './TextActions.vue'

/**
 * The song's own score, between the song's fields and the advanced parameters: a score decides melody, chords and
 * form, so it was too deep inside the parameters. Collapsed while empty, since most songs have none; the example and
 * the MIDI import stay in the header so they need no extra tap. The checkbox there sends the score or keeps it unsent.
 */
const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()
const form = defineModel<FormState>({ required: true })
const fieldErrors = defineModel<Record<string, string[]>>('errors', { required: true })

const hasScore = computed(() => form.value.abc.trim() !== '')
const collapsed = ref(!hasScore.value)
// A score from the library, a transcription or a MIDI file should be seen where it landed.
watch(hasScore, (has) => {
  if (has) {
    collapsed.value = false
  }
})
watch(fieldErrors, (errors) => {
  if (errors.abc) {
    collapsed.value = false
  }
})

// The API refuses this too; saying so before sending saves a round trip from the phone.
const scoreWithoutPlanning = computed(
  () => hasScore.value && form.value.useAbc && form.value.cot === 'off' && !form.value.instrumental,
)

const scoreKey = computed(() => abcKey(form.value.abc))
const keyOptions = computed(() =>
  keyChoices().map((key) => {
    const { root, minor } = keyParts(key)
    return { value: key, label: t(minor ? 'keyMinor' : 'keyMajor', { root }) }
  }),
)
const scoreRange = computed(() => {
  const range = vocalRange(form.value.abc)
  return range ? t('abcVocalRange', range) : null
})

/** YuE2 sings the score's "Vocal" voice at its written pitch, so this is how a song gets lower or higher. */
function transpose(semitones: number): void {
  form.value = { ...form.value, abc: transposeAbc(form.value.abc, semitones).abc }
}

function changeKey(key: string): void {
  form.value = { ...form.value, abc: transposeToKey(form.value.abc, key).abc }
}

const importingMidi = ref(false)

/** A MIDI file, e.g. a song built in Logic, as the score; planning follows it like a song's own score. */
function useMidi(): void {
  pickMidiFile(async (file) => {
    importingMidi.value = true
    try {
      const midi = await midiToAbc(file)
      form.value = { ...form.value, abc: midi.abc, cot: planningFor(midi.abc), useAbc: true }
      if (midi.warnings.length > 0) {
        emit('notice', t('midiWarnings', { messages: midi.warnings.join(' ') }))
      }
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    } finally {
      importingMidi.value = false
    }
  })
}
</script>

<template>
  <Panel v-model:collapsed="collapsed" toggleable>
    <template #header>
      {{ t('abc') }}
    </template>
    <template #icons>
      <Checkbox
        v-if="hasScore"
        v-model="form.useAbc"
        binary
        class="mr-2"
        :aria-label="t('abcUse')"
        v-tooltip="t('abcUse')"
      />
      <Button
        v-if="!hasScore"
        v-tooltip="t('abcExample')"
        :aria-label="t('abcExample')"
        icon="pi pi-upload"
        severity="info"
        text
        rounded
        @click="form.abc = exampleScore"
      />
      <Button
        v-tooltip="t('abcFromMidi')"
        :aria-label="t('abcFromMidi')"
        icon="pi pi-file-arrow-up"
        text
        rounded
        :loading="importingMidi"
        :disabled="importingMidi"
        @click="useMidi"
      />
    </template>

    <small v-if="hasScore && !form.useAbc" class="muted block mb-2">{{ t('abcUnused') }}</small>
    <Textarea
      fluid
      id="gen-abc"
      v-model="form.abc"
      class="mono"
      :class="{ 'opacity-60': hasScore && !form.useAbc }"
      :rows="8"
      spellcheck="false"
      autocapitalize="off"
      autocomplete="off"
      :placeholder="t('abcPlaceholder')"
      :aria-label="t('abc')"
      aria-describedby="gen-abc-help"
    />
    <FieldHelp id="gen-abc-help" :hint="t('abcHint')" :more="t('abcMore')" />
    <div class="flex flex-wrap items-center gap-1">
      <template v-if="hasScore">
        <Button
          v-tooltip="t('abcTransposeDown')"
          :aria-label="t('abcTransposeDown')"
          icon="pi pi-minus"
          size="small"
          text
          rounded
          @click="transpose(-1)"
        />
        <Button
          v-tooltip="t('abcTransposeUp')"
          :aria-label="t('abcTransposeUp')"
          icon="pi pi-plus"
          size="small"
          text
          rounded
          @click="transpose(1)"
        />
        <Select
          v-if="scoreKey"
          :model-value="scoreKey"
          :options="keyOptions"
          option-label="label"
          option-value="value"
          size="small"
          :aria-label="t('abcKeyLabel')"
          v-tooltip="t('abcKeyLabel')"
          @update:model-value="changeKey"
        />
        <small v-if="scoreRange" class="muted ml-1">{{ scoreRange }}</small>
      </template>
      <TextActions v-model="form.abc" class="ml-auto" />
    </div>
    <small v-if="scoreWithoutPlanning" class="danger">{{ t('abcNeedsPlanning') }}</small>
    <small v-else-if="fieldErrors.abc" class="danger">{{ fieldErrors.abc.join(' ') }}</small>
  </Panel>
</template>
