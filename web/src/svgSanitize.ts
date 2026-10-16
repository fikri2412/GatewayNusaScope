/**
 * Sanitasi SVG berbasis DOM untuk gambar yang akan ditampilkan atau diunduh. Server sudah menolak isi aktif, tetapi
 * berkas SVG yang dibuka langsung di browser mengeksekusi skrip, jadi sisi klien tetap membuang elemen aktif,
 * atribut `on*`, tautan selain fragmen lokal/gambar data, dan `<style>` yang memuat sumber eksternal.
 * Penyajian di halaman memakai <img> data-URL (inert); modul ini untuk unduhan dan pemeriksaan bentuk.
 */
const FORBIDDEN = new Set(['script', 'foreignobject', 'iframe', 'object', 'embed'])
const SAFE_LINK = /^(#|data:image\/(?:png|jpeg|webp);base64,)/i

export function sanitizeSvg(source: string): string {
  const doc = new DOMParser().parseFromString(source, 'image/svg+xml')
  const error = doc.querySelector('parsererror')
  if (error) throw new Error('SVG tidak valid (bukan XML yang benar).')
  const root = doc.documentElement
  if (root.localName !== 'svg') throw new Error('Isi gambar bukan dokumen SVG (elemen akar harus <svg>).')

  for (const element of [root, ...Array.from(root.querySelectorAll('*'))]) {
    if (FORBIDDEN.has(element.localName.toLowerCase())) {
      element.remove()
      continue
    }
    if (element.localName === 'style' && /@import|url\(\s*['"]?(?!#)/i.test(element.textContent ?? '')) {
      element.remove()
      continue
    }
    for (const attribute of Array.from(element.attributes)) {
      const name = attribute.localName.toLowerCase()
      if (name.startsWith('on')) element.removeAttributeNode(attribute)
      else if (name === 'href' && !SAFE_LINK.test(attribute.value.trim())) element.removeAttributeNode(attribute)
      else if (name === 'style' && /url\(\s*['"]?(?!#)|expression\(/i.test(attribute.value)) element.removeAttributeNode(attribute)
    }
  }
  return new XMLSerializer().serializeToString(root)
}
