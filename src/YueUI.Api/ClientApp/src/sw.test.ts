import { describe, expect, it } from 'vitest'
import code from '../public/sw.js?raw'

/**
 * The service worker is a plain script in `public/`; it runs here with a fake `self` and Cache Storage, and only its
 * fetch handler is looked at.
 */
function serviceWorker(kept: Map<string, Response>, network: (request: Request) => Promise<Response>) {
  const listeners = new Map<string, (event: unknown) => void>()
  const self = {
    registration: { scope: 'https://mac.example/ui/' },
    location: { origin: 'https://mac.example' },
    addEventListener: (type: string, listener: (event: unknown) => void) => listeners.set(type, listener),
  }
  const caches = {
    open: async () => ({
      match: async (key: Request | string) => kept.get(typeof key === 'string' ? key : key.url)?.clone(),
      put: async (key: Request, response: Response) => void kept.set(key.url, response),
    }),
  }
  new Function('self', 'caches', 'fetch', code)(self, caches, network)
  return async (request: Request): Promise<Response | undefined> => {
    let answer: Promise<Response> | undefined
    listeners.get('fetch')!({ request, respondWith: (response: Promise<Response>) => (answer = response) })
    return answer
  }
}

const stream = 'https://mac.example/api/songs/run-1/song1/stream'
const bytes = new Uint8Array(Array.from({ length: 100 }, (_, i) => i))

describe('service worker', () => {
  it('answers a kept song in byte ranges, as the player asks for them', async () => {
    const handle = serviceWorker(
      new Map([[stream, new Response(bytes, { headers: { 'Content-Type': 'audio/mp4' } })]]),
      () => Promise.reject(new Error('offline')),
    )

    const part = (await handle(new Request(stream, { headers: { Range: 'bytes=10-19' } })))!
    expect(part.status).toBe(206)
    expect(part.headers.get('Content-Range')).toBe('bytes 10-19/100')
    expect([...new Uint8Array(await part.arrayBuffer())]).toEqual([10, 11, 12, 13, 14, 15, 16, 17, 18, 19])

    const open = (await handle(new Request(stream, { headers: { Range: 'bytes=90-' } })))!
    expect(open.headers.get('Content-Range')).toBe('bytes 90-99/100')
    const tail = (await handle(new Request(stream, { headers: { Range: 'bytes=-5' } })))!
    expect(tail.headers.get('Content-Range')).toBe('bytes 95-99/100')
    expect((await handle(new Request(stream, { headers: { Range: 'bytes=200-' } })))!.status).toBe(416)
    expect((await handle(new Request(stream)))!.status).toBe(200)
  })

  it('leaves the page’s own checks and other requests to the network', async () => {
    const handle = serviceWorker(new Map(), () => Promise.resolve(new Response('net')))

    expect(await handle(new Request(stream, { cache: 'no-store' }))).toBeUndefined()
    expect(await handle(new Request('https://mac.example/api/status'))).toBeUndefined()
    expect(await handle(new Request(stream, { method: 'POST', body: 'x' }))).toBeUndefined()
    expect(await (await handle(new Request(stream)))!.text()).toBe('net')
  })

  it('keeps the library for when the Mac cannot be reached, but asks the network first', async () => {
    let online = true
    const handle = serviceWorker(new Map(), () =>
      online ? Promise.resolve(new Response('[1]')) : Promise.reject(new Error('offline')),
    )
    const library = 'https://mac.example/api/library'

    expect(await (await handle(new Request(library)))!.text()).toBe('[1]')
    online = false
    expect(await (await handle(new Request(library)))!.text()).toBe('[1]')
    await expect(handle(new Request('https://mac.example/api/playlists'))).rejects.toThrow('offline')
  })
})
