import { Children, createElement, isValidElement, type ReactElement, type ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import type { Components, UrlTransform } from 'react-markdown'
import { Highlight, themes } from 'prism-react-renderer'
import remarkBreaks from 'remark-breaks'
import remarkGfm from 'remark-gfm'

/**
 * Pipeline markdown jawaban assistant. Ditulis di `.ts` (bukan `.tsx`) supaya `npm run check:markdown`
 * bisa merender lewat Node tanpa JSX; `Markdown.tsx` hanya membungkus fungsi ini, jadi yang diperiksa
 * adalah renderer yang benar-benar dipakai.
 */

/** Hanya skema ini yang boleh jadi tujuan tautan. */
const SAFE_PROTOCOLS = ['http:', 'https:', 'mailto:']

/** Relatif/asing (mis. `javascript:`) tidak pernah ditulis: di dalam jawaban chat tidak ada tujuan yang masuk akal. */
const isSafeUrl = (url: string) => {
  const colon = url.trim().indexOf(':')
  return colon > 0 && SAFE_PROTOCOLS.includes(url.trim().slice(0, colon + 1).toLowerCase())
}

/** URL tidak aman → null; react-markdown membuang properti bernilai null, jadi href-nya hilang seluruhnya. */
export const urlTransform: UrlTransform = (url) => (isSafeUrl(url) ? url : null)

const link: Components['a'] = ({ href, title, children }) =>
  // Satu-satunya tempat tautan ditulis; penjaga di sini tetap berlaku walau urlTransform dilewati.
  createElement(
    'a',
    { href: href && isSafeUrl(href) ? href : undefined, target: '_blank', rel: 'noopener noreferrer', title },
    children,
  )

// Gambar tidak dirender (tidak ada permintaan jaringan ke host asing dari dalam chat); alt-nya tetap terbaca.
const image: Components['img'] = ({ alt }) => (alt ? createElement('span', { className: 'md-image-alt' }, alt) : null)

const table: Components['table'] = ({ children }) =>
  createElement('div', { className: 'md-table-scroll' }, createElement('table', null, children))

/** Anak yang sudah punya kelas bahasa: blok itu merender `<pre>` sendiri, jadi pembungkusnya dibuang. */
const isHighlighted = (children: ReactNode) =>
  Children.toArray(children).some((child) => {
    if (!isValidElement(child)) return false
    const props = child.props
    if (!props || typeof props !== 'object' || !('className' in props)) return false
    return String(props.className ?? '').includes('language-')
  })

const code: Components['code'] = ({ className, children }) => {
  const language = /language-([\w-]+)/.exec(className ?? '')?.[1]
  if (!language) return createElement('code', { className }, children)
  return createElement(Highlight, {
    code: String(children).replace(/\n$/, ''),
    language,
    theme: themes.github,
    // Highlight menerima tokennya lewat fungsi render di `children`; modul .ts ini tidak punya JSX.
    // oxlint-disable-next-line react/no-children-prop
    children: ({ className: prismClass, style, tokens, getLineProps, getTokenProps }) =>
      createElement(
        'pre',
        { className: `md-code-block ${prismClass}`, style },
        tokens.map((line, lineIndex) =>
          createElement(
            'div',
            { key: lineIndex, ...getLineProps({ line }) },
            line.map((token, tokenIndex) => createElement('span', { key: tokenIndex, ...getTokenProps({ token }) })),
          ),
        ),
      ),
  })
}

/** Highlight merender `<pre>`-nya sendiri: pembungkus bawaan markdown dibuang supaya tidak bersarang. */
const pre: Components['pre'] = ({ children }) =>
  isHighlighted(children) ? children : createElement('pre', { className: 'md-code-block' }, children)

const components: Components = { a: link, code, img: image, pre, table }

/** Markdown jawaban: HTML mentah dibuang (`skipHtml`), baris baru tunggal tetap jadi baris baru. */
export function renderMarkdown(text: string): ReactElement {
  return createElement(ReactMarkdown, {
    remarkPlugins: [remarkGfm, remarkBreaks],
    skipHtml: true,
    urlTransform,
    components,
  }, text)
}
