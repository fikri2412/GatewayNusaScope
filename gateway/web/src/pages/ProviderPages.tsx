import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api'
import { canConfigure, useAuth } from '../auth'
import type { CatalogModelDto, ModelDto, ProviderDto, TemplateDto } from '../types'
import {
  Alert,
  DataTable,
  EmptyRow,
  Field,
  Fieldset,
  fmtDateTime,
  fmtMoney,
  fmtNumber,
  Loading,
  Modal,
  PageHeader,
  Toolbar,
  useLoad,
} from '../ui'

export function toNumber(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed === '') return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : null
}

/* ------------------------------- Providers ------------------------------- */

export function ProvidersPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const providers = useLoad(() => api.get<ProviderDto[]>('/admin/api/providers'), [])
  const templates = useLoad(() => api.get<TemplateDto[]>('/admin/api/catalog/templates'), [])
  const [error, setError] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [editing, setEditing] = useState<ProviderDto | null>(null)
  const [rotating, setRotating] = useState<ProviderDto | null>(null)
  const [discovering, setDiscovering] = useState<ProviderDto | null>(null)
  const [importing, setImporting] = useState<ProviderDto | null>(null)

  async function toggleEnabled(provider: ProviderDto) {
    setError(null)
    try {
      await api.patch<ProviderDto>(`/admin/api/providers/${provider.id}`, { enabled: !provider.enabled })
      providers.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  async function remove(provider: ProviderDto) {
    if (!window.confirm(`Hapus provider "${provider.name}"? Model yang memakai provider ini bisa kehilangan route.`)) return
    setError(null)
    try {
      await api.del(`/admin/api/providers/${provider.id}`)
      providers.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Provider"
        description="Provider upstream milik tenant Anda (BYOK). Key hanya ditampilkan 4 karakter terakhir."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreateOpen(true)}>
              Tambah provider
            </button>
          ) : null
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {providers.error ? <Alert>{providers.error}</Alert> : null}
      {providers.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Tipe', 'Template', 'Base URL', 'Key', 'Status', 'Aksi']}>
          {providers.data && providers.data.length > 0 ? (
            providers.data.map((provider) => (
              <tr key={provider.id}>
                <td>{provider.name}</td>
                <td>{provider.type}</td>
                <td>{provider.templateCode ?? '—'}</td>
                <td className="mono">{provider.baseUrl}</td>
                <td>{provider.keyHint ? `…${provider.keyHint}` : '—'}</td>
                <td>
                  <span className={provider.enabled ? 'badge ok' : 'badge off'}>{provider.enabled ? 'aktif' : 'nonaktif'}</span>
                </td>
                <td className="actions">
                  {writable ? (
                    <>
                      <button type="button" onClick={() => setEditing(provider)}>
                        Edit
                      </button>
                      <button type="button" onClick={() => void toggleEnabled(provider)}>
                        {provider.enabled ? 'Nonaktifkan' : 'Aktifkan'}
                      </button>
                      <button type="button" onClick={() => setRotating(provider)}>
                        Rotasi key
                      </button>
                      <button type="button" onClick={() => setDiscovering(provider)}>
                        Test discovery
                      </button>
                      <button type="button" onClick={() => setImporting(provider)}>
                        Import model
                      </button>
                      <button type="button" className="danger" onClick={() => void remove(provider)}>
                        Hapus
                      </button>
                    </>
                  ) : (
                    <span className="muted">read-only</span>
                  )}
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={7} />
          )}
        </DataTable>
      )}

      {createOpen ? (
        <CreateProviderModal
          templates={templates.data ?? []}
          onClose={() => setCreateOpen(false)}
          onCreated={() => {
            setCreateOpen(false)
            providers.reload()
          }}
        />
      ) : null}
      {editing ? (
        <EditProviderModal
          provider={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            providers.reload()
          }}
        />
      ) : null}
      {rotating ? (
        <RotateKeyModal
          provider={rotating}
          onClose={() => setRotating(null)}
          onSaved={() => {
            setRotating(null)
            providers.reload()
          }}
        />
      ) : null}
      {discovering ? (
        <DiscoverModal
          provider={discovering}
          onClose={() => setDiscovering(null)}
          onImported={() => {
            setDiscovering(null)
            providers.reload()
          }}
        />
      ) : null}
      {importing ? (
        <ImportModelsModal
          provider={importing}
          onClose={() => setImporting(null)}
          onImported={() => {
            setImporting(null)
            providers.reload()
          }}
        />
      ) : null}
    </>
  )
}

