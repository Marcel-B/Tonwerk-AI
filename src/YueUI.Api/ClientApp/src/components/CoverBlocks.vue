<script setup lang="ts">
import { computed, ref } from 'vue'
import { t, type MessageKey } from '../i18n'
import { blocksForStyle, coverBlocks, coverGroups, type CoverGroup } from '../coverBlocks'

/**
 * The cover blocks under the description, in tabs like the song's style blocks (`StyleBlocks.vue`). A tap asks the
 * dialog to put the block's sentence in or take it out, since only the dialog knows whether the description is still
 * its suggestion.
 */
const props = defineProps<{
  /** Keys of the blocks the description holds. */
  chosen: string[]
  /** The song's style, for the first tab. */
  songStyle: string
}>()
const emit = defineEmits<{ toggle: [key: string] }>()

const fitting = computed(() => blocksForStyle(props.songStyle))
const tab = ref<CoverGroup | 'song'>(fitting.value.length > 0 ? 'song' : 'medium')

function keysOf(group: CoverGroup): string[] {
  return coverBlocks.filter((block) => block.group === group).map((block) => block.key)
}

function count(group: CoverGroup): number {
  return keysOf(group).filter((key) => props.chosen.includes(key)).length
}

function label(key: string): string {
  return t(`coverBlock_${key}` as MessageKey)
}
</script>

<template>
  <div>
    <span class="font-medium">{{ t('coverBlocks') }}</span>
    <Tabs v-model:value="tab" scrollable class="mt-1">
      <TabList>
        <Tab v-if="fitting.length > 0" value="song">{{ t('coverBlocks_song') }}</Tab>
        <Tab v-for="group in coverGroups" :key="group" :value="group">
          {{ t(`coverBlocks_${group}` as MessageKey) }}
          <Badge v-if="count(group) > 0" :value="count(group)" size="small" />
        </Tab>
      </TabList>
      <TabPanels>
        <TabPanel
          v-for="panel in [
            ...(fitting.length > 0 ? [{ value: 'song', keys: fitting }] : []),
            ...coverGroups.map((group) => ({ value: group, keys: keysOf(group) })),
          ]"
          :key="panel.value"
          :value="panel.value"
        >
          <small class="muted block">{{ t(`coverBlocks_${panel.value}Hint` as MessageKey) }}</small>
          <div class="mt-3 flex flex-wrap gap-2" role="group">
            <Button
              v-for="key in panel.keys"
              :key="key"
              type="button"
              size="small"
              rounded
              :label="label(key)"
              :icon="chosen.includes(key) ? 'pi pi-check' : undefined"
              :outlined="!chosen.includes(key)"
              :severity="chosen.includes(key) ? undefined : 'secondary'"
              :aria-pressed="chosen.includes(key)"
              @click="emit('toggle', key)"
            />
          </div>
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>
