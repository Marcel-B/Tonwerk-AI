import { ref } from 'vue'
import { deletePushSubscription, pushKey, savePushSubscription, testPush } from './api'
import { locale } from './i18n'

/**
 * Whether this browser can be notified when something finishes, and whether it is:
 * - `unsupported`: no Web Push here at all.
 * - `install`: iOS/iPadOS in a browser tab; Web Push only works once the app was added to the home screen.
 * - `denied`: the user blocked notifications; only the system or browser settings can undo that.
 */
export type PushState = 'unsupported' | 'install' | 'denied' | 'off' | 'on'

export const pushState = ref<PushState>('unsupported')

const swUrl = `${import.meta.env.BASE_URL}sw.js`

function supported(): boolean {
  return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window
}

/** iPadOS reports itself as a Mac, but a Mac has no touch screen. */
function isIos(): boolean {
  return (
    /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)
  )
}

function standalone(): boolean {
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    (navigator as { standalone?: boolean }).standalone === true
  )
}

async function currentSubscription(): Promise<PushSubscription | null> {
  const registration = await navigator.serviceWorker.ready
  return registration.pushManager.getSubscription()
}

/**
 * Registers the service worker (push and offline playlists) and finds out the state. An existing subscription is sent to the server again, so
 * that one the server lost (a new data file, a new key pair) heals by itself, and so that it speaks the current
 * language.
 */
export async function initPush(): Promise<void> {
  // Offline playlists need the worker too, also where there is no Web Push (Safari in a tab).
  if ('serviceWorker' in navigator) {
    await navigator.serviceWorker.register(swUrl, { scope: import.meta.env.BASE_URL })
  }
  if (!supported()) {
    pushState.value = isIos() && !standalone() ? 'install' : 'unsupported'
    return
  }
  if (Notification.permission === 'denied') {
    pushState.value = 'denied'
    return
  }
  const subscription = Notification.permission === 'granted' ? await currentSubscription() : null
  if (subscription && (await sameKey(subscription))) {
    await savePushSubscription(subscription, locale.value)
    pushState.value = 'on'
  } else {
    await subscription?.unsubscribe()
    pushState.value = 'off'
  }
}

/** Must run in a tap's handler: Safari asks for permission only then. Returns the new state. */
export async function enablePush(): Promise<PushState> {
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') {
    pushState.value = permission === 'denied' ? 'denied' : 'off'
    return pushState.value
  }
  const registration = await navigator.serviceWorker.ready
  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: base64UrlToBytes(await pushKey()),
  })
  await savePushSubscription(subscription, locale.value)
  pushState.value = 'on'
  await testPush(subscription.endpoint)
  return pushState.value
}

export async function disablePush(): Promise<void> {
  const subscription = await currentSubscription()
  if (subscription) {
    await deletePushSubscription(subscription.endpoint)
    await subscription.unsubscribe()
  }
  pushState.value = 'off'
}

/** Tells the server the new language, so notifications follow the switch. */
export async function updatePushLanguage(): Promise<void> {
  const subscription = pushState.value === 'on' ? await currentSubscription() : null
  if (subscription) {
    await savePushSubscription(subscription, locale.value)
  }
}

/** A subscription made for another key pair would never be delivered to. */
async function sameKey(subscription: PushSubscription): Promise<boolean> {
  const own = subscription.options.applicationServerKey
  if (!own) {
    return true
  }
  const server = base64UrlToBytes(await pushKey())
  const bytes = new Uint8Array(own)
  return bytes.length === server.length && bytes.every((byte, index) => byte === server[index])
}

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value
    .replace(/-/g, '+')
    .replace(/_/g, '/')
    .padEnd(Math.ceil(value.length / 4) * 4, '=')
  return Uint8Array.from(atob(base64), (char) => char.charCodeAt(0))
}