function CreateProviderModal({ templates, onClose, onCreated }: { templates: TemplateDto[]; onClose: () => void; onCreated: () => void }) {
  const [mode, setMode] = useState<'template' | 'custom'>('template')
  const [templateCode, setTemplateCode] = useState('')
  const [name, setName] = useState('')
  const [baseUrl, setBaseUrl] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [authHeader, setAuthHeader] = useState('')
  const [authPrefix, setAuthPrefix] = useState('')
  const [modelsPath, setModelsPath] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const template = templates.find((item) => item.code === templateCode)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (mode === 'template') {
        await api.post('/admin/api/providers', {
          templateCode,
          name: name || template?.name || templateCode,
          baseUrl: baseUrl || template?.defaultBaseUrl || null,
          apiKey,
        })
      } else {
        await api.post('/admin/api/providers', {
          name,
          baseUrl,
          apiKey,
          authHeader: authHeader || null,
          authPrefix: authPrefix || null,
          modelsPath: modelsPath || null,
        })
      }
      onCreated()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Tambah provider" onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <fieldset className="fieldset">
          <legend>Jenis provider</legend>
          <label className="inline">
            <input type="radio" name="provider-mode" checked={mode === 'template'} onChange={() => setMode('template')} /> Dari template
          </label>
          <label className="inline">
            <input type="radio" name="provider-mode" checked={mode === 'custom'} onChange={() => setMode('custom')} /> Kustom
          </label>
        </fieldset>

        {mode === 'template' ? (
          <>
            <Field label="Template" hint="Template katalog platform; base URL dan header bisa ditimpa.">
              <select required value={templateCode} onChange={(event) => setTemplateCode(event.target.value)}>
                <option value="">Pilih template…</option>
                {templates.map((item) => (
                  <option key={item.id} value={item.code}>
                    {item.name} ({item.code})
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Nama provider">
              <input value={name} placeholder={template?.name ?? ''} onChange={(event) => setName(event.target.value)} />
            </Field>
            <Field label="Base URL" hint={template?.defaultBaseUrl ? `Default: ${template.defaultBaseUrl}` : undefined}>
              <input value={baseUrl} placeholder={template?.defaultBaseUrl ?? 'https://…'} onChange={(event) => setBaseUrl(event.target.value)} />
            </Field>
            <Field label="API key" hint="Disimpan terenkripsi; tidak pernah ditampilkan kembali.">
              <input type="password" autoComplete="off" required value={apiKey} onChange={(event) => setApiKey(event.target.value)} />
            </Field>
          </>
        ) : (
          <>
            <Field label="Nama provider">
              <input required value={name} onChange={(event) => setName(event.target.value)} />
            </Field>
            <Field label="Base URL">
              <input required type="url" value={baseUrl} placeholder="https://api.example.com/v1" onChange={(event) => setBaseUrl(event.target.value)} />
            </Field>
            <Field label="API key">
              <input type="password" autoComplete="off" required value={apiKey} onChange={(event) => setApiKey(event.target.value)} />
            </Field>
            <Field label="Auth header" hint="Contoh: Authorization.">
              <input value={authHeader} onChange={(event) => setAuthHeader(event.target.value)} />
            </Field>
            <Field label="Auth prefix" hint="Contoh: Bearer.">
              <input value={authPrefix} onChange={(event) => setAuthPrefix(event.target.value)} />
            </Field>
            <Field label="Models path" hint="Contoh: /models untuk discovery.">
              <input value={modelsPath} onChange={(event) => setModelsPath(event.target.value)} />
            </Field>
          </>
        )}
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan provider'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function EditProviderModal({ provider, onClose, onSaved }: { provider: ProviderDto; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(provider.name)
  const [baseUrl, setBaseUrl] = useState(provider.baseUrl)
  const [enabled, setEnabled] = useState(provider.enabled)
  const [authHeader, setAuthHeader] = useState(provider.authHeader)
  const [authPrefix, setAuthPrefix] = useState(provider.authPrefix)
  const [modelsPath, setModelsPath] = useState(provider.modelsPath ?? '')
  const [clearModelsPath, setClearModelsPath] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.patch(`/admin/api/providers/${provider.id}`, {
        name,
        baseUrl,
        enabled,
        authHeader,
        authPrefix,
        modelsPath: clearModelsPath ? null : modelsPath,
        clearModelsPath,
      })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Edit provider — ${provider.name}`} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Base URL">
          <input required type="url" value={baseUrl} onChange={(event) => setBaseUrl(event.target.value)} />
        </Field>
        <Field label="Auth header">
          <input value={authHeader} onChange={(event) => setAuthHeader(event.target.value)} />
        </Field>
        <Field label="Auth prefix">
          <input value={authPrefix} onChange={(event) => setAuthPrefix(event.target.value)} />
        </Field>
        <Field label="Models path">
          <input disabled={clearModelsPath} value={modelsPath} onChange={(event) => setModelsPath(event.target.value)} />
        </Field>
        <label className="inline">
          <input type="checkbox" checked={clearModelsPath} onChange={(event) => setClearModelsPath(event.target.checked)} /> Kosongkan models path
        </label>
        <label className="inline">
          <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Provider aktif
        </label>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan perubahan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function RotateKeyModal({ provider, onClose, onSaved }: { provider: ProviderDto; onClose: () => void; onSaved: () => void }) {
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.post(`/admin/api/providers/${provider.id}/rotate-key`, { apiKey })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Rotasi key — ${provider.name}`} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <p className="muted">Key lama langsung diganti. Key tidak pernah ditampilkan kembali.</p>
        <Field label="API key baru">
          <input type="password" autoComplete="off" required value={apiKey} onChange={(event) => setApiKey(event.target.value)} />
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Rotasi key'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function DiscoverModal({ provider, onClose, onImported }: { provider: ProviderDto; onClose: () => void; onImported: () => void }) {
  const discovery = useLoad(() => api.post<{ models: string[] }>(`/admin/api/providers/${provider.id}/discover`), [provider.id])
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [result, setResult] = useState<{ created: number; skipped: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const models = discovery.data?.models ?? []

  function toggle(model: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(model)) next.delete(model)
      else next.add(model)
      return next
    })
  }

  async function importSelected() {
    if (selected.size === 0) return
    setBusy(true)
    setError(null)
    try {
      const response = await api.post<{ created: { id: string; alias: string }[]; skipped: number }>(
        `/admin/api/providers/${provider.id}/import-models`,
        { source: 'discovered', upstreamModels: [...selected] },
      )
      setResult({ created: response.created.length, skipped: response.skipped })
      onImported()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Discovery — ${provider.name}`} onClose={onClose} wide>
      {error ? <Alert>{error}</Alert> : null}
      {result ? <Alert kind="success">Import selesai: {result.created} model dibuat, {result.skipped} dilewati.</Alert> : null}
      {discovery.error ? <Alert>{discovery.error}</Alert> : null}
      {discovery.loading ? (
        <Loading label="Menghubungi provider…" />
      ) : models.length === 0 ? (
        <p className="muted">Provider tidak mengembalikan model.</p>
      ) : (
        <>
          <Toolbar>
            <button type="button" className="ghost" onClick={() => setSelected(new Set(models))}>
              Pilih semua
            </button>
            <button type="button" className="ghost" onClick={() => setSelected(new Set())}>
              Kosongkan
            </button>
            <span className="muted">{selected.size} dari {models.length} dipilih</span>
          </Toolbar>
          <ul className="check-list">
            {models.map((model) => (
              <li key={model}>
                <label className="inline">
                  <input type="checkbox" checked={selected.has(model)} onChange={() => toggle(model)} /> <span className="mono">{model}</span>
                </label>
              </li>
            ))}
          </ul>
          <div className="form-actions">
            <button type="button" disabled={busy || selected.size === 0} onClick={() => void importSelected()}>
              {busy ? 'Mengimpor…' : `Import ${selected.size} model`}
            </button>
            <button type="button" className="ghost" onClick={onClose}>
              Tutup
            </button>
          </div>
        </>
      )}
    </Modal>
  )
}

function ImportModelsModal({ provider, onClose, onImported }: { provider: ProviderDto; onClose: () => void; onImported: () => void }) {
  const catalog = useLoad(
    () => (provider.templateCode ? api.get<CatalogModelDto[]>('/admin/api/catalog/models', { templateCode: provider.templateCode }) : Promise.resolve([])),
    [provider.templateCode],
  )
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [result, setResult] = useState<{ created: number; skipped: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Gateway hanya meneruskan family openai_chat; entri lain disaring server.
  const models = (catalog.data ?? []).filter((model) => model.enabled && model.apiFamily === 'openai_chat')

  function toggle(upstreamModel: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(upstreamModel)) next.delete(upstreamModel)
      else next.add(upstreamModel)
      return next
    })
  }

  async function importSelected() {
    if (selected.size === 0) return
    setBusy(true)
    setError(null)
    try {
      const response = await api.post<{ created: { id: string; alias: string }[]; skipped: number }>(
        `/admin/api/providers/${provider.id}/import-models`,
        { source: 'catalog', upstreamModels: [...selected] },
      )
      setResult({ created: response.created.length, skipped: response.skipped })
      onImported()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Import dari katalog — ${provider.name}`} onClose={onClose} wide>
      {error ? <Alert>{error}</Alert> : null}
      {result ? <Alert kind="success">Import selesai: {result.created} model dibuat, {result.skipped} dilewati.</Alert> : null}
      {!provider.templateCode ? (
        <p className="muted">Provider kustom tidak punya template katalog. Pakai “Test discovery” untuk mengimpor model dari upstream.</p>
      ) : catalog.error ? (
        <Alert>{catalog.error}</Alert>
      ) : catalog.loading ? (
        <Loading />
      ) : models.length === 0 ? (
        <p className="muted">Belum ada entri katalog openai_chat yang aktif untuk template ini.</p>
      ) : (
        <>
          <p className="muted">Hanya entri katalog family <span className="mono">openai_chat</span> yang aktif yang bisa diimpor.</p>
          <Toolbar>
            <button type="button" className="ghost" onClick={() => setSelected(new Set(models.map((model) => model.upstreamModel)))}>
              Pilih semua
            </button>
            <button type="button" className="ghost" onClick={() => setSelected(new Set())}>
              Kosongkan
            </button>
            <span className="muted">{selected.size} dari {models.length} dipilih</span>
          </Toolbar>
          <ul className="check-list">
            {models.map((model) => (
              <li key={model.id}>
                <label className="inline">
                  <input type="checkbox" checked={selected.has(model.upstreamModel)} onChange={() => toggle(model.upstreamModel)} />
                  <span className="mono">{model.upstreamModel}</span> <span className="muted">{model.displayName}</span>
                </label>
              </li>
            ))}
          </ul>
          <div className="form-actions">
            <button type="button" disabled={busy || selected.size === 0} onClick={() => void importSelected()}>
              {busy ? 'Mengimpor…' : `Import ${selected.size} model`}
            </button>
            <button type="button" className="ghost" onClick={onClose}>
              Tutup
            </button>
          </div>
        </>
      )}
    </Modal>
  )
}

