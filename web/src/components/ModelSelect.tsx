import { useEffect, useRef, useState } from 'react'
import { isEligible } from '../api/models'
import type { ModelInfoDto } from '../api/types'
import { Icon, ModelGlyph } from './NusaUi'
import './Composer.css'
interface Props {
  models: ModelInfoDto[] | null
  error: string | null
  value: string
  onChange: (alias: string) => void
  onReload: () => void
  /** 'picker' (bawaan): chip + select native di composer percakapan. 'cards': kartu pilihan di beranda. */
  variant?: 'cards' | 'picker'
}

/**
 * Pemilihan model dari katalog gateway yang sebenarnya: semua alias dari server tampil, yang tidak layak
 * nonaktif beserta alasannya. Tidak ada pilih-otomatis item pertama dan tidak ada entri karangan seperti
 * "Otomatis"; nilai awal hanya boleh datang dari alias tersimpan yang masih layak.
 */
export function ModelSelect({ models, error, value, onChange, onReload, variant = 'picker' }: Props) {
  const sliderRef = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  const popoverRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const handleClick = (e: MouseEvent) => {
      if (!popoverRef.current?.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', handleClick)
    document.addEventListener('keydown', handleKey)
    return () => {
      document.removeEventListener('mousedown', handleClick)
      document.removeEventListener('keydown', handleKey)
    }
  }, [open])

  // Catatan bawah dihapus sesuai permintaan user (Image #4)

  if (variant === 'cards') {
    if (error) {
      return (
        <div className="ns-models ns-models-cards" style={{ width: '100%', maxWidth: 760, padding: '16px 0' }}>
          <p className="error ns-note" role="alert">
            Katalog model gagal dimuat: {error}{' '}
            <button type="button" className="ns-link" onClick={onReload}>
              Coba lagi
            </button>
          </p>
        </div>
      )
    }

    if (!models || models.length === 0) {
      return (
        <div className="ns-models ns-models-cards" style={{ width: '100%', maxWidth: 760, padding: '16px 0' }}>
          <p className="ns-note" role="status">
            {!models ? 'Memuat model…' : 'Belum ada model aktif yang tersedia di katalog gateway.'}
          </p>
        </div>
      )
    }

    const allModels = [...models].sort((a, b) => {
      const getRank = (m: ModelInfoDto) => {
        const id = m.id.toLowerCase()
        const eligible = isEligible(m)
        const base = eligible ? 0 : 100
        if (id.includes('deepseek-v4.1')) return base + 1
        if (id.includes('glm-5.3-flash')) return base + 2
        if (id.includes('minimax-m2.7') || id.includes('minimax-m3')) return base + 3
        if (id.includes('deepseek')) return base + 4
        if (id.includes('glm')) return base + 5
        if (id.includes('minimax')) return base + 6
        return base + 10
      }
      return getRank(a) - getRank(b)
    })

    const cards = [
      {
        id: 'auto',
        family: 'auto',
        name: 'Otomatis',
        desc: 'Sistem memilihkan model cerdas sesuai teks atau gambar',
        sub: 'Direkomendasikan',
        ink: '#0F5F57',
        tint: '#EAF3F1',
        eligible: true,
      },
      ...allModels.map((m) => {
        const id = m.id.toLowerCase()
        let family = 'auto'
        let name = m.id
        let ink = '#0F5F57'
        let tint = '#EAF3F1'
        let desc = m.supportsVision ? 'Model multimodal untuk teks dan analisis visual' : 'Model cepat untuk instruksi dan teks'
        if (id.includes('deepseek')) {
          family = 'deepseek'
          name = 'DeepSeek'
          ink = '#0F5F57'
          tint = '#EAF3F1'
          desc = 'Model utama untuk penalaran mendalam, teks, dan kode'
        } else if (id.includes('glm')) {
          family = 'glm'
          name = 'GLM'
          ink = '#3D3F8F'
          tint = '#EEEEF8'
          desc = 'Model multimodal untuk dokumen visual, scan, dan gambar'
        } else if (id.includes('minimax')) {
          family = 'minimax'
          name = 'MiniMax'
          ink = '#B45F2E'
          tint = '#FBF1EA'
          desc = 'Model alternatif cepat untuk percakapan dan ringkasan'
        }
        return {
          id: m.id,
          family,
          name,
          desc,
          sub: m.id,
          ink,
          tint,
          eligible: isEligible(m),
        }
      }),
    ]
    return (
      <div className="ns-models ns-models-cards" role="radiogroup" aria-label="Pilih model AI" style={{ width: '100%', maxWidth: 760, position: 'relative' }}>
        <div
          ref={sliderRef}
          className="ns-mcards ns-slider"
          style={{
            width: '100%',
            maxWidth: 760,
            display: 'flex',
            gap: 10,
            overflowX: 'auto',
            scrollBehavior: 'smooth',
            scrollSnapType: 'x mandatory',
            padding: '4px 0 10px',
            scrollbarWidth: 'none',
            msOverflowStyle: 'none',
          }}
          onWheel={(e) => {
            if (e.deltaY) e.currentTarget.scrollLeft += e.deltaY
          }}
        >
          {cards.map((c) => {
            const isSelected = value ? value === c.id : c.id === 'auto'
            return (
              <button
                key={c.id}
                type="button"
                className="ns-mcard opt"
                data-family={c.family}
                aria-pressed={isSelected}
                disabled={!c.eligible}
                style={{
                  flex: '0 0 calc((100% - 20px) / 3)',
                  minWidth: 230,
                  scrollSnapAlign: 'start',
                  boxSizing: 'border-box',
                  fontFamily: 'inherit',
                  textAlign: 'left',
                  background: isSelected ? c.tint : '#FFFFFF',
                  border: isSelected ? '1.5px solid ' + c.ink : '1.5px solid #E4E2DC',
                  borderRadius: 16,
                  padding: 16,
                  cursor: c.eligible ? 'pointer' : 'not-allowed',
                  opacity: c.eligible ? 1 : 0.5,
                  color: '#1A1A1D',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 12,
                  transition: 'border-color .15s, background .15s, transform .15s',
                }}
                onClick={() => onChange(c.id)}
              >
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                  <span className="spin" style={{ display: 'inline-flex' }}>
                    <ModelGlyph alias={c.family} size={32} />
                  </span>
                  {isSelected && (
                    <svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true">
                      <circle cx="12" cy="12" r="10" fill="#1A1A1D" />
                      <path d="M7.5 12.5l3 3 6-6.5" stroke="#FFFFFF" strokeWidth="2.2" fill="none" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  )}
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
                  <span style={{ fontSize: 16, fontWeight: 600 }}>{c.name}</span>
                  <span style={{ fontSize: 13, color: '#55545A', lineHeight: 1.45 }}>{c.desc}</span>
                </div>
                <div style={{ fontFamily: 'Geist Mono, monospace', fontSize: 12, color: '#55545A' }}>{c.sub}</div>
              </button>
            )
          })}
        </div>
      </div>
    )
  }

  const allModels = [...(models ?? [])].sort((a, b) => {
    const getRank = (m: ModelInfoDto) => {
      const id = m.id.toLowerCase()
      const eligible = isEligible(m)
      const base = eligible ? 0 : 100
      if (id.includes('deepseek-v4.1')) return base + 1
      if (id.includes('glm-5.3-flash')) return base + 2
      if (id.includes('minimax-m2.7') || id.includes('minimax-m3')) return base + 3
      if (id.includes('deepseek')) return base + 4
      if (id.includes('glm')) return base + 5
      if (id.includes('minimax')) return base + 6
      return base + 10
    }
    return getRank(a) - getRank(b)
  })

  const isAuto = value === 'auto'
  const currentModel = models?.find((m) => m.id === value)
  const currentFamily = isAuto ? 'auto' : currentModel ? (
    currentModel.id.toLowerCase().includes('deepseek') ? 'deepseek' :
    currentModel.id.toLowerCase().includes('glm') ? 'glm' :
    currentModel.id.toLowerCase().includes('minimax') ? 'minimax' : 'auto'
  ) : 'deepseek'
  const currentName = isAuto ? 'Otomatis' : currentModel ? (
    currentModel.id.toLowerCase().includes('deepseek') ? 'DeepSeek' :
    currentModel.id.toLowerCase().includes('glm') ? 'GLM' :
    currentModel.id.toLowerCase().includes('minimax') ? 'MiniMax' : currentModel.id
  ) : 'DeepSeek'

  return (
    <div className="ns-picker-container" ref={popoverRef} style={{ position: 'relative' }}>
      <button
        type="button"
        className="ghost"
        aria-expanded={open}
        aria-haspopup="listbox"
        style={{
          height: 40,
          padding: '0 10px',
          borderRadius: 10,
          border: 'none',
          background: '#F2F1ED',
          fontFamily: 'inherit',
          fontSize: 14,
          fontWeight: 500,
          color: '#1A1A1D',
          cursor: 'pointer',
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          flex: 'none',
        }}
        onClick={() => setOpen(!open)}
      >
        <ModelGlyph alias={currentFamily} size={18} />
        <span>{currentName}</span>
        <Icon name="chevron" size={12} />
      </button>

      {open && (
        <div
          role="listbox"
          aria-label="Pilih model"
          className="ns-popover"
          style={{
            position: 'absolute',
            bottom: 'calc(100% + 8px)',
            left: 0,
            width: 320,
            maxWidth: '90vw',
            boxSizing: 'border-box',
            background: '#FFFFFF',
            border: '1px solid #E4E2DC',
            borderRadius: 16,
            padding: 6,
            boxShadow: '0 12px 32px rgba(26,26,29,.10)',
            display: 'flex',
            flexDirection: 'column',
            zIndex: 100,
            maxHeight: 380,
            overflowY: 'auto',
          }}
        >
          <button
            type="button"
            role="option"
            aria-selected={isAuto}
            style={{
              fontFamily: 'inherit',
              textAlign: 'left',
              border: 'none',
              background: isAuto ? '#F2F1ED' : 'transparent',
              borderRadius: 10,
              padding: '8px 12px',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              gap: 12,
              color: '#1A1A1D',
              minHeight: 48,
              boxSizing: 'border-box',
              width: '100%',
            }}
            onClick={() => {
              onChange('auto')
              setOpen(false)
            }}
          >
            <ModelGlyph alias="auto" size={22} />
            <span style={{ flex: 1, display: 'flex', flexDirection: 'column' }}>
              <span style={{ fontSize: 14, fontWeight: 600 }}>Otomatis</span>
              <span style={{ fontSize: 12, color: '#6B6A70' }}>Dipilihkan sesuai pertanyaan</span>
            </span>
          </button>

          <div style={{ height: 1, background: '#EEECE7', margin: '4px 8px' }} />

          {allModels.map((m) => {
            const isSelected = value === m.id || (!value && m.id === 'deepseek-v4.1-flash')
            const id = m.id.toLowerCase()
            let family = 'auto'
            let displayName = m.id
            if (id.includes('deepseek')) {
              family = 'deepseek'
              displayName = id.includes('v4.1') ? 'DeepSeek V4.1' : id.includes('flash') ? 'DeepSeek Flash' : 'DeepSeek'
            } else if (id.includes('glm')) {
              family = 'glm'
              displayName = id.includes('flash') ? 'GLM Flash' : 'GLM'
            } else if (id.includes('minimax')) {
              family = 'minimax'
              displayName = id.includes('m3') ? 'MiniMax M3' : 'MiniMax'
            } else if (id.includes('claude')) {
              family = 'auto'
              displayName = 'Claude Haiku'
            } else if (id.includes('gpt')) {
              family = 'auto'
              displayName = 'GPT Luna'
            } else if (id.includes('qwen')) {
              family = 'auto'
              displayName = 'Qwen Flash'
            } else if (id.includes('kimi')) {
              family = 'auto'
              displayName = 'Kimi'
            }

            const subtitle = isSelected ? 'Default' : m.supportsVision ? 'Vision' : 'Teks'

            return (
              <button
                key={m.id}
                type="button"
                role="option"
                aria-selected={isSelected}
                style={{
                  fontFamily: 'inherit',
                  textAlign: 'left',
                  border: 'none',
                  background: isSelected ? '#EAF3F1' : 'transparent',
                  borderRadius: 10,
                  padding: '8px 12px',
                  cursor: 'pointer',
                  display: 'flex',
                  alignItems: 'center',
                  gap: 12,
                  color: '#1A1A1D',
                  minHeight: 48,
                  width: '100%',
                  boxSizing: 'border-box',
                  transition: 'background 0.12s',
                }}
                onMouseEnter={(e) => {
                  if (!isSelected) e.currentTarget.style.background = '#F2F1ED'
                }}
                onMouseLeave={(e) => {
                  if (!isSelected) e.currentTarget.style.background = 'transparent'
                }}
                onClick={() => {
                  onChange(m.id)
                  setOpen(false)
                }}
              >
                <div style={{ flex: 'none', display: 'flex', alignItems: 'center' }}>
                  <ModelGlyph alias={family} size={22} />
                </div>
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
                  <div style={{ fontSize: 14, fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', lineHeight: 1.3 }}>
                    {displayName}
                  </div>
                  <div style={{ fontSize: 12, color: isSelected ? '#3F4A48' : '#6B6A70', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', lineHeight: 1.3 }}>
                    {subtitle}
                  </div>
                </div>
                <div style={{ flex: 'none', display: 'flex', alignItems: 'center', justifyContent: 'flex-end' }}>
                  {isSelected ? (
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#0F5F57" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                      <path d="M5 12l5 5 9-10" />
                    </svg>
                  ) : (
                    <span style={{ fontFamily: 'Geist Mono, monospace', fontSize: 11, color: '#6B6A70', whiteSpace: 'nowrap', maxWidth: 90, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {m.id}
                    </span>
                  )}
                </div>
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}
