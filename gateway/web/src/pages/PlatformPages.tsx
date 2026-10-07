import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api'
import type { CatalogModelDto, InviteResponse, Page, PlanDto, PlatformAuditItem, SyncResult, TemplateDto, TenantDto, TenantWithInvite, UsageRow } from '../types'
import { TENANT_STATUSES } from '../types'
import {
  Alert,
  DataTable,
  EmptyRow,
  Field,
  fmtDate,
  fmtDateTime,
  fmtMoney,
  fmtNumber,
  isoOrNull,
  Loading,
  Modal,
  PageHeader,
  Pager,
  SecretPanel,
  Toolbar,
  daysAgo,
  endOfTodayIso,
  today,
  useLoad,
} from '../ui'
import { TierRows, tierPayload, toNumber, type TierForm } from './ProviderPages'

const API_FAMILIES = ['openai_chat', 'openai_responses', 'anthropic_messages', 'google_generate']

/* --------------------------------- Tenants -------------------------------- */

export function TenantsPage() {
  const tenants = useLoad(() => api.get<TenantDto[]>('/platform/api/tenants'), [])
  const plans = useLoad(() => api.get<PlanDto[]>('/platform/api/plans'), [])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<TenantDto | null>(null)
  const [inviting, setInviting] = useState<TenantDto | null>(null)
  const [resetting, setResetting] = useState<TenantDto | null>(null)
  const [secret, setSecret] = useState<{ label: string; value: string } | null>(null)

  return (
    <>
      <PageHeader
        title="Tenant"
        description="Buat tenant, atur plan, dan kirim undangan owner."
        actions={
          <button type="button" onClick={() => setCreating(true)}>
            Tambah tenant
          </button>
        }
      />
      {secret ? <SecretPanel label={secret.label} value={secret.value} onDismiss={() => setSecret(null)} /> : null}
      {tenants.error ? <Alert>{tenants.error}</Alert> : null}
      {tenants.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Slug', 'Status', 'Plan', 'Tenant ID', 'Dibuat', 'Aksi']}>
          {tenants.data && tenants.data.length > 0 ? (
            tenants.data.map((tenant) => (
              <tr key={tenant.id}>
                <td>{tenant.name}</td>
                <td className="mono">{tenant.slug}</td>
                <td>
                  <span className={tenant.status === 'active' ? 'badge ok' : 'badge off'}>{tenant.status}</span>
                </td>
                <td>{tenant.planName}</td>
                <td className="mono small">{tenant.id}</td>
                <td>{fmtDate(tenant.createdAt)}</td>
                <td className="actions">
                  <button type="button" onClick={() => setEditing(tenant)}>
                    Edit
                  </button>
                  <button type="button" onClick={() => setInviting(tenant)}>
                    Undang owner
                  </button>
                  <button type="button" onClick={() => setResetting(tenant)}>
                    Reset password
                  </button>
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={7} />
          )}
        </DataTable>
      )}

      {creating ? (
        <CreateTenantModal
          plans={plans.data ?? []}
          onClose={() => setCreating(false)}
          onSaved={(token) => {
            setCreating(false)
            setSecret({ label: 'Token undangan owner tenant', value: token })
            tenants.reload()
          }}
        />
      ) : null}
      {editing ? (
        <EditTenantModal
          tenant={editing}
          plans={plans.data ?? []}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            tenants.reload()
          }}
        />
      ) : null}
      {inviting ? (
        <InviteOwnerModal
          tenant={inviting}
          onClose={() => setInviting(null)}
          onSaved={(token) => {
            setInviting(null)
            setSecret({ label: `Token undangan owner — ${inviting.name}`, value: token })
          }}
        />
      ) : null}
      {resetting ? (
        <ResetUserPasswordModal
          tenant={resetting}
          onClose={() => setResetting(null)}
          onSaved={(token, userId) => {
            setResetting(null)
            setSecret({ label: `Token reset password user #${userId} — ${resetting.name}`, value: token })
          }}
        />
      ) : null}
    </>
  )
}