/* -------------------------------- Models --------------------------------- */

interface RouteForm {
  providerId: string
  upstreamModel: string
  priority: string
  weight: string
}

export interface TierForm {
  minInputTokens: string
  input: string
  output: string
  cacheRead: string
  cacheWrite: string
}

const emptyRoute = (providerId = ''): RouteForm => ({ providerId, upstreamModel: '', priority: '0', weight: '1' })
export const emptyTier = (): TierForm => ({ minInputTokens: '', input: '', output: '', cacheRead: '', cacheWrite: '' })

function routePayload(routes: RouteForm[]) {
  return routes
    .filter((route) => route.providerId && route.upstreamModel.trim())
    .map((route) => ({
      providerId: route.providerId,
      upstreamModel: route.upstreamModel.trim(),
      priority: toNumber(route.priority) ?? 0,
      weight: toNumber(route.weight) ?? 1,
    }))
}

export function tierPayload(tiers: TierForm[]) {
  return tiers
    .filter((tier) => tier.minInputTokens.trim() !== '' && tier.input.trim() !== '' && tier.output.trim() !== '')
    .map((tier) => ({
      minInputTokens: toNumber(tier.minInputTokens) ?? 0,
      input: toNumber(tier.input) ?? 0,
      output: toNumber(tier.output) ?? 0,
      cacheRead: toNumber(tier.cacheRead),
      cacheWrite: toNumber(tier.cacheWrite),
    }))
}

