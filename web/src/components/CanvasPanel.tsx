import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { Highlight, themes } from 'prism-react-renderer'
import { createArtifact, deleteArtifact, getArtifact, getArtifactVersion, listArtifactVersions, restoreArtifact, saveArtifact } from '../api/artifacts'
import { ApiError, errorMessage } from '../api/client'
import type { ArtifactDto, ArtifactKind, ArtifactSummary, ArtifactVersionContent, ArtifactVersionInfo } from '../api/types'
import { CODE_LANGUAGES, DIAGRAM_STARTER, MAX_SELECTION_CHARS, SELECTION_ACTIONS, artifactFileName, buildSelectionPrompt, changedLines, kindLabel, sourceLabel } from '../canvas'
import type { SelectionAction } from '../canvas'
import { DiagramView } from './DiagramView'
import { GenerateImageForm, ImageGallery, VariationForm } from './ImageStudio'
import { ImageView } from './ImageView'
import { SandboxPreview } from './SandboxPreview'
import { SpreadsheetView } from './SpreadsheetView'
import { STARTER_CONTENT } from '../spreadsheet'
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
function CodeTextarea({ value, onChange, onSelectionChange, label }: { value: string; onChange: (next: string) => void; onSelectionChange?: (start: number, end: number) => void; label: string }) {
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
        onSelect={(e) => onSelectionChange?.(e.currentTarget.selectionStart, e.currentTarget.selectionEnd)}
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
  /** Kirim pesan ke AI lewat chat percakapan ini (toolbar teks terpilih). */
  onAskAi: (prompt: string) => Promise<void>
  aiBusy: boolean
  /** Judul sumber bila gambar ini variasi (null bila bukan atau sumbernya sudah dihapus). */
  sourceTitle: string | null
  /** Minta AI membuat gambar/variasi (menandai skeleton galeri sebanyak `count`). */
  onAskImage: (prompt: string, count: number) => Promise<void>
}

/**
 * Satu artifact: edit, simpan (versi baru dari versi dasar yang dilihat), urungkan, riwayat, salin, unduh.
 * Tetap terpasang saat tab lain aktif supaya draf yang belum disimpan tidak hilang.
 */
