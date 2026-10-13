<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Checkbox from 'primevue/checkbox'
import Panel from 'primevue/panel'
import { listLoras } from '../api'
import {
  advancedChanged,
  defaultSampling,
  lengthChoices,
  maxLoraStrength,
  maxSongSeconds,
  resetAdvanced,
  usesScore,
  type FormState,
  type SamplingPhase,
} from '../form'
import { formatDuration, t } from '../i18n'
import type { LoraList } from '../types'
import FieldHelp from './FieldHelp.vue'
import NumberField from './NumberField.vue'
import SamplingFields from './SamplingFields.vue'

/**
 * The parameters beside the song's own fields, in a column of their own on wide screens. They edit the same form as
 * GenerateForm and show the field errors of its last submission. The score has a block of its own (ScoreField), the
 * seed sits by the song count. The checkbox in the header sends them or the defaults, keeping the values either way.
 */
defineProps<{
  /** Whether the worker takes the extension's fields; null while unknown (no worker has started yet). */
  extensions: boolean | null
}>()
const form = defineModel<FormState>({ required: true })
const fieldErrors = defineModel<Record<string, string[]>>('errors', { required: true })

const changed = computed(() => advancedChanged(form.value))

/** The request fields shown here; the sampling ones come as `abcSampling.temperature` and so on. */
const ownFields = new Set(['cot', 'draftSteps', 'fullSteps', 'engines', 'maxTokens', 'lora', 'loraStrength'])
const collapsed = ref(true)
// A refusal about one of these fields would go unseen while the panel is shut.
watch(fieldErrors, (errors) => {
  if (Object.keys(errors).some((key) => ownFields.has(key) || /^(abc|semantic)Sampling\./.test(key))) {
    collapsed.value = false
  }
})
// The worker writes a score unless planning is off (an instrumental turns it back on) or one is supplied.
const plansScore = computed(() => (form.value.cot !== 'off' || form.value.instrumental) && !usesScore(form.value))
// One field for the steps of the chosen quality; each keeps its own value.
const steps = computed({
  get: () => (form.value.quality === 'draft' ? form.value.draftSteps : form.value.fullSteps),
  set: (value: number) => {
    if (form.value.quality === 'draft') {
      form.value.draftSteps = value
    } else {
      form.value.fullSteps = value
    }
  },
})

/** Parameters only: a score someone pasted or transcribed has its own button. */
function resetParameters(): void {
  form.value = resetAdvanced(form.value)
  fieldErrors.value = {}
}

function samplingChanged(phase: SamplingPhase): boolean {
  return JSON.stringify(form.value[phase]) !== JSON.stringify(defaultSampling[phase])
}

function resetSampling(phase: SamplingPhase): void {
  form.value = { ...form.value, [phase]: { ...defaultSampling[phase] } }
}

function lengthLabel(seconds: number): string {
  const duration = formatDuration(seconds)
  return seconds === maxSongSeconds ? t('defaultValue', { value: duration }) : duration
}

const cotItems = [
  { value: 'full', label: t('defaultValue', { value: t('cotFull') }) },
  { value: 'melody', label: t('cotMelody') },
  { value: 'off', label: t('cotOff') },
]
const engineOptions = [
  { value: '', label: t('defaultValue', { value: t('enginesAuto') }) },
  { value: 'gpu', label: t('enginesGpu') },
  { value: 'gpu+ane', label: t('enginesAne') },
]
const lengthOptions = lengthChoices.map((x) => ({ value: x, label: lengthLabel(x) }))