function RouteRows({ routes, providers, onChange }: { routes: RouteForm[]; providers: ProviderDto[]; onChange: (routes: RouteForm[]) => void }) {
  function update(index: number, patch: Partial<RouteForm>) {
    onChange(routes.map((route, i) => (i === index ? { ...route, ...patch } : route)))
  }

  return (
    <Fieldset legend="Routes" hint="Urutan prioritas kecil dicoba lebih dulu; weight menentukan pembagian beban.">
      {routes.map((route, index) => (
        <div className="row-grid" key={index}>
          <Field label="Provider">
            <select value={route.providerId} onChange={(event) => update(index, { providerId: event.target.value })}>
              <option value="">Pilih provider…</option>
              {providers.map((provider) => (
                <option key={provider.id} value={provider.id}>
                  {provider.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Model upstream">
            <input value={route.upstreamModel} onChange={(event) => update(index, { upstreamModel: event.target.value })} />
          </Field>
          <Field label="Prioritas">
            <input type="number" min={0} value={route.priority} onChange={(event) => update(index, { priority: event.target.value })} />
          </Field>
          <Field label="Weight">
            <input type="number" min={1} value={route.weight} onChange={(event) => update(index, { weight: event.target.value })} />
          </Field>
          <button type="button" className="ghost" onClick={() => onChange(routes.filter((_, i) => i !== index))} aria-label={`Hapus route ${index + 1}`}>
            Hapus
          </button>
        </div>
      ))}
      <button type="button" className="ghost" onClick={() => onChange([...routes, emptyRoute()])}>
        Tambah route
      </button>
    </Fieldset>
  )
}

export function TierRows({ tiers, onChange }: { tiers: TierForm[]; onChange: (tiers: TierForm[]) => void }) {
  function update(index: number, patch: Partial<TierForm>) {
    onChange(tiers.map((tier, i) => (i === index ? { ...tier, ...patch } : tier)))
  }

  return (
    <Fieldset legend="Tier harga" hint="Harga per 1 juta token untuk prompt panjang; tier dasar ditentukan oleh harga utama di atas.">
      {tiers.map((tier, index) => (
        <div className="row-grid" key={index}>
          <Field label="Mulai token input">
            <input type="number" min={1} value={tier.minInputTokens} onChange={(event) => update(index, { minInputTokens: event.target.value })} />
          </Field>
          <Field label="Input / 1M">
            <input type="number" step="any" min={0} value={tier.input} onChange={(event) => update(index, { input: event.target.value })} />
          </Field>
          <Field label="Output / 1M">
            <input type="number" step="any" min={0} value={tier.output} onChange={(event) => update(index, { output: event.target.value })} />
          </Field>
          <Field label="Cache read">
            <input type="number" step="any" min={0} value={tier.cacheRead} onChange={(event) => update(index, { cacheRead: event.target.value })} />
          </Field>
          <Field label="Cache write">
            <input type="number" step="any" min={0} value={tier.cacheWrite} onChange={(event) => update(index, { cacheWrite: event.target.value })} />
          </Field>
          <button type="button" className="ghost" onClick={() => onChange(tiers.filter((_, i) => i !== index))} aria-label={`Hapus tier ${index + 1}`}>
            Hapus
          </button>
        </div>
      ))}
      <button type="button" className="ghost" onClick={() => onChange([...tiers, emptyTier()])}>
        Tambah tier
      </button>
    </Fieldset>
  )
}

export function ModelsPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const models = useLoad(() => api.get<ModelDto[]>('/admin/api/models'), [])
  const providers = useLoad(() => api.get<ProviderDto[]>('/admin/api/providers'), [])
  const [error, setError] = useState<string | null>(null)
  const [addingPrice, setAddingPrice] = useState<ModelDto | null>(null)
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<ModelDto | null>(null)
  const [editingRoutes, setEditingRoutes] = useState<ModelDto | null>(null)

  async function toggleEnabled(model: ModelDto) {
    setError(null)
    try {
      await api.patch(`/admin/api/models/${model.id}`, { enabled: !model.enabled })
      models.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  async function remove(model: ModelDto) {
    if (!window.confirm(`Hapus model "${model.alias}"?`)) return
    setError(null)
    try {
      await api.del(`/admin/api/models/${model.id}`)
      models.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Model"
        description="Alias model yang dilihat klien, route ke provider, dan harga."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreating(true)}>
              Tambah model
            </button>
          ) : null
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {models.error ? <Alert>{models.error}</Alert> : null}
      {providers.error ? <Alert>{providers.error}</Alert> : null}
      {models.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Alias', 'Deskripsi', 'Routes', 'Max output', 'Harga', 'Status', 'Aksi']}>
          {models.data && models.data.length > 0 ? (
            models.data.map((model) => (
              <tr key={model.id}>
                <td className="mono">{model.alias}</td>
                <td>{model.description ?? '—'}</td>
                <td>
                  {model.routes.length === 0 ? (
                    <span className="muted">tanpa route</span>
                  ) : (
                    <ul className="tight">
                      {model.routes.map((route, index) => (
                        <li key={index}>
                          {route.providerName} → <span className="mono">{route.upstreamModel}</span> (p{route.priority}/w{route.weight}
                          {route.enabled ? '' : ', nonaktif'})
                        </li>
                      ))}
                    </ul>
                  )}
                </td>
                <td>{fmtNumber(model.maxOutputTokens)}</td>
                <td>
                  {model.price
                    ? `${fmtMoney(model.price.input, model.price.currency)} / ${fmtMoney(model.price.output, model.price.currency)}${
                        model.price.tiers.length > 0 ? ` (+${model.price.tiers.length} tier)` : ''
                      }`
                    : '—'}
                </td>
                <td>
                  <span className={model.enabled ? 'badge ok' : 'badge off'}>{model.enabled ? 'aktif' : 'nonaktif'}</span>
                </td>
                <td className="actions">
                  {writable ? (
                    <>
                      <button type="button" onClick={() => setEditing(model)}>
                        Edit
                      </button>
                      <button type="button" onClick={() => setEditingRoutes(model)}>
                        Routes
                      </button>
                      <button type="button" onClick={() => setAddingPrice(model)}>
                        Tambah harga
                      </button>
                      <button type="button" onClick={() => void toggleEnabled(model)}>
                        {model.enabled ? 'Nonaktifkan' : 'Aktifkan'}
                      </button>
                      <button type="button" className="danger" onClick={() => void remove(model)}>
                        Hapus
                      </button>
                    </>
                  ) : (
                    <span className="muted">read-only</span>
                  )}
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={7} />
          )}
        </DataTable>
      )}

      {creating ? (
        <CreateModelModal
          providers={providers.data ?? []}
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false)
            models.reload()
          }}
        />
      ) : null}
      {editing ? (
        <EditModelModal
          model={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            models.reload()
          }}
        />
      ) : null}
      {editingRoutes ? (
        <RoutesModal
          model={editingRoutes}
          providers={providers.data ?? []}
          onClose={() => setEditingRoutes(null)}
          onSaved={() => {
            setEditingRoutes(null)
            models.reload()
          }}
        />
      ) : null}
      {addingPrice ? (
        <PriceModal
          model={addingPrice}
          onClose={() => setAddingPrice(null)}
          onSaved={() => {
            setAddingPrice(null)
            models.reload()
          }}
        />
      ) : null}
    </>
  )
}

function CreateModelModal({ providers, onClose, onCreated }: { providers: ProviderDto[]; onClose: () => void; onCreated: () => void }) {
  const [alias, setAlias] = useState('')
  const [description, setDescription] = useState('')
  const [maxOutputTokens, setMaxOutputTokens] = useState('')
  const [routes, setRoutes] = useState<RouteForm[]>([emptyRoute(providers[0]?.id ?? '')])
  const [withPrice, setWithPrice] = useState(true)
  const [input, setInput] = useState('')
  const [output, setOutput] = useState('')
  const [cacheRead, setCacheRead] = useState('')
  const [cacheWrite, setCacheWrite] = useState('')
  const [currency, setCurrency] = useState('USD')
  const [tiers, setTiers] = useState<TierForm[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    const routePayloadValue = routePayload(routes)
    if (routePayloadValue.length === 0) {
      setError('Minimal satu route dengan provider dan model upstream.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await api.post('/admin/api/models', {
        alias: alias.trim(),
        description: description.trim() || null,
        maxOutputTokens: toNumber(maxOutputTokens),
        routes: routePayloadValue,
        price: withPrice
          ? {
              input: toNumber(input) ?? 0,
              output: toNumber(output) ?? 0,
              cacheRead: toNumber(cacheRead),
              cacheWrite: toNumber(cacheWrite),
              currency: currency.trim() || 'USD',
              tiers: tierPayload(tiers),
            }
          : null,
      })
      onCreated()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Tambah model" onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Alias" hint="Nama model yang dipakai klien, mis. gpt-4o-mini.">
          <input required value={alias} onChange={(event) => setAlias(event.target.value)} />
        </Field>
        <Field label="Deskripsi">
          <input value={description} onChange={(event) => setDescription(event.target.value)} />
        </Field>
        <Field label="Max output token">
          <input type="number" min={1} value={maxOutputTokens} onChange={(event) => setMaxOutputTokens(event.target.value)} />
        </Field>
        <RouteRows routes={routes} providers={providers} onChange={setRoutes} />
        <label className="inline">
          <input type="checkbox" checked={withPrice} onChange={(event) => setWithPrice(event.target.checked)} /> Sertakan harga awal
        </label>
        {withPrice ? (
          <>
            <div className="row-grid">
              <Field label="Input / 1M">
                <input type="number" step="any" min={0} required value={input} onChange={(event) => setInput(event.target.value)} />
              </Field>
              <Field label="Output / 1M">
                <input type="number" step="any" min={0} required value={output} onChange={(event) => setOutput(event.target.value)} />
              </Field>
              <Field label="Cache read / 1M">
                <input type="number" step="any" min={0} value={cacheRead} onChange={(event) => setCacheRead(event.target.value)} />
              </Field>
              <Field label="Cache write / 1M">
                <input type="number" step="any" min={0} value={cacheWrite} onChange={(event) => setCacheWrite(event.target.value)} />
              </Field>
              <Field label="Mata uang">
                <input value={currency} onChange={(event) => setCurrency(event.target.value)} />
              </Field>
            </div>
            <TierRows tiers={tiers} onChange={setTiers} />
          </>
        ) : null}
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan model'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function EditModelModal({ model, onClose, onSaved }: { model: ModelDto; onClose: () => void; onSaved: () => void }) {
  const [description, setDescription] = useState(model.description ?? '')
  const [enabled, setEnabled] = useState(model.enabled)
  const [maxOutputTokens, setMaxOutputTokens] = useState(model.maxOutputTokens === null ? '' : String(model.maxOutputTokens))
  const [clearMaxOutputTokens, setClearMaxOutputTokens] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.patch(`/admin/api/models/${model.id}`, {
        description,
        enabled,
        maxOutputTokens: clearMaxOutputTokens ? null : toNumber(maxOutputTokens),
        clearMaxOutputTokens,
      })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Edit model — ${model.alias}`} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Deskripsi">
          <input value={description} onChange={(event) => setDescription(event.target.value)} />
        </Field>
        <Field label="Max output token">
          <input type="number" min={1} disabled={clearMaxOutputTokens} value={maxOutputTokens} onChange={(event) => setMaxOutputTokens(event.target.value)} />
        </Field>
        <label className="inline">
          <input type="checkbox" checked={clearMaxOutputTokens} onChange={(event) => setClearMaxOutputTokens(event.target.checked)} /> Kosongkan max output token
        </label>
        <label className="inline">
          <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Model aktif
        </label>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan perubahan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function RoutesModal({ model, providers, onClose, onSaved }: { model: ModelDto; providers: ProviderDto[]; onClose: () => void; onSaved: () => void }) {
  const [routes, setRoutes] = useState<RouteForm[]>(
    model.routes.map((route) => ({
      providerId: route.providerId,
      upstreamModel: route.upstreamModel,
      priority: String(route.priority),
      weight: String(route.weight),
    })),
  )
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.put(`/admin/api/models/${model.id}/routes`, { routes: routePayload(routes) })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Routes — ${model.alias}`} onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <p className="muted">Daftar route menggantikan seluruh route model ini.</p>
        <RouteRows routes={routes} providers={providers} onChange={setRoutes} />
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan routes'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function PriceModal({ model, onClose, onSaved }: { model: ModelDto; onClose: () => void; onSaved: () => void }) {
  const [input, setInput] = useState(model.price ? String(model.price.input) : '')
  const [output, setOutput] = useState(model.price ? String(model.price.output) : '')
  const [cacheRead, setCacheRead] = useState(model.price?.cacheRead != null ? String(model.price.cacheRead) : '')
  const [cacheWrite, setCacheWrite] = useState(model.price?.cacheWrite != null ? String(model.price.cacheWrite) : '')
  const [currency, setCurrency] = useState(model.price?.currency ?? 'USD')
  const [tiers, setTiers] = useState<TierForm[]>(
    (model.price?.tiers ?? []).map((tier) => ({
      minInputTokens: String(tier.minInputTokens),
      input: String(tier.input),
      output: String(tier.output),
      cacheRead: tier.cacheRead != null ? String(tier.cacheRead) : '',
      cacheWrite: tier.cacheWrite != null ? String(tier.cacheWrite) : '',
    })),
  )
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.post(`/admin/api/models/${model.id}/prices`, {
        input: toNumber(input) ?? 0,
        output: toNumber(output) ?? 0,
        cacheRead: toNumber(cacheRead),
        cacheWrite: toNumber(cacheWrite),
        currency: currency.trim() || 'USD',
        tiers: tierPayload(tiers),
      })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Tambah harga — ${model.alias}`} onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <p className="muted">Harga baru berlaku sejak sekarang; riwayat harga lama tetap tersimpan.</p>
        {model.price ? (
          <p className="muted">
            Harga saat ini: {fmtMoney(model.price.input, model.price.currency)} input / {fmtMoney(model.price.output, model.price.currency)} output (
            {fmtDateTime(model.price.effectiveFrom)})
          </p>
        ) : null}
        <div className="row-grid">
          <Field label="Input / 1M">
            <input type="number" step="any" min={0} required value={input} onChange={(event) => setInput(event.target.value)} />
          </Field>
          <Field label="Output / 1M">
            <input type="number" step="any" min={0} required value={output} onChange={(event) => setOutput(event.target.value)} />
          </Field>
          <Field label="Cache read / 1M">
            <input type="number" step="any" min={0} value={cacheRead} onChange={(event) => setCacheRead(event.target.value)} />
          </Field>
          <Field label="Cache write / 1M">
            <input type="number" step="any" min={0} value={cacheWrite} onChange={(event) => setCacheWrite(event.target.value)} />
          </Field>
          <Field label="Mata uang">
            <input value={currency} onChange={(event) => setCurrency(event.target.value)} />
          </Field>
        </div>
        <TierRows tiers={tiers} onChange={setTiers} />
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan harga'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}
