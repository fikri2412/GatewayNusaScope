import type { ReactNode } from 'react'

export function Brand({ compact = false }: { compact?: boolean }) {
  return <span className="brand"><img src="/brand/logo-nusascope.svg" width="28" height="28" alt="" />{!compact && <span>NusaScope</span>}</span>
}

/** Glyph konseptual, bukan logo resmi atau klaim capability model. Alias tetap ditampilkan apa adanya. */
export function ModelGlyph({ alias, size = 22 }: { alias?: string | null; size?: number }) {
  const id = alias?.toLowerCase() ?? ''
  const glyph = id.includes('deepseek') ? 'deepseek' : id.includes('glm') ? 'glm' : id.includes('minimax') ? 'minimax' : 'auto'
  return <img className="model-glyph" src={`/brand/glyph-${glyph}.svg`} width={size} height={size} alt="" />
}

const shapes = {
  plus: <path d="M12 5v14M5 12h14" />,
  arrow: <path d="M12 19V5M5 12l7-7 7 7" />,
  clip: <path d="m21 11-8.5 8.5a5 5 0 0 1-7-7L14 4a3.5 3.5 0 0 1 5 5l-8.5 8.5a2 2 0 0 1-3-3L15 7" />,
  copy: <><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3" /></>,
  check: <path d="m5 12 5 5 9-10" />,
  close: <path d="m6 6 12 12M18 6 6 18" />,
  globe: <><circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18" /></>,
  chevron: <path d="m6 9 6 6 6-6" />,
  user: <><circle cx="12" cy="8" r="4" /><path d="M4 21c1-4 4-6 8-6s7 2 8 6" /></>,
  lock: <><rect x="5" y="11" width="14" height="10" rx="2" /><path d="M8 11V8a4 4 0 0 1 8 0v3" /></>,
  eye: <><path d="M2 12c1-2.5 5-7 10-7s9 4.5 10 7c-1 2.5-5 7-10 7S3 14.5 2 12Z" /><circle cx="12" cy="12" r="3" /></>,
  eyeOff: <path d="m3 3 18 18M10.6 10.6a2 2 0 0 0 2.8 2.8M9.9 5.1A10 10 0 0 1 12 5c5 0 9 4.5 10 7a12 12 0 0 1-3 4.2M6.1 6.1C3.9 7.6 2.5 10 2 12c1 2.5 5 7 10 7a10 10 0 0 0 4.5-1" />,
  logout: <><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M9 12h12m-4-4 4 4-4 4" /></>,
  chat: <path d="M21 11.5a8.4 8.4 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.4 8.4 0 0 1-3.8-.9L3 21l1.9-5.7a8.4 8.4 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.4 8.4 0 0 1 3.8-.9h.5a8.5 8.5 0 0 1 8 8v.5Z" />,
  file: <><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Zm0 0v6h6M8 13h8M8 17h6" /></>,
  undo: <><path d="M9 14 4 9l5-5" /><path d="M4 9h10a6 6 0 0 1 0 12h-3" /></>,
  download: <path d="M12 4v11m-5-5 5 5 5-5M5 20h14" />,
  code: <path d="m8 8-4 4 4 4M16 8l4 4-4 4M14 5l-4 14" />,
  diagram: <><rect x="9" y="3" width="6" height="5" rx="1" /><rect x="3" y="16" width="6" height="5" rx="1" /><rect x="15" y="16" width="6" height="5" rx="1" /><path d="M12 8v4M6 16v-2a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v2" /></>,
  grid: <><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M3 10h18M3 15h18M9 4v16" /></>,
  image: <><rect x="3" y="4" width="18" height="16" rx="3" /><circle cx="9" cy="10" r="2" /><path d="m21 16-5-5-9 9" /></>,
  upload: <path d="M12 16V4m-5 5 5-5 5 5M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3" />,
  menu: <path d="M4 6h16M4 12h16M4 18h16" />,
  camera: <><path d="M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1Z" /><circle cx="12" cy="13" r="3.5" /></>,
  screen: <><rect x="3" y="4" width="18" height="12" rx="2" /><path d="M8 20h8M12 16v4" /></>,
} satisfies Record<string, ReactNode>

export function Icon({ name, size = 18 }: { name: keyof typeof shapes; size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{shapes[name]}</svg>
}
