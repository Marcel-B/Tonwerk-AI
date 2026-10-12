<script setup lang="ts">
import { ref, watch } from 'vue'
import { locale } from '../i18n'

/**
 * NumberField for a value that may be left to the model: an empty field is null, shown with the placeholder
 * ("auto"). Typed as text for the same reason (the iPhone's keypad comma); comma and point both mean the decimal point.
 */
const props = withDefaults(defineProps<{ min: number; max: number; fractionDigits?: number }>(), {
  fractionDigits: 0,
})
const model = defineModel<number | null>({ required: true })

const text = ref(format(model.value))
const editing = ref(false)

watch([model, locale], () => {
  if (!editing.value) {
    text.value = format(model.value)
  }
})

function format(value: number | null): string {
  return value !== null && Number.isFinite(value)
    ? value.toLocaleString(locale.value, { maximumFractionDigits: props.fractionDigits, useGrouping: false })
    : ''
}

/** "0,95", "0.95" and ",95" are 0.95, an empty field is null; anything else (undefined) keeps the last value. */
function parse(value: string): number | null | undefined {
  const normalized = value.trim().replace(',', '.')
  if (normalized === '') {
    return null
  }
  if (!/^-?\d*\.?\d*$/.test(normalized) || !/\d/.test(normalized)) {
    return undefined
  }
  const number = Number(Number(normalized).toFixed(props.fractionDigits))
  return Number.isFinite(number) ? Math.min(props.max, Math.max(props.min, number)) : undefined
}

function onInput(value: string | undefined) {
  text.value = value ?? ''
  const number = parse(text.value)
  if (number !== undefined) {
    model.value = number
  }
}

function onBlur() {
  editing.value = false
  text.value = format(model.value)
}
</script>

<template>
  <InputText
    fluid
    :model-value="text"
    type="text"
    :inputmode="fractionDigits > 0 || min < 0 ? 'decimal' : 'numeric'"
    autocomplete="off"
    @update:model-value="onInput"
    @focus="editing = true"
    @blur="onBlur"
  />
</template>
