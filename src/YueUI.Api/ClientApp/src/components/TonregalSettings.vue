<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import { ApiError, checkTonregal, setTonregal } from '../api'
import { t } from '../i18n'
import type { TonregalCheck, TonregalStatus } from '../types'

/**
 * Tonregal's address and API key, written by the server into its env file (`~/.config/tonwerk/tonregal.env`), then
 * checked against Tonregal, so a key that does not match shows here rather than at the first handover. The key is never
 * shown; leaving its field empty keeps it.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ status: TonregalStatus }>()
const emit = defineEmits<{ saved: [status: TonregalStatus] }>()

const url = ref('')
const apiKey = ref('')
const working = ref(false)
const check = ref<TonregalCheck | null>(null)
const error = ref<string | null>(null)

watch(visible, (open) => {
  if (open) {
    url.value = props.status.url ?? ''
    apiKey.value = ''
    check.value = null
    error.value = null
  }
})

const checkText = computed(() => {
  const found = check.value
  if (!found) {
    return null
  }
  const keys = {
    ok: 'tonregalCheckOk',
    key: 'tonregalKey',
    setup: 'tonregalCheckSetup',
    unreachable: 'tonregalCheckUnreachable',
    failed: 'tonregalCheckFailed',
    notConfigured: 'tonregalCheckOff',
  } as const
  return t(keys[found.state], { detail: found.detail ?? '' })
})

async function save(): Promise<void> {
  working.value = true
  error.value = null
  check.value = null
  try {
    const status = await setTonregal({ url: url.value.trim(), apiKey: apiKey.value.trim() || null })
    emit('saved', status)
    apiKey.value = ''
    check.value = await checkTonregal()
  } catch (caught) {
    error.value =
      caught instanceof ApiError
        ? (Object.values(caught.errors)[0]?.[0] ?? caught.message)
        : caught instanceof Error
          ? caught.message
          : String(caught)
  } finally {
    working.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" modal :header="t('tonregalSettings')" class="w-[32rem] max-w-[calc(100vw-2rem)]">
    <form class="flex flex-col gap-3" @submit.prevent="void save()">
      <p v-if="!status.editable" class="m-0 muted text-sm">{{ t('tonregalFixed') }}</p>
      <label class="flex flex-col gap-1">
        <span class="text-sm">{{ t('tonregalUrl') }}</span>
        <InputText
          v-model="url"
          type="url"
          placeholder="http://oi-o-mat.tail6105c0.ts.net:5100"
          autocomplete="off"
          :disabled="!status.editable"
          fluid
        />
      </label>
      <label class="flex flex-col gap-1">
        <span class="text-sm">{{ t('tonregalApiKey') }}</span>
        <InputText
          v-model="apiKey"
          type="password"
          :placeholder="status.keySet ? t('tonregalKeyKept') : ''"
          autocomplete="off"
          :disabled="!status.editable"
          fluid
        />
      </label>
      <p class="m-0 muted text-sm">{{ t('tonregalKeyHint') }}</p>
      <p v-if="checkText" class="m-0 text-sm" :class="{ danger: check?.state !== 'ok' }" role="status">
        {{ checkText }}
      </p>
      <p v-if="error" class="m-0 text-sm danger" role="status">{{ error }}</p>
      <div class="flex justify-end gap-2">
        <Button :label="t('tonregalClose')" severity="secondary" text @click="visible = false" />
        <Button type="submit" :label="t('tonregalSave')" :loading="working" :disabled="!status.editable" />
      </div>
    </form>
  </Dialog>
</template>
