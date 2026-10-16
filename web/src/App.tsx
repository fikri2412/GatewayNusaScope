import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { logoutRemote, refreshSession } from './api/auth'
import { api, errorMessage, SESSION_EXPIRED, setOnUnauthorized } from './api/client'
import { clearSession, loadSession, parseThinkingModes, saveSession } from './api/session'
import type { Session, ThinkingMode } from './api/session'
import type { AttachmentPolicyDto, ConversationDto, ModelInfoDto } from './api/types'
import { buildConversationMarkdown, downloadText, markdownFileName } from './exportMarkdown'
import { artifactsForMessage } from './canvas'
import { ArtifactCard } from './components/ArtifactCard'
import { CanvasPanel } from './components/CanvasPanel'
import { CollectionView } from './components/CollectionView'
import { Composer } from './components/Composer'
import { ConversationList } from './components/ConversationList'
import { JobStatusBar } from './components/JobStatusBar'
import { LoginForm } from './components/LoginForm'
import { DraftBubble, MessageBubble } from './components/MessageBubble'
import { PendingAnswer } from './components/PendingAnswer'
import { Brand, Icon } from './components/NusaUi'
import { useArtifacts } from './hooks/useArtifacts'
import { useAttachmentPolicy } from './hooks/useAttachmentPolicy'
import { useConversation } from './hooks/useConversation'
import { useModels } from './hooks/useModels'

