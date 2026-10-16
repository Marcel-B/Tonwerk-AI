<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { t, type MessageKey } from '../i18n'
import { loadInsights, provenStyleBlocks } from '../insights'
import { countTags, hasTag, styleCategories, toggleTag, type StyleCategory } from '../styleTags'
import { heardTempo, tempoTag } from '../traits'
import FieldHelp from './FieldHelp.vue'

/** Whether the song is instrumental: voice and timbre then describe nobody. */
defineProps<{ instrumental: boolean }>()
const style = defineModel<string>({ required: true })

const category = ref<StyleCategory | 'proven'>('genre')

// The proven blocks are a suggestion; without a server that knows them the tab just stays away.
onMounted(() => loadInsights().catch(() => {}))

function label(key: StyleCategory): string {
  return t(`styleBlocks_${key}` as MessageKey)
}

/** The blocks of a category; the tempo heard in the song taken over goes first, if the list lacks it. */
function tagsOf(key: StyleCategory, tags: readonly string[]): readonly string[] {
  const heard = key === 'tempo' && heardTempo.value ? tempoTag(heardTempo.value) : null
  return heard && !tags.includes(heard) ? [heard, ...tags] : tags
}

function hint(key: StyleCategory): string {
  return t(`styleBlocks_${key}Hint` as MessageKey)
}
</script>

<template>
  <div class="mt-2">
    <FieldHelp id="gen-style-blocks-help" :hint="t('styleBlocksIntro')" :more="t('styleBlocksMore')" />
    <Tabs v-model:value="category" scrollable class="mt-1">
      <TabList>
        <Tab v-if="provenStyleBlocks.length > 0" value="proven"
          ><i class="pi pi-star-fill text-xs" /> {{ t('styleBlocks_proven') }}</Tab
        >
        <Tab v-for="definition in styleCategories" :key="definition.key" :value="definition.key">
          {{ label(definition.key) }}
          <Badge v-if="countTags(style, definition.key) > 0" :value="countTags(style, definition.key)" size="small" />
        </Tab>
      </TabList>
      <TabPanels>
        <TabPanel v-if="provenStyleBlocks.length > 0" value="proven">
          <small class="muted block">{{ t('styleBlocks_provenHint') }}</small>
          <div class="mt-3 flex flex-wrap gap-2" role="group" :aria-label="t('styleBlocks_proven')">
            <Button
              v-for="tag in provenStyleBlocks"
              :key="tag"
              type="button"
              size="small"
              rounded
              :label="tag"
              :icon="hasTag(style, tag) ? 'pi pi-check' : undefined"
              :outlined="!hasTag(style, tag)"
              :severity="hasTag(style, tag) ? undefined : 'secondary'"
              :aria-pressed="hasTag(style, tag)"
              @click="style = toggleTag(style, tag)"
            />
          </div>
        </TabPanel>
        <TabPanel v-for="definition in styleCategories" :key="definition.key" :value="definition.key">
          <small class="muted block">{{ hint(definition.key) }}</small>
          <small
            v-if="instrumental && (definition.key === 'voice' || definition.key === 'timbre')"
            class="warning block"
            >{{ t('styleBlocksInstrumental') }}</small
          >
          <small v-if="definition.key === 'tempo' && heardTempo" class="muted block">
            {{ t('styleBlocks_heard') }}: {{ tempoTag(heardTempo) }}
          </small>
          <div class="mt-3 flex flex-wrap gap-2" role="group" :aria-label="label(definition.key)">
            <Button
              v-for="tag in tagsOf(definition.key, definition.tags)"
              :key="tag"
              type="button"
              size="small"
              rounded
              :label="tag"
              :icon="hasTag(style, tag) ? 'pi pi-check' : undefined"
              :outlined="!hasTag(style, tag)"
              :severity="hasTag(style, tag) ? undefined : 'secondary'"
              :aria-pressed="hasTag(style, tag)"
              @click="style = toggleTag(style, tag)"
            />
          </div>
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>

<style scoped>
.warning {
  margin-top: 0.3rem;
  color: var(--warning-text);
}
</style>
