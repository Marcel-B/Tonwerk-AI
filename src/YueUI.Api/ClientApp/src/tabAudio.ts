/**
 * Recording what another browser tab plays (a video, a stream, a song in a web player), through screen capture with the
 * tab's audio. Only Chromium hands over a tab's sound: Safari and Firefox share the screen without it, and phones have
 * no screen capture at all, so the button is offered only where it can work.
 */

/** What screen capture needs to be asked for a tab's sound. */
interface Navigatorish {
  mediaDevices?: { getDisplayMedia?: unknown }
  userAgentData?: unknown
}

/**
 * Chromium on a desktop: it alone has `getDisplayMedia` with tab audio, and it alone has `userAgentData` (Safari and
 * Firefox have the first without the audio, Chrome on Android neither).
 */
export function tabAudioSupported(
  nav: Navigatorish | undefined = globalThis.navigator,
  recorder = globalThis.MediaRecorder,
): boolean {
  return (
    !!nav &&
    typeof nav.mediaDevices?.getDisplayMedia === 'function' &&
    nav.userAgentData !== undefined &&
    typeof recorder === 'function'
  )
}

/** The file a recording becomes: its start time in the name, so two of them in the list can be told apart. */
export function tabRecordingName(started: Date, extension: string): string {
  const two = (n: number): string => String(n).padStart(2, '0')
  const date = `${started.getFullYear()}-${two(started.getMonth() + 1)}-${two(started.getDate())}`
  return `Tab ${date} ${two(started.getHours())}-${two(started.getMinutes())}-${two(started.getSeconds())}.${extension}`
}

/** Opus in WebM is what Chromium records; the server's ffmpeg reads it. */
function recordingType(): { mimeType?: string; extension: string } {
  for (const [mimeType, extension] of [
    ['audio/webm;codecs=opus', 'webm'],
    ['audio/webm', 'webm'],
    ['audio/mp4', 'm4a'],
  ] as const) {
    if (MediaRecorder.isTypeSupported(mimeType)) {
      return { mimeType, extension }
    }
  }
  return { extension: 'webm' }
}

/** Thrown when the user shared a tab without ticking "also share tab audio", or a window that has none. */
export class NoTabAudio extends Error {
  constructor() {
    super('no tab audio')
  }
}

export interface TabRecording {
  /** Ends the recording; `done` then resolves with the file. */
  stop(): void
  /** The recording as a file, once stopped here or by Chrome's own "stop sharing". */
  done: Promise<File>
}

/**
 * Asks Chrome for a tab with its sound and records that sound. Chrome insists on a video track as well; it is kept
 * until the end (stopping it early may end the whole capture) and never recorded.
 */
export async function recordTab(): Promise<TabRecording> {
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: true,
    // Raw sound: the call processing would treat music as noise. The tab keeps playing for the listener.
    audio: {
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false,
      suppressLocalAudioPlayback: false,
    },
    // Chrome's own hints: offer tabs first, leave this one out (recording it would record nothing but itself).
    preferCurrentTab: false,
    selfBrowserSurface: 'exclude',
    systemAudio: 'include',
  } as DisplayMediaStreamOptions)
  const release = (): void => stream.getTracks().forEach((track) => track.stop())
  const audio = stream.getAudioTracks()
  if (audio.length === 0) {
    release()
    throw new NoTabAudio()
  }
  const type = recordingType()
  const started = new Date()
  const chunks: Blob[] = []
  // Opus at its music rate; the default is set for speech.
  const recorder = new MediaRecorder(new MediaStream(audio), {
    ...(type.mimeType ? { mimeType: type.mimeType } : {}),
    audioBitsPerSecond: 256_000,
  })
  const done = new Promise<File>((resolve, reject) => {
    recorder.addEventListener('dataavailable', (event) => {
      if (event.data.size > 0) {
        chunks.push(event.data)
      }
    })
    recorder.addEventListener('stop', () => {
      release()
      const blob = new Blob(chunks, { type: recorder.mimeType || type.mimeType || 'audio/webm' })
      resolve(new File([blob], tabRecordingName(started, type.extension), { type: blob.type }))
    })
    recorder.addEventListener('error', (event) => {
      release()
      reject(new Error(String((event as ErrorEvent).error ?? 'recording failed')))
    })
  })
  const stop = (): void => {
    if (recorder.state !== 'inactive') {
      recorder.stop()
    }
  }
  // Chrome's bar "stop sharing", or the tab closed: the capture ends, so does the recording.
  stream.getTracks().forEach((track) => track.addEventListener('ended', stop))
  // In slices, so an hour's recording is not one buffer at the end.
  recorder.start(1000)
  return { stop, done }
}