/** Di-key per percakapan oleh App, jadi state riwayat dimulai kosong tiap kali percakapan berganti. */
function ChatPane({ conversation, models, modelsError, reloadModels, policy, policyError, reloadPolicy, refreshPolicy, username, thinkMode, onThinkModeChange, onHome, onLogout, onRefresh }: {
  conversation: ConversationDto
  models: ModelInfoDto[] | null
  modelsError: string | null
  reloadModels: () => void
  policy: AttachmentPolicyDto | null
  policyError: string | null
  reloadPolicy: () => void
  refreshPolicy: () => Promise<AttachmentPolicyDto | null>
  username: string
  thinkMode: ThinkingMode
  onThinkModeChange: (mode: ThinkingMode) => void
  onHome: () => void
  onLogout: () => void
  onRefresh: () => void
}) {
  const { messages, drafts, jobs, pendingItems, error, waitingForReply, send, upload, regenerate, compare } = useConversation(conversation.id, onRefresh)
  const bottom = useRef<HTMLDivElement>(null)
  // AI membuat artifact sebelum jawabannya tersimpan, jadi daftar dimuat ulang tiap jumlah pesan berubah.
  const { artifacts, loaded: artifactsLoaded, error: artifactsError, reload: reloadArtifacts } = useArtifacts(conversation.id, messages.length)
  const [canvasOpen, setCanvasOpen] = useState(false)
  const [activeArtifactId, setActiveArtifactId] = useState<string | null>(null)
  const openArtifact = (id: string) => {
    setActiveArtifactId(id)
    setCanvasOpen(true)
  }
  // Canvas tidak punya tombol sendiri: terbuka lewat kartu di chat, atau otomatis saat AI baru membuat artifact di
  // sesi ini (bukan saat riwayat dimuat). Disesuaikan saat render, bukan lewat efek.
  const [knownArtifactIds, setKnownArtifactIds] = useState<ReadonlySet<string> | null>(null)
  if (artifactsLoaded) {
    if (knownArtifactIds === null) {
      setKnownArtifactIds(new Set(artifacts.map((a) => a.id)))
    } else {
      const fresh = artifacts.filter((a) => !knownArtifactIds.has(a.id))
      if (fresh.length > 0) {
        setKnownArtifactIds(new Set(artifacts.map((a) => a.id)))
        const byAi = fresh.filter((a) => a.sourceMessageId !== null)
        if (byAi.length > 0) openArtifact(byAi.reduce((a, b) => (b.createdAt > a.createdAt ? b : a)).id)
      }
    }
  }
  // Artifact tanpa kartu di chat (buatan sendiri, atau jawaban alternatif) tetap harus bisa dibuka lagi.
  const cardedIds = new Set(messages.flatMap((m) => artifactsForMessage(m, artifacts).map((a) => a.id)))
  const orphanArtifacts = artifacts.filter((a) => !cardedIds.has(a.id))
  // Draft tanpa teks belum perlu bubble; jumlah karakter yang berubah menggeser tampilan agar teks baru tetap terlihat.
  const visibleDrafts = drafts.filter((d) => d.text.length > 0 || d.state !== 'streaming')
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' })
  }, [messages.length, waitingForReply, drafts])

  /** Ekspor lokal: berkas .md dibuat di browser dari pesan yang sudah tampil, tanpa endpoint baru. */
  const exportMarkdown = () => {
    const at = new Date()
    const title = conversation.title ?? 'Sesi baru'
    downloadText(markdownFileName(title, at), buildConversationMarkdown({ title, messages, exportedAt: at }))
  }

  return (
    <>
      <header className="chat-header">
        <button type="button" className="brand-button" onClick={onHome} aria-label="Kembali ke Home" title="Kembali ke Home">
          <Brand compact />
        </button>
        <h1 className="chat-title">{conversation.title ?? 'Sesi baru'}</h1>
        <div className="chat-header-actions">
          <button type="button" className="ghost-button" onClick={exportMarkdown} disabled={messages.length === 0} title="Unduh percakapan sebagai Markdown">
            Ekspor .md
          </button>
          <button type="button" className="ghost-button" onClick={onHome} title="Sesi baru">
            + Sesi baru
          </button>
          <span className="header-account" title={username}>{username}</span>
          <button type="button" className="icon-button" onClick={onLogout} aria-label="Keluar" title="Keluar">
            <Icon name="logout" />
          </button>
        </div>
      </header>
      <div className={canvasOpen ? 'workspace with-canvas' : 'workspace'}>
      <main className="chat" aria-label={conversation.title ?? 'Sesi baru'}>
        {error && <p className="error" role="alert">{error}</p>}
        {artifactsError && <p className="error" role="alert">Canvas: {artifactsError}</p>}
        <div className="messages">
          {messages.length === 0 && visibleDrafts.length === 0 && <p className="muted">Belum ada pesan. Tulis sesuatu untuk memulai.</p>}
          {messages.map((m) => (
            <Fragment key={m.id}>
              <MessageBubble
                message={m}
                conversationId={conversation.id}
                turnPending={waitingForReply}
                onRegenerate={regenerate}
                onCompare={compare}
              />
              {artifactsForMessage(m, artifacts).length > 0 && (
                <div className="artifact-cards">
                  {artifactsForMessage(m, artifacts).map((a) => (
                    <ArtifactCard key={a.id} artifact={a} active={canvasOpen && a.id === activeArtifactId} onOpen={openArtifact} />
                  ))}
                </div>
              )}
            </Fragment>
          ))}
          {orphanArtifacts.length > 0 && (
            <div className="artifact-cards">
              {orphanArtifacts.map((a) => <ArtifactCard key={a.id} artifact={a} active={canvasOpen && a.id === activeArtifactId} onOpen={openArtifact} />)}
            </div>
          )}
          {/* Tiket alternatif (jawab ulang/bandingkan) belum punya pesan apa pun; tiket biasa tampil sebagai indikator tunggu. */}
          {pendingItems.filter((item) => item.isAlternative).map((item) => <PendingAnswer key={item.queueItemId} item={item} />)}
          {visibleDrafts.map((d) => <DraftBubble key={d.streamId} draft={d} />)}
          {waitingForReply && visibleDrafts.length === 0 && <p className="typing" role="status"><span className="activity-dot" aria-hidden="true" />Menyiapkan jawaban…</p>}
          <div ref={bottom} />
        </div>
        <JobStatusBar jobs={jobs} />
        {/* Percakapan tanpa AI sama sekali tetap tanpa baris ini; usage yang tidak lengkap wajib tetap terlihat walau terukurnya 0. */}
        {(conversation.tokensUsed > 0 || conversation.usageIncomplete) && (
          <p className="muted usage">
            {conversation.usageIncomplete
              ? `Pemakaian ≥ ${conversation.tokensUsed.toLocaleString()} token (cakupan usage belum lengkap)`
              : `Pemakaian ${conversation.tokensUsed.toLocaleString()} token`}
          </p>
        )}
        <Composer
          models={models}
          modelsError={modelsError}
          onReloadModels={reloadModels}
          policy={policy}
          policyError={policyError}
          onReloadPolicy={reloadPolicy}
          onRefreshPolicy={refreshPolicy}
          storedModelAlias={conversation.modelAlias}
          thinkMode={thinkMode}
          onThinkModeChange={onThinkModeChange}
          onUpload={upload}
          onSend={async (content, attachmentIds, model, thinkMode) => {
            await send(content, attachmentIds, model, thinkMode)
            onRefresh() // judul, alias tersimpan, dan pemakaian token diisi server
          }}
        />
      </main>
      <CanvasPanel
        conversationId={conversation.id}
        artifacts={artifacts}
        activeId={activeArtifactId}
        open={canvasOpen}
        onSelect={setActiveArtifactId}
        onClose={() => setCanvasOpen(false)}
        onChanged={reloadArtifacts}
        aiBusy={waitingForReply}
        onAskAi={async (prompt) => {
          // Memakai model terakhir percakapan ini; tanpa model tersimpan, pengguna memilihnya dulu di komposer.
          if (!conversation.modelAlias) throw new Error('Pilih model di komposer chat dan kirim satu pesan dulu, lalu coba lagi.')
          await send(prompt, [], conversation.modelAlias, thinkMode)
          onRefresh()
        }}
      />
      </div>
    </>
  )
}

