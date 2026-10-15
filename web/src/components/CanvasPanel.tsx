import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { Highlight, themes } from 'prism-react-renderer'
import { createArtifact, deleteArtifact, getArtifact, getArtifactVersion, listArtifactVersions, restoreArtifact, saveArtifact } from '../api/artifacts'
import { ApiError, errorMessage } from '../api/client'
import type { ArtifactDto, ArtifactKind, ArtifactSummary, ArtifactVersionContent, ArtifactVersionInfo } from '../api/types'
import { CODE_LANGUAGES, DIAGRAM_STARTER, artifactFileName, changedLines, kindLabel, sourceLabel } from '../canvas'
import { DiagramView } from './DiagramView'
import { Markdown } from './Markdown'
import { Icon } from './NusaUi'
import './Canvas.css'

const MAX_TITLE = 200

const formatTime = (iso: string) => new Date(iso).toLocaleString('id-ID', { dateStyle: 'short', timeStyle: 'short' })

function downloadArtifact(name: string, text: string, kind: ArtifactKind) {
  const type = kind === 'dokumen' ? 'text/markdown;charset=utf-8' : 'text/plain;charset=utf-8'
  const url = URL.createObjectURL(new Blob([text], { type }))
  const link = document.createElement('a')
  link.href = url
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}

/** Penomoran baris untuk editor kode; digulir bersama textarea lewat ref, tanpa state per scroll. */
function CodeTextarea({ value, onChange, label }: { value: string; onChange: (next: string) => void; label: string }) {
  const gutter = useRef<HTMLPreElement>(null)
  const count = value === '' ? 1 : value.split('\n').length
  const numbers = useMemo(() => Array.from({ length: count }, (_, i) => i + 1).join('\n'), [count])
  return (
    <div className="canvas-code-edit">
      <pre ref={gutter} className="canvas-gutter" aria-hidden="true">{numbers}</pre>
      <textarea
        className="canvas-code-input"
        value={value}
        aria-label={label}
        spellCheck={false}
        wrap="off"
        onChange={(e) => onChange(e.target.value)}
        onScroll={(e) => {
          if (gutter.current) gutter.current.scrollTop = e.currentTarget.scrollTop
        }}
      />
    </div>
  )
}

/** Kode tersorot; baris yang baru/berubah dibanding versi sebelumnya diberi latar. `changed` null = tanpa sorotan. */
function CodeView({ code, language, changed }: { code: string; language: string; changed: Set<number> | null }) {
  return (
    <Highlight code={code} language={language} theme={themes.vsDark}>
      {({ tokens, getLineProps, getTokenProps }) => (
        <pre className="canvas-code-view" tabIndex={0} aria-label="Kode dengan sorotan sintaks">
          {tokens.map((line, i) => (
            <div key={i} {...getLineProps({ line })} className={changed?.has(i + 1) ? 'canvas-line is-changed' : 'canvas-line'}>
              <span className="canvas-line-no" aria-hidden="true">{i + 1}</span>
              <span>{line.map((token, k) => <span key={k} {...getTokenProps({ token })} />)}</span>
            </div>
          ))}
        </pre>
      )}
    </Highlight>
  )
}

type EditorProps = {
  summary: ArtifactSummary
  hidden: boolean
  onChanged: () => Promise<void>
  onDeleted: (id: string) => void
  onDirtyChange: (id: string, dirty: boolean) => void
}

/**
 * Satu artifact: edit, simpan (versi baru dari versi dasar yang dilihat), urungkan, riwayat, salin, unduh.
 * Tetap terpasang saat tab lain aktif supaya draf yang belum disimpan tidak hilang.
 */