// Read when the page opens and each time the panel opens, so a LoRA trained meanwhile shows up.
const loras = ref<LoraList | null>(null)
async function loadLoras(): Promise<void> {
  try {
    loras.value = await listLoras()
  } catch {
    loras.value = null
  }
}
onMounted(loadLoras)
watch(collapsed, (shut) => {
  if (!shut) {
    void loadLoras()
  }
})
const chosenLora = computed(() => loras.value?.loras.find((l) => l.name === form.value.lora) ?? null)
const loraOptions = computed(() => {
  const options = [{ value: '', label: t('defaultValue', { value: t('loraNone') }) }]
  for (const lora of loras.value?.loras ?? []) {
    options.push({ value: lora.name, label: lora.name })
  }
  // A LoRA taken over from a song, or deleted since it was chosen, stays visible, so the form does not hide it.
  if (form.value.lora !== '' && loras.value !== null && chosenLora.value === null) {
    options.push({ value: form.value.lora, label: t('loraMissing', { name: form.value.lora }) })
  }
  return options
})
const loraHint = computed(() => {
  const lora = chosenLora.value
  if (lora === null) {
    return loras.value !== null && loras.value.loras.length === 0 ? t('loraNoneYet') : t('loraHint')
  }
  const parts = [lora.triggerWord ? t('loraTrigger', { word: lora.triggerWord }) : null]
  if (lora.songs !== null && lora.minutes !== null) {
    parts.push(t('loraTrained', { songs: lora.songs, minutes: Math.round(lora.minutes), steps: lora.steps ?? '?' }))
  }
  return parts.filter(Boolean).join(' ') || t('loraHint')
})
</script>

