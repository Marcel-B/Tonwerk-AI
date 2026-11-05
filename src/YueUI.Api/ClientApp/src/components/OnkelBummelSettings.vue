<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import Checkbox from 'primevue/checkbox'
import { ApiError, setOnkelBummel, testOnkelBummel } from '../api'
import { t } from '../i18n'
import type { OnkelBummelCheck, OnkelBummelStatus } from '../types'

/**
 * Onkel Bummel's address, publish token and topic, plus Tonwerk's own address for the messages' links, written by the
 * server into its env file (`~/.config/tonwerk/onkel-bummel.env`); saving sends a test message, so a token without
 * the right for the topic shows here rather than as silence after the next batch. The token is never shown; leaving
 * its field empty keeps it.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ status: OnkelBummelStatus }>()
const emit = defineEmits<{ saved: [status: OnkelBummelStatus] }>()

const enabled = ref(true)
const url = ref('')
const token = ref('')
const topic = ref('')
const link = ref('')
const working = ref(false)
const check = ref<OnkelBummelCheck | null>(null)
const error = ref<string | null>(null)

watch(visible, (open) => {
  if (open) {
    enabled.value = props.status.enabled
    url.value = props.status.url ?? ''
    token.value = ''
    topic.value = props.status.topic
    link.value = props.status.link ?? ''
    check.value = null
    error.value = null
  }
})

const checkText = computed(() => {
  const found = check.value
  if (!found) {
    return null
  }
  if (found.state === 'ok' && !props.status.enabled) {
    return t('onkelBummelCheckDisabled')
  }
  const keys = {
    ok: 'onkelBummelCheckOk',
    token: 'onkelBummelCheckToken',
    topic: 'onkelBummelCheckTopic',
    unreachable: 'onkelBummelCheckUnreachable',
    failed: 'onkelBummelCheckFailed',
    notConfigured: 'onkelBummelCheckOff',
  } as const
  return t(keys[found.state], { detail: found.detail ?? '' })
})

async function save(): Promise<void> {
  working.value = true
  error.value = null
  check.value = null
  try {
    const status = await setOnkelBummel({
      url: url.value.trim(),
      token: token.value.trim() || null,
      topic: topic.value.trim() || null,
      link: link.value.trim() || null,
      enabled: enabled.value,
    })
    emit('saved', status)
    token.value = ''
    // Switched off, a test would still arrive; only check what will be used.
    check.value = status.enabled ? await testOnkelBummel() : { state: 'ok', detail: null }
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
  <Dialog v-model:visible="visible" modal :header="t('onkelBummelSettings')" class="w-[32rem] max-w-[calc(100vw-2rem)]">
    <form class="flex flex-col gap-3" @submit.prevent="void save()">
      <p class="m-0 muted text-sm">{{ t('onkelBummelIntro') }}</p>
      <p v-if="!status.editable" class="m-0 muted text-sm">{{ t('onkelBummelFixed') }}</p>
      <label class="flex items-center gap-2">
        <Checkbox v-model="enabled" binary :disabled="!status.editable" />
        <span>{{ t('onkelBummelEnabled') }}</span>
      </label>
      <label class="flex flex-col gap-1">
        <span class="text-sm">{{ t('onkelBummelUrl') }}</span>
        <InputText
          v-model="url"
          type="url"
          placeholder="https://onkel-bummel.tail6105c0.ts.net"
          autocomplete="off"
          :disabled="!status.editable"
          fluid
        />
      </label>
      <label class="flex flex-col gap-1">
        <span class="text-sm">{{ t('onkelBummelToken') }}</span>
        <InputText
          v-model="token"
          type="password"
          :placeholder="status.tokenSet ? t('onkelBummelTokenKept') : 'ob_…'"
          autocomplete="off"
          :disabled="!status.editable"
          fluid
        />
      </label>
      <label class="flex flex-col gap-1">
        <span class="text-sm">{{ t('onkelBummelTopic') }}</span>
        <InputText v-model="topic" placeholder="tonwerk" autocomplete="off" :disabled="!status.editable" fluid />
      </label>
      <label class="flex flex-col gap-1">
        <span class="text-sm">{{ t('onkelBummelLink') }}</span>
        <InputText
          v-model="link"
          type="url"
          placeholder="https://oi-o-mat.tail6105c0.ts.net:8443"
          autocomplete="off"
          :disabled="!status.editable"
          fluid
        />
      </label>
      <p class="m-0 muted text-sm">{{ t('onkelBummelHint') }}</p>
      <p
        v-if="checkText"
        class="m-0 text-sm"
        :class="{ danger: !['ok', 'notConfigured'].includes(check?.state ?? '') }"
        role="status"
      >
        {{ checkText }}
      </p>
      <p v-if="error" class="m-0 text-sm danger" role="status">{{ error }}</p>
      <div class="flex justify-end gap-2">
        <Button :label="t('onkelBummelClose')" severity="secondary" text @click="visible = false" />
        <Button type="submit" :label="t('onkelBummelSave')" :loading="working" :disabled="!status.editable" />
      </div>
    </form>
  </Dialog>
</template>