function ArtifactEditor({ summary, hidden, onChanged, onDeleted, onDirtyChange }: EditorProps) {
  const [loaded, setLoaded] = useState<ArtifactDto | null>(null)
  const [draft, setDraft] = useState('')
  const [titleDraft, setTitleDraft] = useState(summary.title)
  const [mode, setMode] = useState<'edit' | 'view'>(summary.kind === 'diagram' ? 'view' : 'edit')
  const [versions, setVersions] = useState<ArtifactVersionInfo[] | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [viewing, setViewing] = useState<ArtifactVersionContent | null>(null)
  const [previous, setPrevious] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)
  const [copied, setCopied] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const dirty = loaded !== null && (draft !== loaded.content || titleDraft.trim() !== loaded.title)
  useEffect(() => onDirtyChange(summary.id, dirty), [onDirtyChange, summary.id, dirty])

  const adopt = (next: ArtifactDto) => {
    setLoaded(next)
    setDraft(next.content)
    setTitleDraft(next.title)
    setConflict(false)
    setViewing(null)
    setVersions(null)
  }

  const load = async () => {
    setError(null)
    try {
      adopt(await getArtifact(summary.id))
    } catch (e: unknown) {
      setError(errorMessage(e))
    }
  }

  useEffect(() => {
    void load()
    // Dimuat sekali per artifact (komponen di-key per id); muat ulang eksplisit lewat tombol.
    // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [summary.id])

  // Versi aktif di server maju (AI memperbarui): muat otomatis bila tidak ada edit; bila ada, jangan timpa.
  const stale = loaded !== null && summary.currentVersion > loaded.currentVersion
  useEffect(() => {
    if (stale && !dirty) void load()
    // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [stale, dirty])

  // Versi sebelumnya untuk menyorot baris kode yang berubah.
  const version = loaded?.currentVersion ?? 0
  useEffect(() => {
    if (summary.kind !== 'kode' || version < 2) {
      setPrevious(null)
      return
    }
    let cancelled = false
    getArtifactVersion(summary.id, version - 1)
      .then((v) => !cancelled && setPrevious(v.content))
      .catch(() => !cancelled && setPrevious(null))
    return () => {
      cancelled = true
    }
  }, [summary.id, summary.kind, version])

  const changed = useMemo(
    () => (previous !== null && loaded ? changedLines(previous, loaded.content) : null),
    [previous, loaded],
  )

  /** Jalankan aksi tulis; 409 konflik ditampilkan khusus, galat lain apa adanya. */
  const run = async (action: () => Promise<void>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (e: unknown) {
      if (e instanceof ApiError && e.code === 'version_conflict') setConflict(true)
      else setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const save = () =>
    loaded && dirty && !busy
      ? run(async () => {
          adopt(await saveArtifact(summary.id, loaded.currentVersion, draft, titleDraft.trim() || undefined))
          await onChanged()
        })
      : Promise.resolve()

  const undo = () => {
    if (!loaded) return
    if (dirty) {
      // Ada edit lokal: urungkan berarti membuangnya, tanpa menyentuh server.
      setDraft(loaded.content)
      setTitleDraft(loaded.title)
      return
    }
    return run(async () => {
      adopt(await restoreArtifact(summary.id, loaded.currentVersion - 1, loaded.currentVersion))
      await onChanged()
    })
  }

  const restore = (target: number) =>
    loaded &&
    run(async () => {
      adopt(await restoreArtifact(summary.id, target, loaded.currentVersion))
      await onChanged()
    })

  const toggleHistory = () => {
    const next = !historyOpen
    setHistoryOpen(next)
    setViewing(null)
    if (next && versions === null) {
      listArtifactVersions(summary.id).then(setVersions).catch((e: unknown) => setError(errorMessage(e)))
    }
  }

  const view = (target: number) =>
    getArtifactVersion(summary.id, target).then(setViewing).catch((e: unknown) => setError(errorMessage(e)))

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(viewing?.content ?? draft)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      setError('Tidak dapat menyalin ke clipboard. Pilih teks lalu salin manual.')
    }
  }

  const remove = () =>
    run(async () => {
      await deleteArtifact(summary.id)
      onDeleted(summary.id)
      await onChanged()
    })

  const onKeyDown = (e: KeyboardEvent) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
      e.preventDefault()
      void save()
    }
  }

  if (!loaded) {
    return (
      <div className="canvas-body" hidden={hidden}>
        {error ? (
          <p className="error" role="alert">{error} <button type="button" className="text-button" onClick={() => void load()}>Coba lagi</button></p>
        ) : (
          <p className="muted" role="status">Memuat…</p>
        )}
      </div>
    )
  }

  const isCode = summary.kind === 'kode'
  const isDiagram = summary.kind === 'diagram'
  const language = summary.language ?? 'text'
  const shown = viewing?.content ?? draft
  const fileName = artifactFileName(titleDraft, summary.kind, summary.language)

  return (
    // oxlint-disable-next-line jsx-a11y/no-static-element-interactions
    <div className="canvas-body" hidden={hidden} onKeyDown={onKeyDown}>
      <div className="canvas-toolbar">
        <input
          className="canvas-title"
          value={titleDraft}
          maxLength={MAX_TITLE}
          aria-label="Judul artifact"
          onChange={(e) => setTitleDraft(e.target.value)}
          disabled={viewing !== null}
        />
        <span className="canvas-version" title={`Versi aktif di server: ${loaded.currentVersion}`}>v{loaded.currentVersion}{dirty ? ' •' : ''}</span>
        <div className="canvas-actions">
          <button type="button" className="ghost-button" onClick={() => setMode(mode === 'edit' ? 'view' : 'edit')} disabled={viewing !== null}>
            {mode === 'edit' ? (isCode ? 'Sorotan' : isDiagram ? 'Hasil' : 'Pratinjau') : isDiagram ? 'Kode Mermaid' : 'Edit'}
          </button>
          <button type="button" className="ghost-button" onClick={() => void undo()} disabled={busy || viewing !== null || (!dirty && loaded.currentVersion < 2)}
            title={dirty ? 'Buang perubahan yang belum disimpan' : `Kembali ke isi v${loaded.currentVersion - 1} (dicatat sebagai versi baru)`}>
            {dirty ? 'Buang perubahan' : 'Urungkan'}
          </button>
          <button type="button" className="ghost-button" onClick={toggleHistory} aria-expanded={historyOpen}>Riwayat</button>
          <button type="button" className="icon-button" onClick={() => void copy()} aria-label={copied ? 'Tersalin' : 'Salin isi'} title={copied ? 'Tersalin' : 'Salin isi'}>
            <Icon name={copied ? 'check' : 'copy'} />
          </button>
          <button type="button" className="icon-button" onClick={() => downloadArtifact(fileName, shown, summary.kind)} aria-label={`Unduh ${fileName}`} title={`Unduh ${fileName}`}>
            <Icon name="upload" />
          </button>
          <button type="button" className="primary-button" onClick={() => void save()} disabled={!dirty || busy || viewing !== null}>
            {busy ? 'Menyimpan…' : 'Simpan'}
          </button>
        </div>
      </div>

      {error && <p className="error canvas-note" role="alert">{error}</p>}
      {conflict && (
        <div className="canvas-note canvas-conflict" role="alert">
          <span>Artifact sudah berubah di tempat lain (mis. diperbarui AI). Penyimpananmu ditolak agar tidak menimpa.</span>
          <button type="button" className="text-button" onClick={() => void copy()}>Salin edit saya</button>
          <button type="button" className="text-button" onClick={() => void load()}>Muat versi terbaru</button>
        </div>
      )}
      {stale && dirty && !conflict && (
        <div className="canvas-note canvas-conflict" role="status">
          <span>Versi terbaru di server (v{summary.currentVersion}) berbeda dari yang kamu edit.</span>
          <button type="button" className="text-button" onClick={() => void load()}>Muat versi terbaru (buang edit)</button>
        </div>
      )}

      {historyOpen && (
        <div className="canvas-history">
          {versions === null ? <p className="muted">Memuat riwayat…</p> : (
            <ul>
              {versions.map((v) => (
                <li key={v.version} className={viewing?.version === v.version ? 'is-viewing' : undefined}>
                  <span>v{v.version} · {sourceLabel(v.source)} · {formatTime(v.createdAt)} · {v.length.toLocaleString('id-ID')} karakter</span>
                  {v.version === loaded.currentVersion ? <span className="muted">aktif</span> : (
                    <button type="button" className="text-button" onClick={() => void view(v.version)}>Lihat</button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {viewing && (
        <div className="canvas-note canvas-conflict" role="status">
          <span>Melihat v{viewing.version} (hanya baca).</span>
          <button type="button" className="text-button" onClick={() => void restore(viewing.version)} disabled={busy || dirty}
            title={dirty ? 'Simpan atau buang perubahanmu dulu' : 'Salin isi versi ini sebagai versi baru'}>Pulihkan versi ini</button>
          <button type="button" className="text-button" onClick={() => setViewing(null)}>Kembali</button>
        </div>
      )}

      <div className="canvas-content">
        {viewing || mode === 'view' ? (
          isDiagram ? <DiagramView source={shown} pngName={fileName.replace(/\.mmd$/, '.png')} />
            : isCode ? <CodeView code={shown} language={language} changed={viewing ? null : changed} /> : <div className="canvas-doc"><Markdown text={shown} /></div>
        ) : isCode || isDiagram ? (
          <CodeTextarea value={draft} onChange={setDraft} label={isDiagram ? 'Kode Mermaid' : `Kode ${language}`} />
        ) : (
          <textarea className="canvas-text" value={draft} onChange={(e) => setDraft(e.target.value)} aria-label="Isi dokumen (Markdown)" spellCheck />
        )}
      </div>

      <footer className="canvas-foot">
        <span className="muted">{isCode ? `Kode · ${language}` : isDiagram ? 'Diagram · Mermaid' : 'Dokumen · Markdown'} · {shown.length.toLocaleString('id-ID')} karakter</span>
        {confirmDelete ? (
          <span className="canvas-confirm">
            Hapus artifact beserta semua versinya?
            <button type="button" className="text-button" onClick={() => void remove()} disabled={busy}>Ya, hapus</button>
            <button type="button" className="text-button" onClick={() => setConfirmDelete(false)}>Batal</button>
          </span>
        ) : (
          <button type="button" className="text-button" onClick={() => setConfirmDelete(true)}>Hapus</button>
        )}
      </footer>
    </div>
  )
}

type CanvasPanelProps = {
  conversationId: string
  artifacts: ArtifactSummary[]
  activeId: string | null
  open: boolean
  onSelect: (id: string | null) => void
  onClose: () => void
  /** Muat ulang daftar artifact (setelah buat/simpan/hapus). */
  onChanged: () => Promise<void>
}

/**
 * Panel Canvas di samping chat. Tetap terpasang saat disembunyikan (`open=false`) supaya draf yang belum disimpan
 * tidak hilang; peringatan keluar halaman aktif selama ada draf kotor.
 */
export function CanvasPanel({ conversationId, artifacts, activeId, open, onSelect, onClose, onChanged }: CanvasPanelProps) {
  const [visited, setVisited] = useState<ReadonlySet<string>>(new Set())
  const [dirtyIds, setDirtyIds] = useState<ReadonlySet<string>>(new Set())
  const [language, setLanguage] = useState<string>('text')
  const [creating, setCreating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const active = artifacts.find((a) => a.id === activeId) ?? null

  // Isi baru diambil saat panel benar-benar dibuka, bukan saat daftar artifact dimuat. Disesuaikan saat render
  // (pola resmi React untuk state turunan), bukan lewat efek.
  if (open && activeId && !visited.has(activeId)) setVisited(new Set(visited).add(activeId))

  // Tanpa artifact terpilih tetapi ada daftar: pilih yang pertama supaya panel tidak kosong.
  useEffect(() => {
    if (open && !active && artifacts.length > 0) onSelect(artifacts[0].id)
  }, [open, active, artifacts, onSelect])

  useEffect(() => {
    if (dirtyIds.size === 0) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirtyIds])

  const onDirtyChange = useCallback(
    (id: string, dirty: boolean) =>
      setDirtyIds((prev) => {
        if (prev.has(id) === dirty) return prev
        const next = new Set(prev)
        if (dirty) next.add(id)
        else next.delete(id)
        return next
      }),
    [],
  )

  const create = async (kind: ArtifactKind) => {
    setCreating(true)
    setError(null)
    try {
      const made = await createArtifact(conversationId, {
        kind,
        title: kind === 'dokumen' ? 'Dokumen baru' : kind === 'diagram' ? 'Diagram baru' : 'Kode baru',
        language: kind === 'kode' ? language : null,
        content: kind === 'diagram' ? DIAGRAM_STARTER : '',
      })
      await onChanged()
      onSelect(made.id)
    } catch (e: unknown) {
      setError(errorMessage(e))
    } finally {
      setCreating(false)
    }
  }

  const onDeleted = (id: string) => {
    setVisited((prev) => {
      const next = new Set(prev)
      next.delete(id)
      return next
    })
    onDirtyChange(id, false)
    onSelect(artifacts.find((a) => a.id !== id)?.id ?? null)
  }

  return (
    <aside className="canvas" aria-label="Canvas" hidden={!open}>
      <header className="canvas-head">
        <div className="canvas-tabs" role="tablist" aria-label="Artifact">
          {artifacts.map((a) => (
            <button key={a.id} type="button" role="tab" aria-selected={a.id === activeId} className="canvas-tab" onClick={() => onSelect(a.id)} title={a.title}>
              <span className="canvas-tab-kind">{kindLabel(a.kind)}</span>
              <span className="canvas-tab-title">{a.title}</span>
              {dirtyIds.has(a.id) && <span className="canvas-dirty" role="img" aria-label="Belum disimpan">•</span>}
            </button>
          ))}
        </div>
        <button type="button" className="icon-button" onClick={onClose} aria-label="Tutup Canvas" title="Tutup Canvas">
          <Icon name="close" />
        </button>
      </header>
      <div className="canvas-new">
        <button type="button" className="ghost-button" onClick={() => void create('dokumen')} disabled={creating}>+ Dokumen</button>
        <button type="button" className="ghost-button" onClick={() => void create('diagram')} disabled={creating}>+ Diagram</button>
        <button type="button" className="ghost-button" onClick={() => void create('kode')} disabled={creating}>+ Kode</button>
        <select className="canvas-lang" value={language} onChange={(e) => setLanguage(e.target.value)} aria-label="Bahasa untuk artifact kode baru">
          {CODE_LANGUAGES.map((l) => <option key={l} value={l}>{l}</option>)}
        </select>
        {error && <span className="error" role="alert">{error}</span>}
      </div>
      {artifacts.length === 0 && (
        <p className="muted canvas-empty">Belum ada artifact. Minta AI menulis dokumen atau kode, atau buat sendiri dengan tombol di atas.</p>
      )}
      {artifacts.filter((a) => visited.has(a.id)).map((a) => (
        <ArtifactEditor
          key={a.id}
          summary={a}
          hidden={a.id !== activeId}
          onChanged={onChanged}
          onDeleted={onDeleted}
          onDirtyChange={onDirtyChange}
        />
      ))}
    </aside>
  )
}
