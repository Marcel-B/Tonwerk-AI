<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { t } from '../i18n'
import { helpersOffered, loadMachines, machineOptions, type MachineNeed } from '../machines'

/** Which Mac a job runs on, offered only when the server has a helper besides itself (see machines.ts). */
const props = defineProps<{
  /** The server's engine name the job needs. */
  engine: MachineNeed
  disabled?: boolean
}>()
/** Empty for automatic, `local` or a helper's id. */
const selection = defineModel<string>({ required: true })

onMounted(() => void loadMachines())

const options = computed(() => machineOptions(props.engine, selection.value))
</script>

<template>
  <Select
    v-if="helpersOffered"
    v-model="selection"
    :options="options"
    option-value="value"
    option-label="label"
    :disabled="disabled"
    :aria-label="t('machine')"
    :title="t('machine')"
    size="small"
    class="min-w-0"
  >
    <template #value="{ value }">
      <span
        ><i class="pi pi-desktop mr-2" aria-hidden="true" />{{ options.find((o) => o.value === value)?.label }}</span
      >
    </template>
  </Select>
</template>
