<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import { addVersion, listVoices } from '../api'
import { t } from '../i18n'
import type { ReferenceVoice, RunInfo, SongInfo } from '../types'
import { machineField, storedMachine } from '../machines'
import { octaveOptions, stepOptions, strengthOptions } from '../voiceChoices'
import MachinePicker from './MachinePicker.vue'

/** Asks for a version of a song sung with a reference voice. One for the whole library, shown while `target` is set. */
const target = defineModel<{ run: RunInfo; song: SongInfo } | null>({ required: true })

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

const voiceList = ref<ReferenceVoice[]>([])
const voicesLoading = ref(false)
// Kept between songs, so a second version with the same voice needs no choosing.
const voiceChoice = ref<string | null>(null)
const octave = ref(0)
const strength = ref(0.7)
const steps = ref(50)
const keepReverb = ref(true)
const queueing = ref(false)
/** The Mac that separates and converts; the mix is made here either way. */
const machine = storedMachine('voices')

const octaves = computed(octaveOptions)
const strengths = computed(strengthOptions)
const stepChoices = computed(stepOptions)

function failed(caught: unknown): void {
  emit('error', caught instanceof Error ? caught.message : String(caught))
}

// The voices are read whenever the dialog opens, since one may have been recorded on the voices page meanwhile.
watch(target, async (opened, before) => {
  if (!opened || before) {
    return
  }
  voicesLoading.value = true
  try {
    voiceList.value = await listVoices()
    if (!voiceList.value.some((v) => v.id === voiceChoice.value)) {
      voiceChoice.value = voiceList.value[0]?.id ?? null
    }
  } catch (caught) {
    target.value = null
    failed(caught)
  } finally {
    voicesLoading.value = false
  }
})

async function sing(): Promise<void> {
  const song = target.value
  if (!song || !voiceChoice.value) {
    return
  }
  queueing.value = true
  try {
    const version = await addVersion(song.song.id, {
      voiceId: voiceChoice.value,
      semiToneShift: octave.value,
      strength: strength.value,
      diffusionSteps: steps.value,
      keepReverb: keepReverb.value,
      machine: machineField(machine.value),
    })
    target.value = null
    emit('notice', t('versionQueued', { title: song.run.title || t('untitled'), voice: version.voiceLabel }))
  } catch (caught) {
    failed(caught)
  } finally {
    queueing.value = false
  }
}
</script>

<template>
  <Dialog
    :visible="target !== null"
    modal
    :header="t('singWithVoice')"
    :draggable="false"
    :style="{ width: 'min(30rem, calc(100vw - 2rem))' }"
    @update:visible="(open: boolean) => !open && (target = null)"
  >
    <form v-if="target" class="flex flex-col gap-4" @submit.prevent="sing">
      <p class="muted m-0 text-sm">
        {{ t('singIntro', { title: target.run.title || t('untitled'), song: t('songN', { n: target.song.index }) }) }}
      </p>
      <p v-if="!voicesLoading && voiceList.length === 0" class="m-0">{{ t('singNoVoices') }}</p>
      <div v-else class="flex flex-col gap-1">
        <label for="sing-voice" class="muted text-sm">{{ t('voice') }}</label>
        <Select
          v-model="voiceChoice"
          input-id="sing-voice"
          :options="voiceList"
          option-label="label"
          option-value="id"
          :loading="voicesLoading"
          fluid
        />
      </div>
      <div class="flex flex-col gap-1">
        <span class="muted text-sm">{{ t('octave') }}</span>
        <SelectButton
          v-model="octave"
          :options="octaves"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('octave')"
        />
      </div>
      <div class="flex flex-col gap-1">
        <span class="muted text-sm">{{ t('strength') }}</span>
        <SelectButton
          v-model="strength"
          :options="strengths"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('strength')"
        />
      </div>
      <div class="flex flex-col gap-1">
        <span class="muted text-sm">{{ t('steps') }}</span>
        <SelectButton
          v-model="steps"
          :options="stepChoices"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('steps')"
        />
      </div>
      <div class="flex items-center gap-2">
        <Checkbox v-model="keepReverb" input-id="sing-reverb" binary />
        <label for="sing-reverb">{{ t('keepReverb') }}</label>
      </div>
      <p class="muted m-0 text-sm">{{ t('singHint') }}</p>
      <div class="flex flex-wrap items-center justify-end gap-2">
        <MachinePicker v-model="machine" engine="voices" :disabled="queueing" class="mr-auto" />
        <Button type="button" :label="t('cancel')" severity="secondary" text @click="target = null" />
        <Button type="submit" :label="t('sing')" :loading="queueing" :disabled="!voiceChoice || voicesLoading" />
      </div>
    </form>
  </Dialog>
</template>