<template>
  <!-- Collapsed at first: a normal song needs none of this, and the queue sits right below. -->
  <Panel v-model:collapsed="collapsed" toggleable>
    <template #header>
      <div class="flex gap-4">
        {{ t('advanced') }}
      </div>
    </template>
    <template #icons>
      <Tag v-if="!form.useAdvanced" severity="secondary">{{ t('advancedOff') }}</Tag>
      <Tag v-else-if="changed" severity="warn">{{ t('advancedChanged') }}</Tag>
      <Checkbox
        v-model="form.useAdvanced"
        binary
        class="ml-2 mr-2"
        :aria-label="t('advancedUse')"
        v-tooltip="t('advancedUse')"
      />
    </template>
    <small v-if="!form.useAdvanced" class="muted block mb-2">{{ t('advancedUnused') }}</small>
    <div :class="{ 'opacity-60': !form.useAdvanced }">
      <div>
        <small class="muted">{{ t('advancedIntro') }}</small>
        <div class="flex justify-end">
          <Button
            icon="pi pi-refresh"
            :label="changed ? t('advancedReset') : t('advancedAtDefaults')"
            size="small"
            severity="warn"
            :disabled="!changed"
            @click="resetParameters"
          />
        </div>
      </div>
      <p v-if="extensions === false" class="notice" role="note">{{ t('extensionsOff') }}</p>

      <div class="grid grid-cols-2 gap-3">
        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              id="gen-cot"
              input-class="w-full"
              class="w-full"
              v-model="form.cot"
              :options="cotItems"
              option-label="label"
              option-value="value"
              aria-describedby="gen-cot-help"
            />
            <label for="gen-cot">{{ t('cot') }}</label>
          </FloatLabel>
          <small v-if="fieldErrors.cot" class="danger">{{ fieldErrors.cot.join(' ') }}</small>
          <FieldHelp id="gen-cot-help" :hint="t('cotHint')" :more="t('cotMore')" />
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6">
            <NumberField
              id="gen-steps"
              v-model="steps"
              :min="1"
              :max="form.quality === 'draft' ? 32 : 64"
              aria-describedby="gen-steps-help"
            />
            <label for="gen-steps">{{ form.quality === 'draft' ? t('stepsDraft') : t('stepsFull') }}</label>
          </FloatLabel>
          <FieldHelp
            id="gen-steps-help"
            :hint="form.quality === 'draft' ? t('draftStepsHint') : t('fullStepsHint')"
            :more="t('stepsMore')"
          />
          <small v-if="fieldErrors.draftSteps || fieldErrors.fullSteps" class="danger">
            {{ (fieldErrors.draftSteps ?? fieldErrors.fullSteps)!.join(' ') }}
          </small>
        </div>
        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              id="gen-engines"
              v-model="form.engines"
              class="w-full"
              :options="engineOptions"
              option-value="value"
              option-label="label"
              aria-describedby="gen-engines-help"
            />
            <label for="gen-engines">{{ t('engines') }}</label>
          </FloatLabel>
          <FieldHelp id="gen-engines-help" :hint="t('enginesHint')" :more="t('enginesMore')" />
          <small v-if="fieldErrors.engines" class="danger">{{ fieldErrors.engines.join(' ') }}</small>
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              option-label="label"
              option-value="value"
              class="w-full"
              id="gen-length"
              :options="lengthOptions"
              v-model.number="form.maxSeconds"
              aria-describedby="gen-length-help"
            />

            <label for="gen-length">{{ t('maxLength') }}</label>
          </FloatLabel>
          <FieldHelp id="gen-length-help" :hint="t('maxLengthHint')" :more="t('maxLengthMore')" />
          <small v-if="fieldErrors.maxTokens" class="danger">{{ fieldErrors.maxTokens.join(' ') }}</small>
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              id="gen-lora"
              v-model="form.lora"
              class="w-full"
              :options="loraOptions"
              option-value="value"
              option-label="label"
              aria-describedby="gen-lora-help"
            />
            <label for="gen-lora">{{ t('lora') }}</label>
          </FloatLabel>
          <FieldHelp id="gen-lora-help" :hint="loraHint" :more="t('loraMore', { folder: loras?.folder ?? 'loras/' })" />
          <small v-if="fieldErrors.lora" class="danger">{{ fieldErrors.lora.join(' ') }}</small>
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6">
            <NumberField
              id="gen-lora-strength"
              v-model="form.loraStrength"
              :min="0"
              :max="maxLoraStrength"
              :fraction-digits="2"
              :disabled="form.lora === ''"
              aria-describedby="gen-lora-strength-help"
            />
            <label for="gen-lora-strength">{{ t('loraStrength') }}</label>
          </FloatLabel>
          <FieldHelp id="gen-lora-strength-help" :hint="t('loraStrengthHint')" :more="t('loraStrengthMore')" />
          <small v-if="fieldErrors.loraStrength" class="danger">{{ fieldErrors.loraStrength.join(' ') }}</small>
        </div>

        <Panel class="col-span-2" toggleable>
          <template #header>
            {{ t('samplingSemantic') }}
          </template>
          <small class="muted">{{ t('samplingSemanticIntro') }}</small>
          <SamplingFields class="mt-6" v-model="form.semanticSampling" phase="semanticSampling" :errors="fieldErrors" />

          <div class="flex justify-end">
            <Button
              icon="pi pi-refresh"
              severity="warn"
              size="small"
              :label="samplingChanged('semanticSampling') ? t('samplingReset') : t('samplingAtDefaults')"
              :disabled="!samplingChanged('semanticSampling')"
              @click="resetSampling('semanticSampling')"
            />
          </div>
        </Panel>

        <Panel class="col-span-2" toggleable>
          <template #header>
            {{ t('samplingAbc') }}
          </template>
          <small class="muted">{{ t('samplingAbcIntro') }}</small>
          <small v-if="!plansScore" class="inactive">{{ t('samplingAbcInactive') }}</small>
          <SamplingFields
            class="mt-6"
            v-model="form.abcSampling"
            phase="abcSampling"
            :errors="fieldErrors"
            :disabled="!plansScore"
          />
          <div class="flex justify-end">
            <Button
              severity="warn"
              size="small"
              icon="pi pi-refresh"
              :disabled="!samplingChanged('abcSampling')"
              :label="samplingChanged('abcSampling') ? t('samplingReset') : t('samplingAtDefaults')"
              @click="resetSampling('abcSampling')"
            />
          </div>
        </Panel>
      </div>
    </div>

    <template #footer>
      <div class="flex justify-end">
        <Button
          severity="warn"
          icon="pi pi-refresh"
          :label="changed ? t('advancedReset') : t('advancedAtDefaults')"
          size="small"
          :disabled="!changed"
          @click="resetParameters"
        />
      </div>
    </template>
  </Panel>
</template>