function CreateTenantModal({ plans, onClose, onSaved }: { plans: PlanDto[]; onClose: () => void; onSaved: (inviteToken: string) => void }) {
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [planId, setPlanId] = useState(plans[0] ? String(plans[0].id) : '')
  const [ownerEmail, setOwnerEmail] = useState('')
  const [ownerDisplayName, setOwnerDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await api.post<TenantWithInvite>('/platform/api/tenants', {
        name,
        slug,
        planId: toNumber(planId),
        ownerEmail: ownerEmail.trim(),
        ownerDisplayName,
      })
      onSaved(result.inviteToken)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Tambah tenant" onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama tenant">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Slug" hint="Huruf kecil, angka, dan tanda hubung.">
          <input required pattern="[a-z0-9-]+" value={slug} onChange={(event) => setSlug(event.target.value)} />
        </Field>
        <Field label="Plan">
          <select required value={planId} onChange={(event) => setPlanId(event.target.value)}>
            <option value="">Pilih plan…</option>
            {plans.map((plan) => (
              <option key={plan.id} value={plan.id}>
                {plan.name}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Email owner">
          <input type="email" required value={ownerEmail} onChange={(event) => setOwnerEmail(event.target.value)} />
        </Field>
        <Field label="Nama owner">
          <input required value={ownerDisplayName} onChange={(event) => setOwnerDisplayName(event.target.value)} />
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Buat tenant + undangan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function EditTenantModal({ tenant, plans, onClose, onSaved }: { tenant: TenantDto; plans: PlanDto[]; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(tenant.name)
  const [status, setStatus] = useState(tenant.status)
  const [planId, setPlanId] = useState(String(tenant.planId))
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.patch(`/platform/api/tenants/${tenant.id}`, { name, status, planId: toNumber(planId) })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Edit tenant — ${tenant.name}`} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Status" hint="Suspend menolak semua perubahan dari tenant (read-only).">
          <select value={status} onChange={(event) => setStatus(event.target.value)}>
            {TENANT_STATUSES.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Plan">
          <select value={planId} onChange={(event) => setPlanId(event.target.value)}>
            {plans.map((plan) => (
              <option key={plan.id} value={plan.id}>
                {plan.name}
              </option>
            ))}
          </select>
        </Field>
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

function InviteOwnerModal({ tenant, onClose, onSaved }: { tenant: TenantDto; onClose: () => void; onSaved: (inviteToken: string) => void }) {
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await api.post<InviteResponse>(`/platform/api/tenants/${tenant.id}/invite-owner`, { email: email.trim(), displayName })
      onSaved(result.inviteToken)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Undang owner — ${tenant.name}`} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Email">
          <input type="email" required value={email} onChange={(event) => setEmail(event.target.value)} />
        </Field>
        <Field label="Nama tampilan">
          <input required value={displayName} onChange={(event) => setDisplayName(event.target.value)} />
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Mengirim…' : 'Buat undangan owner'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function ResetUserPasswordModal({
  tenant,
  onClose,
  onSaved,
}: {
  tenant: TenantDto
  onClose: () => void
  onSaved: (token: string, userId: string) => void
}) {
  const [userId, setUserId] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await api.post<{ token: string }>(`/platform/api/tenants/${tenant.id}/reset-password/${userId.trim()}`)
      onSaved(result.token, userId.trim())
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Reset password pengguna — ${tenant.name}`} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <p className="muted">Masukkan ID pengguna (angka) dari halaman Pengguna tenant tersebut.</p>
        <Field label="User ID">
          <input required pattern="[0-9]+" value={userId} onChange={(event) => setUserId(event.target.value)} />
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Membuat…' : 'Buat token reset'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* ---------------------------------- Plans --------------------------------- */

export function PlansPage() {
  const plans = useLoad(() => api.get<PlanDto[]>('/platform/api/plans'), [])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<PlanDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(plan: PlanDto) {
    if (!window.confirm(`Hapus plan "${plan.name}"? Plan yang masih dipakai tenant tidak bisa dihapus.`)) return
    setError(null)
    try {
      await api.del(`/platform/api/plans/${plan.id}`)
      plans.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Plan"
        description="Batas kuota per tenant. Kosong berarti tanpa batas."
        actions={
          <button type="button" onClick={() => setCreating(true)}>
            Tambah plan
          </button>
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {plans.error ? <Alert>{plans.error}</Alert> : null}
      {plans.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Max project', 'Max API key', 'Max request/bulan', 'Max token/bulan', 'Aksi']}>
          {plans.data && plans.data.length > 0 ? (
            plans.data.map((plan) => (
              <tr key={plan.id}>
                <td>{plan.name}</td>
                <td>{fmtNumber(plan.maxProjects)}</td>
                <td>{fmtNumber(plan.maxApiKeys)}</td>
                <td>{fmtNumber(plan.maxRequestsPerMonth)}</td>
                <td>{fmtNumber(plan.maxTokensPerMonth)}</td>
                <td className="actions">
                  <button type="button" onClick={() => setEditing(plan)}>
                    Edit
                  </button>
                  <button type="button" className="danger" onClick={() => void remove(plan)}>
                    Hapus
                  </button>
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={6} />
          )}
        </DataTable>
      )}
      {creating ? (
        <PlanModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            plans.reload()
          }}
        />
      ) : null}
      {editing ? (
        <PlanModal
          plan={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            plans.reload()
          }}
        />
      ) : null}
    </>
  )
}

function PlanModal({ plan, onClose, onSaved }: { plan?: PlanDto; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(plan?.name ?? '')
  const [maxProjects, setMaxProjects] = useState(plan?.maxProjects != null ? String(plan.maxProjects) : '')
  const [maxApiKeys, setMaxApiKeys] = useState(plan?.maxApiKeys != null ? String(plan.maxApiKeys) : '')
  const [maxRequestsPerMonth, setMaxRequestsPerMonth] = useState(plan?.maxRequestsPerMonth != null ? String(plan.maxRequestsPerMonth) : '')
  const [maxTokensPerMonth, setMaxTokensPerMonth] = useState(plan?.maxTokensPerMonth != null ? String(plan.maxTokensPerMonth) : '')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    const body = {
      name,
      maxProjects: toNumber(maxProjects),
      maxApiKeys: toNumber(maxApiKeys),
      maxRequestsPerMonth: toNumber(maxRequestsPerMonth),
      maxTokensPerMonth: toNumber(maxTokensPerMonth),
    }
    try {
      if (plan) await api.patch(`/platform/api/plans/${plan.id}`, body)
      else await api.post('/platform/api/plans', body)
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={plan ? `Edit plan — ${plan.name}` : 'Tambah plan'} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama plan">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <div className="row-grid">
          <Field label="Max project" hint="Kosong = tanpa batas.">
            <input type="number" min={0} value={maxProjects} onChange={(event) => setMaxProjects(event.target.value)} />
          </Field>
          <Field label="Max API key">
            <input type="number" min={0} value={maxApiKeys} onChange={(event) => setMaxApiKeys(event.target.value)} />
          </Field>
          <Field label="Max request/bulan">
            <input type="number" min={0} value={maxRequestsPerMonth} onChange={(event) => setMaxRequestsPerMonth(event.target.value)} />
          </Field>
          <Field label="Max token/bulan">
            <input type="number" min={0} value={maxTokensPerMonth} onChange={(event) => setMaxTokensPerMonth(event.target.value)} />
          </Field>
        </div>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan plan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* -------------------------------- Templates ------------------------------- */

export function TemplatesPage() {
  const templates = useLoad(() => api.get<TemplateDto[]>('/admin/api/catalog/templates'), [])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<TemplateDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(template: TemplateDto) {
    if (!window.confirm(`Hapus template "${template.name}"?`)) return
    setError(null)
    try {
      await api.del(`/platform/api/templates/${template.id}`)
      templates.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Template provider"
        description="Template upstream untuk membuat provider tenant; sinkronisasi katalog memakai sync URL template."
        actions={
          <button type="button" onClick={() => setCreating(true)}>
            Tambah template
          </button>
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {templates.error ? <Alert>{templates.error}</Alert> : null}
      {templates.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Code', 'Tipe', 'Base URL', 'Auth', 'Models path', 'Sync', 'Status', 'Aksi']}>
          {templates.data && templates.data.length > 0 ? (
            templates.data.map((template) => (
              <tr key={template.id}>
                <td>{template.name}</td>
                <td className="mono">{template.code}</td>
                <td>{template.type}</td>
                <td className="mono small">{template.defaultBaseUrl ?? '—'}</td>
                <td>{[template.authHeader, template.authPrefix].filter(Boolean).join(' ') || '—'}</td>
                <td className="mono small">{template.modelsPath ?? '—'}</td>
                <td>
                  {template.syncKind ?? '—'}
                  {template.syncUrl ? <div className="mono small">{template.syncUrl}</div> : null}
                </td>
                <td>
                  <span className={template.enabled ? 'badge ok' : 'badge off'}>{template.enabled ? 'aktif' : 'nonaktif'}</span>
                </td>
                <td className="actions">
                  <button type="button" onClick={() => setEditing(template)}>
                    Edit
                  </button>
                  <button type="button" className="danger" onClick={() => void remove(template)}>
                    Hapus
                  </button>
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={9} />
          )}
        </DataTable>
      )}
      {creating ? (
        <TemplateModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            templates.reload()
          }}
        />
      ) : null}
      {editing ? (
        <TemplateModal
          template={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            templates.reload()
          }}
        />
      ) : null}
    </>
  )
}

function TemplateModal({ template, onClose, onSaved }: { template?: TemplateDto; onClose: () => void; onSaved: () => void }) {
  const [code, setCode] = useState(template?.code ?? '')
  const [name, setName] = useState(template?.name ?? '')
  const [type, setType] = useState(template?.type ?? 'openai')
  const [defaultBaseUrl, setDefaultBaseUrl] = useState(template?.defaultBaseUrl ?? '')
  const [authHeader, setAuthHeader] = useState(template?.authHeader ?? '')
  const [authPrefix, setAuthPrefix] = useState(template?.authPrefix ?? '')
  const [modelsPath, setModelsPath] = useState(template?.modelsPath ?? '')
  const [syncKind, setSyncKind] = useState(template?.syncKind ?? '')
  const [syncUrl, setSyncUrl] = useState(template?.syncUrl ?? '')
  const [enabled, setEnabled] = useState(template?.enabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    const body = {
      code,
      name,
      type,
      defaultBaseUrl: defaultBaseUrl || null,
      authHeader: authHeader || null,
      authPrefix: authPrefix || null,
      modelsPath: modelsPath || null,
      syncKind: syncKind || null,
      syncUrl: syncUrl || null,
      enabled,
    }
    try {
      if (template) await api.put(`/platform/api/templates/${template.id}`, body)
      else await api.post('/platform/api/templates', body)
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={template ? `Edit template — ${template.name}` : 'Tambah template'} onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <div className="row-grid">
          <Field label="Code" hint="Identifier unik, mis. openai.">
            <input required disabled={!!template} value={code} onChange={(event) => setCode(event.target.value)} />
          </Field>
          <Field label="Nama">
            <input required value={name} onChange={(event) => setName(event.target.value)} />
          </Field>
          <Field label="Tipe">
            <select value={type} onChange={(event) => setType(event.target.value)}>
              <option value="openai">openai</option>
            </select>
          </Field>
        </div>
        <Field label="Base URL default">
          <input type="url" value={defaultBaseUrl} onChange={(event) => setDefaultBaseUrl(event.target.value)} />
        </Field>
        <div className="row-grid">
          <Field label="Auth header">
            <input value={authHeader} onChange={(event) => setAuthHeader(event.target.value)} />
          </Field>
          <Field label="Auth prefix">
            <input value={authPrefix} onChange={(event) => setAuthPrefix(event.target.value)} />
          </Field>
          <Field label="Models path">
            <input value={modelsPath} onChange={(event) => setModelsPath(event.target.value)} />
          </Field>
        </div>
        <div className="row-grid">
          <Field label="Sync kind">
            <select value={syncKind} onChange={(event) => setSyncKind(event.target.value)}>
              <option value="">(tidak ada)</option>
              <option value="opencode_config">opencode_config</option>
            </select>
          </Field>
          <Field label="Sync URL" hint="Default untuk sinkronisasi katalog.">
            <input value={syncUrl} onChange={(event) => setSyncUrl(event.target.value)} />
          </Field>
        </div>
        <label className="inline">
          <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Template aktif
        </label>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan template'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* --------------------------------- Catalog -------------------------------- */

function tiersToForm(tiers: unknown): TierForm[] {
  if (!Array.isArray(tiers)) return []
  return tiers.map((tier) => {
    const item = (tier ?? {}) as Partial<Record<'minInputTokens' | 'input' | 'output' | 'cacheRead' | 'cacheWrite', number | null>>
    return {
      minInputTokens: item.minInputTokens != null ? String(item.minInputTokens) : '',
      input: item.input != null ? String(item.input) : '',
      output: item.output != null ? String(item.output) : '',
      cacheRead: item.cacheRead != null ? String(item.cacheRead) : '',
      cacheWrite: item.cacheWrite != null ? String(item.cacheWrite) : '',
    }
  })
}

export function CatalogPage() {
  const templates = useLoad(() => api.get<TemplateDto[]>('/admin/api/catalog/templates'), [])
  const [templateCode, setTemplateCode] = useState('')
  const effectiveCode = templateCode || templates.data?.[0]?.code || ''
  const models = useLoad(
    () => (effectiveCode ? api.get<CatalogModelDto[]>('/admin/api/catalog/models', { templateCode: effectiveCode }) : Promise.resolve([])),
    [effectiveCode],
  )
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<CatalogModelDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(model: CatalogModelDto) {
    if (!window.confirm(`Hapus model katalog "${model.upstreamModel}"?`)) return
    setError(null)
    try {
      await api.del(`/platform/api/catalog/${model.id}`)
      models.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Katalog model"
        description="Entri katalog template yang dipakai provider tenant saat import model."
        actions={
          <button type="button" disabled={!effectiveCode} onClick={() => setCreating(true)}>
            Tambah entri
          </button>
        }
      />
      <SyncPanel templates={templates.data ?? []} onSynced={() => models.reload()} />
      {error ? <Alert>{error}</Alert> : null}
      <Toolbar>
        <Field label="Template">
          <select value={effectiveCode} onChange={(event) => setTemplateCode(event.target.value)}>
            {(templates.data ?? []).map((template) => (
              <option key={template.id} value={template.code}>
                {template.name} ({template.code})
              </option>
            ))}
          </select>
        </Field>
      </Toolbar>
      {models.error ? <Alert>{models.error}</Alert> : null}
      {models.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Upstream model', 'Nama tampilan', 'Family', 'Context', 'Max in/out', 'Harga in/out per 1M', 'Tier', 'Fitur', 'Sumber', 'Status', 'Aksi']}>
          {models.data && models.data.length > 0 ? (
            models.data.map((model) => (
              <tr key={model.id}>
                <td className="mono">{model.upstreamModel}</td>
                <td>{model.displayName}</td>
                <td>{model.apiFamily ?? '—'}</td>
                <td>{fmtNumber(model.contextWindow)}</td>
                <td>
                  {fmtNumber(model.maxInputTokens)} / {fmtNumber(model.maxOutputTokens)}
                </td>
                <td>
                  {fmtNumber(model.inputPricePer1M)} / {fmtNumber(model.outputPricePer1M)} {model.currency ?? ''}
                </td>
                <td>{model.tiers && Array.isArray(model.tiers) && model.tiers.length > 0 ? `${model.tiers.length} tier` : '—'}</td>
                <td>
                  {[model.supportsTools ? 'tools' : null, model.supportsVision ? 'vision' : null, model.supportsReasoning ? 'reasoning' : null]
                    .filter(Boolean)
                    .join(', ') || '—'}
                </td>
                <td>{model.source}</td>
                <td>
                  <span className={model.enabled ? 'badge ok' : 'badge off'}>{model.enabled ? 'aktif' : 'nonaktif'}</span>
                </td>
                <td className="actions">
                  <button type="button" onClick={() => setEditing(model)}>
                    Edit
                  </button>
                  <button type="button" className="danger" onClick={() => void remove(model)}>
                    Hapus
                  </button>
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={11} />
          )}
        </DataTable>
      )}
      {creating ? (
        <CatalogModelModal
          templates={templates.data ?? []}
          defaultTemplateCode={effectiveCode}
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            models.reload()
          }}
        />
      ) : null}
      {editing ? (
        <CatalogModelModal
          model={editing}
          templates={templates.data ?? []}
          defaultTemplateCode={effectiveCode}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            models.reload()
          }}
        />
      ) : null}
    </>
  )
}

function SyncPanel({ templates, onSynced }: { templates: TemplateDto[]; onSynced: () => void }) {
  const [serviceKey, setServiceKey] = useState('')
  const [url, setUrl] = useState('')
  const [result, setResult] = useState<SyncResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      const response = await api.post<SyncResult>('/platform/api/catalog/sync', { serviceKey, url: url.trim() || undefined })
      setResult(response)
      setServiceKey('')
      onSynced()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  const defaultSyncUrl = templates.find((template) => template.syncUrl && template.syncKind)?.syncUrl

  return (
    <section className="card">
      <h2>Sinkronisasi katalog</h2>
      <p className="muted">
        Mengambil katalog dari sumber OpenCode. Service key tidak disimpan maupun dicatat di audit.
        {defaultSyncUrl ? ` Default URL: ${defaultSyncUrl}` : ''}
      </p>
      {error ? <Alert>{error}</Alert> : null}
      {result ? (
        <Alert kind="success">
          Template baru: {result.templatesAdded}; model baru: {result.modelsAdded}; diperbarui: {result.modelsUpdated}; dilewati (manual):{' '}
          {result.modelsSkippedManual}; tidak didukung: {result.modelsUnsupported}.
        </Alert>
      ) : null}
      <form onSubmit={submit} className="form">
        <div className="row-grid">
          <Field label="Service key">
            <input type="password" autoComplete="off" required value={serviceKey} onChange={(event) => setServiceKey(event.target.value)} />
          </Field>
          <Field label="URL" hint="Kosongkan untuk memakai sync URL template.">
            <input type="url" value={url} placeholder={defaultSyncUrl ?? ''} onChange={(event) => setUrl(event.target.value)} />
          </Field>
          <button type="submit" disabled={busy}>
            {busy ? 'Menyinkronkan…' : 'Mulai sinkronisasi'}
          </button>
        </div>
      </form>
    </section>
  )
}

function CatalogModelModal({
  model,
  templates,
  defaultTemplateCode,
  onClose,
  onSaved,
}: {
  model?: CatalogModelDto
  templates: TemplateDto[]
  defaultTemplateCode: string
  onClose: () => void
  onSaved: () => void
}) {
  const [templateCode, setTemplateCode] = useState(model?.templateCode ?? defaultTemplateCode)
  const [upstreamModel, setUpstreamModel] = useState(model?.upstreamModel ?? '')
  const [displayName, setDisplayName] = useState(model?.displayName ?? '')
  const [apiFamily, setApiFamily] = useState(model?.apiFamily ?? 'openai_chat')
  const [contextWindow, setContextWindow] = useState(model?.contextWindow != null ? String(model.contextWindow) : '')
  const [maxInputTokens, setMaxInputTokens] = useState(model?.maxInputTokens != null ? String(model.maxInputTokens) : '')
  const [maxOutputTokens, setMaxOutputTokens] = useState(model?.maxOutputTokens != null ? String(model.maxOutputTokens) : '')
  const [inputPrice, setInputPrice] = useState(model?.inputPricePer1M != null ? String(model.inputPricePer1M) : '')
  const [outputPrice, setOutputPrice] = useState(model?.outputPricePer1M != null ? String(model.outputPricePer1M) : '')
  const [cacheReadPrice, setCacheReadPrice] = useState(model?.cacheReadPricePer1M != null ? String(model.cacheReadPricePer1M) : '')
  const [cacheWritePrice, setCacheWritePrice] = useState(model?.cacheWritePricePer1M != null ? String(model.cacheWritePricePer1M) : '')
  const [currency, setCurrency] = useState(model?.currency ?? 'USD')
  const [tiers, setTiers] = useState<TierForm[]>(tiersToForm(model?.tiers ?? []))
  const [supportsTools, setSupportsTools] = useState(model?.supportsTools ?? false)
  const [supportsVision, setSupportsVision] = useState(model?.supportsVision ?? false)
  const [supportsReasoning, setSupportsReasoning] = useState(model?.supportsReasoning ?? false)
  const [enabled, setEnabled] = useState(model?.enabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    const body = {
      templateCode,
      upstreamModel,
      displayName,
      apiFamily,
      contextWindow: toNumber(contextWindow),
      maxInputTokens: toNumber(maxInputTokens),
      maxOutputTokens: toNumber(maxOutputTokens),
      inputPricePer1M: toNumber(inputPrice),
      outputPricePer1M: toNumber(outputPrice),
      cacheReadPricePer1M: toNumber(cacheReadPrice),
      cacheWritePricePer1M: toNumber(cacheWritePrice),
      tiers: tierPayload(tiers),
      currency: currency || null,
      supportsTools,
      supportsVision,
      supportsReasoning,
      enabled,
    }
    try {
      if (model) await api.put(`/platform/api/catalog/${model.id}`, body)
      else await api.post('/platform/api/catalog', body)
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={model ? `Edit entri katalog — ${model.upstreamModel}` : 'Tambah entri katalog'} onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <div className="row-grid">
          <Field label="Template">
            <select value={templateCode} onChange={(event) => setTemplateCode(event.target.value)}>
              {templates.map((template) => (
                <option key={template.id} value={template.code}>
                  {template.name} ({template.code})
                </option>
              ))}
            </select>
          </Field>
          <Field label="Model upstream" hint="Nama model di sisi provider.">
            <input required value={upstreamModel} onChange={(event) => setUpstreamModel(event.target.value)} />
          </Field>
          <Field label="Nama tampilan">
            <input required value={displayName} onChange={(event) => setDisplayName(event.target.value)} />
          </Field>
          <Field label="API family">
            <select value={apiFamily} onChange={(event) => setApiFamily(event.target.value)}>
              {API_FAMILIES.map((family) => (
                <option key={family} value={family}>
                  {family}
                </option>
              ))}
            </select>
          </Field>
        </div>
        <div className="row-grid">
          <Field label="Context window">
            <input type="number" min={1} value={contextWindow} onChange={(event) => setContextWindow(event.target.value)} />
          </Field>
          <Field label="Max input token">
            <input type="number" min={1} value={maxInputTokens} onChange={(event) => setMaxInputTokens(event.target.value)} />
          </Field>
          <Field label="Max output token">
            <input type="number" min={1} value={maxOutputTokens} onChange={(event) => setMaxOutputTokens(event.target.value)} />
          </Field>
          <Field label="Mata uang">
            <input value={currency} onChange={(event) => setCurrency(event.target.value)} />
          </Field>
        </div>
        <div className="row-grid">
          <Field label="Input / 1M">
            <input type="number" step="any" min={0} value={inputPrice} onChange={(event) => setInputPrice(event.target.value)} />
          </Field>
          <Field label="Output / 1M">
            <input type="number" step="any" min={0} value={outputPrice} onChange={(event) => setOutputPrice(event.target.value)} />
          </Field>
          <Field label="Cache read / 1M">
            <input type="number" step="any" min={0} value={cacheReadPrice} onChange={(event) => setCacheReadPrice(event.target.value)} />
          </Field>
          <Field label="Cache write / 1M">
            <input type="number" step="any" min={0} value={cacheWritePrice} onChange={(event) => setCacheWritePrice(event.target.value)} />
          </Field>
        </div>
        <TierRows tiers={tiers} onChange={setTiers} />
        <div className="checks">
          <label className="inline">
            <input type="checkbox" checked={supportsTools} onChange={(event) => setSupportsTools(event.target.checked)} /> Mendukung tools
          </label>
          <label className="inline">
            <input type="checkbox" checked={supportsVision} onChange={(event) => setSupportsVision(event.target.checked)} /> Mendukung vision
          </label>
          <label className="inline">
            <input type="checkbox" checked={supportsReasoning} onChange={(event) => setSupportsReasoning(event.target.checked)} /> Mendukung reasoning
          </label>
          <label className="inline">
            <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Entri aktif
          </label>
        </div>
        <p className="muted">Sumber entri selalu “manual” saat disimpan dari UI.</p>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan entri'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* ----------------------------- Platform usage ----------------------------- */

export function PlatformUsagePage() {
  const [from, setFrom] = useState(daysAgo(6))
  const [to, setTo] = useState(today())
  const usage = useLoad(() => api.get<UsageRow[]>('/platform/api/usage', { from, to }), [from, to])

  return (
    <>
      <PageHeader title="Pemakaian platform" description="Pemakaian agregat per tenant." />
      <Toolbar>
        <Field label="Dari">
          <input type="date" required value={from} onChange={(event) => setFrom(event.target.value)} />
        </Field>
        <Field label="Sampai">
          <input type="date" required value={to} onChange={(event) => setTo(event.target.value)} />
        </Field>
      </Toolbar>
      {usage.error ? <Alert>{usage.error}</Alert> : null}
      {usage.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Tenant', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya']}>
          {usage.data && usage.data.length > 0 ? (
            usage.data.map((row) => (
              <tr key={row.key}>
                <td>{row.label}</td>
                <td>{fmtNumber(row.requests)}</td>
                <td>{fmtNumber(row.denied)}</td>
                <td>{fmtNumber(row.errors)}</td>
                <td>{fmtNumber(row.inputTokens)}</td>
                <td>{fmtNumber(row.outputTokens)}</td>
                <td>{fmtMoney(row.cost)}</td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={7} />
          )}
        </DataTable>
      )}
    </>
  )
}

/* ----------------------------- Platform audit ----------------------------- */

export function PlatformAuditPage() {
  const [from, setFrom] = useState(daysAgo(6) + 'T00:00')
  const [to, setTo] = useState(endOfTodayIso())
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const audit = useLoad(
    () =>
      api.get<Page<PlatformAuditItem>>('/platform/api/audit', {
        from: isoOrNull(from),
        to: isoOrNull(to),
        page,
        pageSize,
      }),
    [from, to, page, pageSize],
  )

  return (
    <>
      <PageHeader title="Audit platform" description="Audit seluruh instalasi (aksi platform dan tenant), tanpa payload detail." />
      <Toolbar>
        <Field label="Dari">
          <input type="datetime-local" value={from} onChange={(event) => { setFrom(event.target.value); setPage(1) }} />
        </Field>
        <Field label="Sampai">
          <input type="datetime-local" value={to} onChange={(event) => { setTo(event.target.value); setPage(1) }} />
        </Field>
        <Field label="Baris per halaman">
          <select value={pageSize} onChange={(event) => { setPageSize(Number(event.target.value)); setPage(1) }}>
            <option value={25}>25</option>
            <option value={50}>50</option>
            <option value={100}>100</option>
            <option value={200}>200</option>
          </select>
        </Field>
      </Toolbar>
      {audit.error ? <Alert>{audit.error}</Alert> : null}
      {audit.loading ? (
        <Loading />
      ) : (
        <>
          <DataTable head={['Waktu', 'User', 'Action', 'Entity', 'Entity ID', 'IP']}>
            {audit.data && audit.data.items.length > 0 ? (
              audit.data.items.map((item) => (
                <tr key={item.id}>
                  <td>{fmtDateTime(item.createdAt)}</td>
                  <td>{item.userId ?? '—'}</td>
                  <td className="mono">{item.action}</td>
                  <td>{item.entity ?? '—'}</td>
                  <td className="mono">{item.entityId ?? '—'}</td>
                  <td>{item.ip ?? '—'}</td>
                </tr>
              ))
            ) : (
              <EmptyRow colSpan={6} />
            )}
          </DataTable>
          {audit.data ? <Pager page={audit.data.pageNumber} pageSize={audit.data.pageSize} total={audit.data.total} onPage={setPage} /> : null}
        </>
      )}
    </>
  )
}
