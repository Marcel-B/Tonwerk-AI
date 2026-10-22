import { ref } from 'vue'
import type { WritingReply } from './types'

/**
 * The writing page's answer as the event stream last brought it, kept outside the page so an answer that grows while
 * the page was never opened (or another device asked it) is there when it opens.
 */
export const liveWriting = ref<WritingReply | null>(null)