/** Sesi dibuat saat unggah/kirim pertama, bukan sekadar membuka Home. */
function HomePane({ models, modelsError, reloadModels, policy, policyError, reloadPolicy, refreshPolicy, thinkMode, onThinkModeChange, onCreated, onSent }: {
  models: ModelInfoDto[] | null
  modelsError: string | null
  reloadModels: () => void
  policy: AttachmentPolicyDto | null
  policyError: string | null
  reloadPolicy: () => void
  refreshPolicy: () => Promise<AttachmentPolicyDto | null>
  onCreated: (conversation: ConversationDto) => void
  thinkMode: ThinkingMode
  onThinkModeChange: (mode: ThinkingMode) => void
  onSent: (id: string, mode: ThinkingMode) => void
}) {
  const pending = useRef<Promise<ConversationDto> | null>(null)
  const ensureConversation = () => {
    if (!pending.current) {
      pending.current = api.createConversation().then((c) => {
        onCreated(c)
        return c
      }).catch((e: unknown) => {
        pending.current = null
        throw e
      })
    }
    return pending.current
  }
  return (
    <main className="home">
      <Composer
        home
        models={models}
        modelsError={modelsError}
        onReloadModels={reloadModels}
        policy={policy}
        policyError={policyError}
        onReloadPolicy={reloadPolicy}
        onRefreshPolicy={refreshPolicy}
        storedModelAlias={null}
        thinkMode={thinkMode}
        onThinkModeChange={onThinkModeChange}
        onUpload={async (file, options) => api.uploadAttachment((await ensureConversation()).id, file, options)}
        onSend={async (content, attachmentIds, model, thinkMode) => {
          const c = await ensureConversation()
          await api.sendMessage(c.id, content, attachmentIds, model, thinkMode)
          onSent(c.id, thinkMode)
        }}
      />
    </main>
  )
}

