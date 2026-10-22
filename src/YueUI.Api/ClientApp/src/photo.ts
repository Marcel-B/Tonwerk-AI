/** Longest side a photo is sent with: enough for a vision model (Gemma 4 sees at most about this), some 200 KB. */
const maxSide = 1024

/**
 * A photo as a JPEG data URL for a lyrics draft, scaled down in the browser: a phone's original is several MB and
 * often HEIC, which the language model cannot read. Drawing it through an `<img>` also applies its EXIF rotation.
 */
export async function photoDataUrl(file: File): Promise<string> {
  const url = URL.createObjectURL(file)
  try {
    const image = new Image()
    image.src = url
    await image.decode()
    const scale = Math.min(1, maxSide / Math.max(image.naturalWidth, image.naturalHeight))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(image.naturalWidth * scale))
    canvas.height = Math.max(1, Math.round(image.naturalHeight * scale))
    const context = canvas.getContext('2d')
    if (!context) {
      throw new Error('No canvas to scale the photo.')
    }
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    return canvas.toDataURL('image/jpeg', 0.85)
  } finally {
    URL.revokeObjectURL(url)
  }
}

/**
 * A picture as a JPEG of about `pixels` for a cover reference: the image models scale references to about a
 * megapixel anyway, and a phone's original would only cost upload time (and is often HEIC, which Pillow cannot read).
 */
export async function referenceJpeg(file: Blob, pixels = 1_000_000): Promise<Blob> {
  const url = URL.createObjectURL(file)
  try {
    const image = new Image()
    image.src = url
    await image.decode()
    const scale = Math.min(1, Math.sqrt(pixels / (image.naturalWidth * image.naturalHeight)))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(image.naturalWidth * scale))
    canvas.height = Math.max(1, Math.round(image.naturalHeight * scale))
    const context = canvas.getContext('2d')
    if (!context) {
      throw new Error('No canvas to scale the picture.')
    }
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob(
        (blob) => (blob ? resolve(blob) : reject(new Error('The picture could not be encoded.'))),
        'image/jpeg',
        0.9,
      ),
    )
  } finally {
    URL.revokeObjectURL(url)
  }
}