function ArtifactEditor({ summary, hidden, onChanged, onDeleted, onDirtyChange, onAskAi, aiBusy, sourceTitle, onAskImage }: EditorProps) {
  const [loaded, setLoaded] = useState<ArtifactDto | null>(null)
  const [draft, setDraft] = useState('')
  const [titleDraft, setTitleDraft] = useState(summary.title)
  const [mode, setMode] = useState<'edit' | 'view' | 'preview'>(summary.kind === 'diagram' || summary.kind === 'gambar' ? 'view' : 'edit')
  const [versions, setVersions] = useState<ArtifactVersionInfo[] | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [viewing, setViewing] = useState<ArtifactVersionContent | null>(null)
  const [previous, setPrevious] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)
  const [copied, setCopied] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [selection, setSelection] = useState<{ start: number; end: number } | null>(null)
  const [asking, setAsking] = useState<SelectionAction | null>(null)
  const [question, setQuestion] = useState('')
  const [aiNote, setAiNote] = useState<string | null>(null)
  const [variationOpen, setVariationOpen] = useState(false)

  const dirty = loaded !== null && (draft !== loaded.content || titleDraft.trim() !== loaded.title)
  useEffect(() => onDirtyChange(summary.id, dirty), [onDirtyChange, summary.id, dirty])

  const [sheetKey, setSheetKey] = useState(0)

  /** `remount`: spreadsheet dibangun ulang dari isi baru; false setelah simpan, supaya kursor/seleksi tidak lepas. */
  const adopt = (next: ArtifactDto, remount = true) => {
    if (remount) setSheetKey((k) => k + 1)
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
          adopt(await saveArtifact(summary.id, loaded.currentVersion, draft, titleDraft.trim() || undefined), false)
          await onChanged()
        })
      : Promise.resolve()

  const undo = () => {
    if (!loaded) return
    if (dirty) {
      // Ada edit lokal: urungkan berarti membuangnya, tanpa menyentuh server.
      setDraft(loaded.content)
      setTitleDraft(loaded.title)
      setSheetKey((k) => k + 1)
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

  const selectionActions = SELECTION_ACTIONS[summary.kind] ?? []
  const selectedText = selection && selection.end > selection.start ? draft.slice(selection.start, selection.end) : ''

  const askAi = async (action: SelectionAction) => {
    if (!loaded || selectedText === '') return
    if (action.needsQuestion && asking?.key !== action.key) {
      setAsking(action)
      return
    }
    if (action.needsQuestion && question.trim() === '') return
    setBusy(true)
    setError(null)
    setAiNote(null)
    try {
      await onAskAi(buildSelectionPrompt({ action, title: loaded.title, artifactId: summary.id, version: loaded.currentVersion, text: selectedText, question }))
      setAiNote(action.edits ? 'Permintaan dikirim ke chat. Hasilnya muncul sebagai versi baru setelah AI selesai.' : 'Permintaan dikirim ke chat. Jawabannya muncul di chat.')
      setAsking(null)
      setQuestion('')
      setSelection(null)
    } catch (e: unknown) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

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
  const isSheet = summary.kind === 'spreadsheet'
  const isImage = summary.kind === 'gambar'
  const isHtml = isCode && summary.language === 'html'
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
          {!isSheet && <button type="button" className="ghost-button" onClick={() => setMode(mode === 'edit' ? 'view' : 'edit')} disabled={viewing !== null}>
            {mode === 'edit' ? (isCode ? 'Sorotan' : isDiagram || isImage ? 'Hasil' : 'Pratinjau') : isDiagram && mode === 'view' ? 'Kode Mermaid' : isImage && mode === 'view' ? 'Kode SVG' : 'Edit'}
          </button>}
          {isHtml && (
            <button type="button" className="ghost-button" aria-pressed={mode === 'preview'} onClick={() => setMode(mode === 'preview' ? 'edit' : 'preview')} disabled={viewing !== null}>
              Pratinjau
            </button>
          )}
          <button type="button" className="ghost-button" onClick={() => void undo()} disabled={busy || viewing !== null || (!dirty && loaded.currentVersion < 2)}
            title={dirty ? 'Buang perubahan yang belum disimpan' : `Kembali ke isi v${loaded.currentVersion - 1} (dicatat sebagai versi baru)`}>
            {dirty ? 'Buang perubahan' : 'Urungkan'}
          </button>
          {isImage && (
            <button type="button" className="ghost-button" onClick={() => setVariationOpen((o) => !o)} aria-expanded={variationOpen} disabled={viewing !== null}>Variasi…</button>
          )}
          <button type="button" className="ghost-button" onClick={toggleHistory} aria-expanded={historyOpen}>Riwayat</button>
          {!isSheet && (
            <>
              <button type="button" className="icon-button" onClick={() => void copy()} aria-label={copied ? 'Tersalin' : 'Salin isi'} title={copied ? 'Tersalin' : 'Salin isi'}>
                <Icon name={copied ? 'check' : 'copy'} />
              </button>
              <button type="button" className="icon-button" onClick={() => downloadArtifact(fileName, shown, summary.kind)} aria-label={`Unduh ${fileName}`} title={`Unduh ${fileName}`}>
                <Icon name="upload" />
              </button>
            </>
          )}
          <button type="button" className="primary-button" onClick={() => void save()} disabled={!dirty || busy || viewing !== null}>
            {busy ? 'Menyimpan…' : 'Simpan'}
          </button>
        </div>
      </div>

      {error && <p className="error canvas-note" role="alert">{error}</p>}
      {selectionActions.length > 0 && mode === 'edit' && viewing === null && selectedText !== '' && (
        <div className="canvas-note canvas-ai-bar" role="group" aria-label="Aksi AI untuk teks terpilih">
          <span className="muted">Terpilih {selectedText.length.toLocaleString('id-ID')} karakter</span>
          {selectionActions.map((a) => (
            <button key={a.key} type="button" className="ghost-button" onClick={() => void askAi(a)}
              disabled={busy || aiBusy || dirty || selectedText.length > MAX_SELECTION_CHARS}
              title={dirty ? 'Simpan dulu supaya AI membaca isi yang sama denganmu' : aiBusy ? 'AI sedang menjawab di chat' : undefined}>
              {a.label}
            </button>
          ))}
          {asking && (
            <form className="canvas-ai-ask" onSubmit={(e) => { e.preventDefault(); void askAi(asking) }}>
              <input value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="Tulis pertanyaanmu" aria-label="Pertanyaan untuk AI" maxLength={500} />
              <button type="submit" className="ghost-button" disabled={busy || aiBusy || question.trim() === ''}>Kirim</button>
            </form>
          )}
          {selectedText.length > MAX_SELECTION_CHARS && <span className="error">Pilihan terlalu panjang (maksimal {MAX_SELECTION_CHARS.toLocaleString('id-ID')} karakter).</span>}
          {dirty && <span className="muted">Simpan dulu agar AI membaca isi yang sama.</span>}
        </div>
      )}
      {aiNote && <p className="canvas-note muted" role="status">{aiNote}</p>}
      {isImage && summary.derivedFromId && (
        <p className="canvas-lineage muted">Variasi dari {sourceTitle ? `“${sourceTitle}”` : 'sumber yang sudah dihapus'}{summary.derivedFromVersion ? ` (v${summary.derivedFromVersion})` : ''}.</p>
      )}
      {isImage && variationOpen && viewing === null && (
        dirty
          ? <p className="canvas-note muted" role="status">Simpan dulu perubahanmu agar AI membuat variasi dari isi yang sama.</p>
          : <VariationForm artifact={{ id: summary.id, title: loaded.title }} version={loaded.currentVersion} busy={aiBusy} onSubmit={async (prompt, count) => { await onAskImage(prompt, count); setVariationOpen(false) }} />
      )}
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
        {isSheet ? (
          <SpreadsheetView key={viewing ? `v${viewing.version}` : `c${sheetKey}`} value={viewing?.content ?? draft} readOnly={viewing !== null} title={titleDraft} onChange={setDraft} />
        ) : isHtml && mode === 'preview' && !viewing ? (
          <SandboxPreview source={draft} />
        ) : viewing || mode === 'view' ? (
          isImage ? <ImageView source={shown} baseName={fileName.replace(/\.svg$/, '')} />
            : isDiagram ? <DiagramView source={shown} pngName={fileName.replace(/\.mmd$/, '.png')} />
            : isCode ? <CodeView code={shown} language={language} changed={viewing ? null : changed} /> : <div className="canvas-doc"><Markdown text={shown} /></div>
        ) : isCode || isDiagram || isImage ? (
          <CodeTextarea value={draft} onChange={setDraft} onSelectionChange={(start, end) => setSelection({ start, end })} label={isDiagram ? 'Kode Mermaid' : isImage ? 'Kode SVG' : `Kode ${language}`} />
        ) : (
          <textarea className="canvas-text" value={draft} onChange={(e) => setDraft(e.target.value)} onSelect={(e) => setSelection({ start: e.currentTarget.selectionStart, end: e.currentTarget.selectionEnd })} aria-label="Isi dokumen (Markdown)" spellCheck />
        )}
      </div>

      <footer className="canvas-foot">
        <span className="muted">{isCode ? `Kode · ${language}` : isDiagram ? 'Diagram · Mermaid' : isImage ? 'Gambar · SVG vektor' : isSheet ? 'Spreadsheet · Fortune-sheet' : 'Dokumen · Markdown'} · {shown.length.toLocaleString('id-ID')} karakter</span>
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

/** Urutan tombol "buat baru". */
const NEW_KINDS: ArtifactKind[] = ['dokumen', 'diagram', 'gambar', 'spreadsheet', 'kode']

const NEW_LABEL: Record<ArtifactKind, string> = { dokumen: 'Dokumen', diagram: 'Diagram', gambar: 'Gambar (AI)', spreadsheet: 'Spreadsheet', kode: 'Kode' }

type CanvasPanelProps = {
  conversationId: string
  artifacts: ArtifactSummary[]
  activeId: string | null
  open: boolean
  onSelect: (id: string | null) => void
  onClose: () => void
  /** Muat ulang daftar artifact (setelah buat/simpan/hapus). */
  onChanged: () => Promise<void>
  /** Kirim pesan ke AI lewat chat percakapan ini; ditolak (melempar) bila belum ada model atau pengiriman gagal. */
  onAskAi: (prompt: string) => Promise<void>
  /** Ada giliran AI utama yang berjalan: aksi AI ditahan dulu. */
  aiBusy: boolean
}

/**
 * Panel Canvas di samping chat. Tetap terpasang saat disembunyikan (`open=false`) supaya draf yang belum disimpan
 * tidak hilang; peringatan keluar halaman aktif selama ada draf kotor.
 */
export function CanvasPanel({ conversationId, artifacts, activeId, open, onSelect, onClose, onChanged, onAskAi, aiBusy }: CanvasPanelProps) {
  const [visited, setVisited] = useState<ReadonlySet<string>>(new Set())
  const [showOtherKinds, setShowOtherKinds] = useState(false)
  const [dirtyIds, setDirtyIds] = useState<ReadonlySet<string>>(new Set())
  const [language, setLanguage] = useState<string>('text')
  const [creating, setCreating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showGenerate, setShowGenerate] = useState(false)
  const [showGallery, setShowGallery] = useState(false)
  // Gambar yang sedang dibuat AI: skeleton galeri selama tiket chat berjalan, bukan animasi buatan.
  const [pending, setPending] = useState<{ count: number; baseline: number } | null>(null)
  const sawBusy = useRef(false)
  const images = useMemo(() => artifacts.filter((a) => a.kind === 'gambar'), [artifacts])

  useEffect(() => {
    if (!pending) {
      sawBusy.current = false
      return
    }
    if (aiBusy) {
      sawBusy.current = true
      return
    }
    if (!sawBusy.current) return
    // Tiket selesai: hilangkan skeleton begitu gambar baru muncul di daftar (atau setelah jeda singkat bila tidak ada).
    const timer = setTimeout(() => setPending(null), images.length > pending.baseline ? 0 : 4000)
    return () => clearTimeout(timer)
  }, [pending, aiBusy, images.length])

  const askImage = async (prompt: string, count: number) => {
    await onAskAi(prompt)
    setPending({ count, baseline: images.length })
    setShowGenerate(false)
    setShowGallery(true)
  }

  const active = artifacts.find((a) => a.id === activeId) ?? null

  // Isi baru diambil saat panel benar-benar dibuka, bukan saat daftar artifact dimuat. Disesuaikan saat render
  // (pola resmi React untuk state turunan), bukan lewat efek.
  if (open && activeId && !visited.has(activeId)) setVisited(new Set(visited).add(activeId))

  // Tanpa artifact terpilih tetapi ada daftar: pilih yang pertama supaya panel tidak kosong.
  useEffect(() => {
    if (open && !active && artifacts.length > 0) onSelect(artifacts[0].id)
  }, [open, active, artifacts, onSelect])

  // Tab aktif yang di luar jangkauan strip (banyak artifact) digulir ke terlihat.
  useEffect(() => {
    document.querySelector('.canvas-tab[aria-selected="true"]')?.scrollIntoView?.({ inline: 'nearest', block: 'nearest' })
  }, [activeId, artifacts.length])

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
        title: kind === 'dokumen' ? 'Dokumen baru' : kind === 'diagram' ? 'Diagram baru' : kind === 'spreadsheet' ? 'Spreadsheet baru' : 'Kode baru',
        language: kind === 'kode' ? language : null,
        content: kind === 'diagram' ? DIAGRAM_STARTER : kind === 'spreadsheet' ? STARTER_CONTENT : '',
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

  // Tombol "buat baru" mengikuti konteks: artifact yang sedang dibuka menentukan jenis yang ditawarkan (diagram → hanya
  // diagram). Tanpa artifact terpilih semua jenis tampil; jenis lain bisa dibuka lewat "Jenis lain…".
  const shownKinds: ArtifactKind[] = !active || showOtherKinds ? NEW_KINDS : [active.kind]

  const newButton = (kind: ArtifactKind) => {
    if (kind === 'gambar') {
      return (
        <span key={kind} className="canvas-new-group">
          <button type="button" className="ghost-button" onClick={() => setShowGenerate((o) => !o)} aria-expanded={showGenerate}>+ Gambar (AI)</button>
          {(images.length > 0 || pending) && (
            <button type="button" className="ghost-button" onClick={() => setShowGallery((o) => !o)} aria-pressed={showGallery}>Galeri ({images.length})</button>
          )}
        </span>
      )
    }
    return (
      <button key={kind} type="button" className="ghost-button" onClick={() => void create(kind)} disabled={creating}>
        + {NEW_LABEL[kind]}
      </button>
    )
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
        {NEW_KINDS.filter((k) => shownKinds.includes(k)).map((k) => newButton(k))}
        {shownKinds.includes('kode') && (
          <select className="canvas-lang" value={language} onChange={(e) => setLanguage(e.target.value)} aria-label="Bahasa untuk artifact kode baru">
            {CODE_LANGUAGES.map((l) => <option key={l} value={l}>{l}</option>)}
          </select>
        )}
        {active && (
          <button type="button" className="text-button" onClick={() => setShowOtherKinds((o) => !o)} aria-expanded={showOtherKinds}>
            {showOtherKinds ? 'Sembunyikan jenis lain' : 'Jenis lain…'}
          </button>
        )}
        {error && <span className="error" role="alert">{error}</span>}
      </div>
      {showGenerate && <GenerateImageForm busy={aiBusy} onSubmit={askImage} />}
      {showGallery && (
        <ImageGallery images={images} all={artifacts} pending={pending && (aiBusy || images.length <= pending.baseline) ? pending.count : 0}
          onOpen={(id) => { onSelect(id); setShowGallery(false) }} />
      )}
      {artifacts.length === 0 && !showGenerate && (
        <p className="muted canvas-empty">Belum ada artifact. Minta AI menulis dokumen atau kode, atau buat sendiri dengan tombol di atas.</p>
      )}
      {artifacts.filter((a) => visited.has(a.id)).map((a) => (
        <ArtifactEditor
          key={a.id}
          summary={a}
          hidden={a.id !== activeId || showGallery}
          onChanged={onChanged}
          onDeleted={onDeleted}
          onDirtyChange={onDirtyChange}
          onAskAi={onAskAi}
          aiBusy={aiBusy}
          sourceTitle={a.derivedFromId ? (artifacts.find((x) => x.id === a.derivedFromId)?.title ?? null) : null}
          onAskImage={askImage}
        />
      ))}
    </aside>
  )
}