/** Di-key per username oleh App, jadi keluar atau masuk akun lain selalu mulai dari state kosong. */
function Chat({ session, onLogout }: { session: Session; onLogout: () => void }) {
  const [conversations, setConversations] = useState<ConversationDto[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [collectionOpen, setCollectionOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  // ponytail: preferensi per akun/percakapan di tab ini; simpan di backend bila perlu sinkron antarperangkat.
  const modeStorageKey = `operator-ai.thinking-modes:${session.username}`
  const [thinkingModes, setThinkingModes] = useState<Record<string, ThinkingMode>>(() => {
    try {
      return parseThinkingModes(sessionStorage.getItem(modeStorageKey))
    } catch {
      return {}
    }
  })
  useEffect(() => {
    try {
      sessionStorage.setItem(modeStorageKey, JSON.stringify(thinkingModes))
    } catch {
      // Storage diblokir/penuh: pilihan tetap bertahan selama halaman ini masih terbuka.
    }
  }, [modeStorageKey, thinkingModes])
  // Katalog dimuat setelah masuk dan bisa dimuat ulang tanpa mengirim ulang apa pun.
  const { models, error: modelsError, reload: reloadModels } = useModels()
  // Kebijakan lampiran efektif juga dimuat setelah masuk; komposer memuat ulang tepat sebelum unggah/kirim.
  const { policy, error: policyError, reload: reloadPolicy, refresh: refreshPolicy } = useAttachmentPolicy()

  const selected = conversations.find((c) => c.id === selectedId) ?? null

  const refresh = useCallback(() => {
    api
      .listConversations()
      .then((rows) => {
        setConversations(rows)
        setError(null)
      })
      .catch((e: unknown) => setError(errorMessage(e)))
      .finally(() => setLoading(false))
  }, [])

  useEffect(refresh, [refresh])

  /** Daftar sesi di sidebar bukan tab: memilih sesi selalu keluar dari Koleksi. */
  const openConversation = (id: string) => {
    setCollectionOpen(false)
    setSelectedId(id)
    // Sesi dari Koleksi bisa belum ada di daftar yang sudah dimuat (mis. daftar masih menyusul).
    if (!conversations.some((c) => c.id === id)) refresh()
  }

  const goHome = () => {
    setCollectionOpen(false)
    setSelectedId(null)
  }

  const newChat = () => {
    goHome()
    requestAnimationFrame(() => document.querySelector<HTMLTextAreaElement>('.ns-input')?.focus())
  }

  /** 204 DELETE: baris hilang di server. Sesi yang sedang dibuka diganti tetangganya, bukan langsung Home. */
  const onDeleted = (id: string, nextId: string | null) => {
    setConversations(conversations.filter((c) => c.id !== id))
    setThinkingModes((prev) => {
      const next = { ...prev }
      delete next[id]
      return next
    })
    if (selectedId === id) setSelectedId(nextId)
  }

  return (
    <div className="app">
      <ConversationList
        conversations={conversations}
        selectedId={collectionOpen ? null : selectedId}
        collectionOpen={collectionOpen}
        onSelect={openConversation}
        onNew={newChat}
        onOpenCollection={() => setCollectionOpen(true)}
        onDeleted={onDeleted}
        username={session.username}
        onLogout={onLogout}
        loading={loading}
        error={error}
        onRetry={refresh}
      />
      <div className="app-main">
        {collectionOpen ? (
          <CollectionView onOpenSession={openConversation} />
        ) : selected ? (
          <ChatPane
            key={selected.id}
            conversation={selected}
            models={models}
            modelsError={modelsError}
            reloadModels={reloadModels}
            policy={policy}
            policyError={policyError}
            reloadPolicy={reloadPolicy}
            refreshPolicy={refreshPolicy}
            username={session.username}
            thinkMode={thinkingModes[selected.id] ?? 'fast'}
            onThinkModeChange={(mode) => setThinkingModes((prev) => ({ ...prev, [selected.id]: mode }))}
            onHome={goHome}
            onLogout={onLogout}
            onRefresh={refresh}
          />
        ) : (
          <HomePane
            models={models}
            modelsError={modelsError}
            reloadModels={reloadModels}
            policy={policy}
            policyError={policyError}
            reloadPolicy={reloadPolicy}
            refreshPolicy={refreshPolicy}
            thinkMode={thinkingModes.home ?? 'fast'}
            onThinkModeChange={(mode) => setThinkingModes((prev) => ({ ...prev, home: mode }))}
            onCreated={(c) => setConversations((prev) => [c, ...prev])}
            onSent={(id, mode) => {
              setThinkingModes((prev) => ({ ...prev, [id]: mode, home: 'fast' }))
              setSelectedId(id)
              refresh()
            }}
          />
        )}
      </div>
    </div>
  )
}

function App() {
  const [session, setSession] = useState<Session | null>(loadSession)
  const [notice, setNotice] = useState<string | undefined>(undefined)
  // Tanpa sesi tersimpan (mis. tab baru): coba segarkan dari cookie dulu, jangan langsung menuduh belum masuk.
  const [checking, setChecking] = useState(() => loadSession() === null)

  useEffect(() => {
    if (!checking) return
    let cancelled = false
    refreshSession()
      .then((s) => {
        if (cancelled || !s) return
        saveSession(s)
        setSession(s)
      })
      .catch(() => {
        // Jaringan gagal bukan bukti sesi ada; form masuk yang tampil seperti biasa.
      })
      .finally(() => {
        if (!cancelled) setChecking(false)
      })
    return () => {
      cancelled = true
    }
  }, [checking])

  // 401 di mana pun: client sudah membuang token, App kembali ke layar masuk dengan pesannya.
  useEffect(() => {
    setOnUnauthorized(() => {
      setSession(null)
      setNotice(SESSION_EXPIRED)
    })
    return () => setOnUnauthorized(null)
  }, [])

  if (checking) {
    return <p className="session-check" role="status">Memeriksa sesi…</p>
  }

  if (!session) {
    return (
      <LoginForm
        notice={notice}
        onAuthenticated={(s) => {
          saveSession(s)
          setNotice(undefined)
          setSession(s)
        }}
      />
    )
  }

  return (
    <Chat
      key={session.username}
      session={session}
      onLogout={() => {
        // Cookie dicabut di server dulu (best effort); sesi lokal dibuang apa pun hasilnya.
        void logoutRemote().finally(() => {
          clearSession()
          setNotice(undefined)
          setSession(null)
        })
      }}
    />
  )
}

export default App
