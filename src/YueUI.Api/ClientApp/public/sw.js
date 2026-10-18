// Service worker for Web Push (src/YueUI.Api/Push) and offline listening (src/offline.ts).
//
// The app itself is fetched from the network first and taken from the cache only when there is none, so a deploy is
// never hidden behind an old build. Song copies the page kept for offline playlists are answered from the cache first,
// with or without a connection, since that also spares the Mac's slow upload; the player asks for byte ranges, which
// are cut from the cached file here.

const OFFLINE = 'tonwerk-offline'
const SHELL = 'tonwerk-shell'

self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()))

const scopePath = new URL(self.registration.scope).pathname
// What the playlist page needs to show itself without the Mac.
const lastKnown = ['/api/library', '/api/playlists']

self.addEventListener('fetch', (event) => {
  const request = event.request
  const url = new URL(request.url)
  // The page checks its copies (by ETag) past the cache, and everything else goes its own way.
  if (request.method !== 'GET' || url.origin !== self.location.origin || request.cache === 'no-store') {
    return
  }
  if (/^\/api\/songs\/[^/]+\/[^/]+\/(stream|cover)$/.test(url.pathname)) {
    event.respondWith(fromOffline(request))
  } else if (lastKnown.includes(url.pathname)) {
    event.respondWith(networkFirst(request))
  } else if (url.pathname.startsWith(`${scopePath}assets/`)) {
    // Hashed names: a file never changes, a new build has new names.
    event.respondWith(cacheFirst(request))
  } else if (url.pathname.startsWith(scopePath) && !url.pathname.endsWith('/version.json')) {
    event.respondWith(networkFirst(request))
  }
})

async function fromOffline(request) {
  const kept = await (await caches.open(OFFLINE)).match(request.url)
  if (!kept) {
    return fetch(request)
  }
  const range = request.headers.get('Range')
  return range ? slice(kept, range) : kept
}

/** A 206 of the asked bytes, as the server answers it; Safari plays media only from a source that does. */
async function slice(response, range) {
  const blob = await response.blob()
  const size = blob.size
  const match = /^bytes=(\d*)-(\d*)$/.exec(range.trim())
  let start
  let end
  if (!match || (match[1] === '' && match[2] === '')) {
    return new Response(null, { status: 416, headers: { 'Content-Range': `bytes */${size}` } })
  }
  if (match[1] === '') {
    start = Math.max(size - Number(match[2]), 0)
    end = size - 1
  } else {
    start = Number(match[1])
    end = match[2] === '' ? size - 1 : Math.min(Number(match[2]), size - 1)
  }
  if (start >= size || start > end) {
    return new Response(null, { status: 416, headers: { 'Content-Range': `bytes */${size}` } })
  }
  return new Response(blob.slice(start, end + 1), {
    status: 206,
    headers: {
      'Content-Type': response.headers.get('Content-Type') || 'audio/mp4',
      'Content-Range': `bytes ${start}-${end}/${size}`,
      'Content-Length': String(end - start + 1),
      'Accept-Ranges': 'bytes',
    },
  })
}

async function networkFirst(request) {
  const cache = await caches.open(SHELL)
  try {
    const response = await fetch(request)
    if (response.ok) {
      await cache.put(request, response.clone())
    }
    return response
  } catch (error) {
    const kept = await cache.match(request, { ignoreSearch: request.mode === 'navigate' })
    if (kept) {
      return kept
    }
    throw error
  }
}

async function cacheFirst(request) {
  const cache = await caches.open(SHELL)
  const kept = await cache.match(request)
  if (kept) {
    return kept
  }
  const response = await fetch(request)
  if (response.ok) {
    await cache.put(request, response.clone())
  }
  return response
}

// Payload from PushNotifier.Payload: { title, body, tag, url }. Every push must show a notification: Safari revokes
// the subscription of a site that receives pushes silently.
self.addEventListener('push', (event) => {
  let message = {}
  try {
    message = event.data ? event.data.json() : {}
  } catch {
    message = { body: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.registration.showNotification(message.title || 'Tonwerk-AI', {
      body: message.body || '',
      tag: message.tag,
      icon: 'icon-192.png',
      badge: 'badge-96.png',
      data: { url: message.url || self.registration.scope },
    }),
  )
})

// Tapping a notification brings an open window of the app forward, or opens one.
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = new URL(event.notification.data?.url || self.registration.scope, self.location.origin).href
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      const open = windows.find((client) => client.url.startsWith(self.registration.scope))
      return open ? open.focus() : self.clients.openWindow(url)
    }),
  )
})
