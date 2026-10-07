import { useState, type FormEvent } from 'react'
import { api, downloadFile, errorMessage } from '../api'
import { canConfigure, useAuth } from '../auth'
import type { AdminUser, ApiKeyDto, AuditItem, ModelDto, Page, PolicyDto, ProjectDto, Role, UsageBodyDto, UsageLogItem, UsageRow } from '../types'
import { PROJECT_STATUSES, ROLES } from '../types'
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
  firstOfMonth,
  linesToArray,
  startOfTodayIso,
  today,
  useLoad,
} from '../ui'

function toNumber(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed === '') return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : null
}

/* ------------------------------- Dashboard -------------------------------- */

const RANGES = [
  { id: 'today', label: 'Hari ini' },
  { id: '7d', label: '7 hari' },
  { id: 'month', label: 'Bulan ini' },
] as const

type RangeId = (typeof RANGES)[number]['id']

function rangeParams(range: RangeId): { from: string; to: string } {
  const to = today()
  if (range === 'today') return { from: to, to }
  if (range === '7d') return { from: daysAgo(6), to }
  return { from: firstOfMonth(), to }
}

export function DashboardPage() {
  const [range, setRange] = useState<RangeId>('today')
  const params = rangeParams(range)
  const daily = useLoad(() => api.get<UsageRow[]>('/admin/api/usage/summary', { ...params, groupBy: 'day' }), [params.from, params.to])
  const byModel = useLoad(() => api.get<UsageRow[]>('/admin/api/usage/summary', { ...params, groupBy: 'model' }), [params.from, params.to])

  const totals = (daily.data ?? []).reduce(
    (acc, row) => ({
      requests: acc.requests + row.requests,
      denied: acc.denied + row.denied,
      errors: acc.errors + row.errors,
      inputTokens: acc.inputTokens + row.inputTokens,
      outputTokens: acc.outputTokens + row.outputTokens,
      cost: acc.cost + row.cost,
    }),
    { requests: 0, denied: 0, errors: 0, inputTokens: 0, outputTokens: 0, cost: 0 },
  )

  return (
    <>
      <PageHeader title="Dashboard" description={`Pemakaian ${params.from} s/d ${params.to}.`} />
      <Toolbar>
        <div role="group" aria-label="Rentang waktu">
          {RANGES.map((item) => (
            <button key={item.id} type="button" className={range === item.id ? '' : 'ghost'} aria-pressed={range === item.id} onClick={() => setRange(item.id)}>
              {item.label}
            </button>
          ))}
        </div>
      </Toolbar>
      {daily.error ? <Alert>{daily.error}</Alert> : null}
      {daily.loading ? (
        <Loading />
      ) : (
        <section className="cards" aria-label="Ringkasan pemakaian">
          <div className="card stat">
            <span className="stat-label">Request</span>
            <span className="stat-value">{fmtNumber(totals.requests)}</span>
          </div>
          <div className="card stat">
            <span className="stat-label">Ditolak</span>
            <span className="stat-value">{fmtNumber(totals.denied)}</span>
          </div>
          <div className="card stat">
            <span className="stat-label">Error</span>
            <span className="stat-value">{fmtNumber(totals.errors)}</span>
          </div>
          <div className="card stat">
            <span className="stat-label">Token input</span>
            <span className="stat-value">{fmtNumber(totals.inputTokens)}</span>
          </div>
          <div className="card stat">
            <span className="stat-label">Token output</span>
            <span className="stat-value">{fmtNumber(totals.outputTokens)}</span>
          </div>
          <div className="card stat">
            <span className="stat-label">Biaya</span>
            <span className="stat-value">{fmtMoney(totals.cost)}</span>
          </div>
        </section>
      )}

      <section className="card">
        <h2>Per hari</h2>
        <DataTable head={['Hari', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya']}>
          {daily.data && daily.data.length > 0 ? (
            daily.data.map((row) => (
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
            <EmptyRow colSpan={7} label="Belum ada pemakaian pada rentang ini." />
          )}
        </DataTable>
      </section>

      <section className="card">
        <h2>Per model</h2>
        {byModel.error ? <Alert>{byModel.error}</Alert> : null}
        <DataTable head={['Model', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya']}>
          {byModel.data && byModel.data.length > 0 ? (
            byModel.data.map((row) => (
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
      </section>
    </>
  )
}

/* -------------------------------- Projects -------------------------------- */

export function ProjectsPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const projects = useLoad(() => api.get<ProjectDto[]>('/admin/api/projects'), [])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<ProjectDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(project: ProjectDto) {
    if (!window.confirm(`Hapus project "${project.name}"? API key project ini ikut terpengaruh.`)) return
    setError(null)
    try {
      await api.del(`/admin/api/projects/${project.id}`)
      projects.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Project"
        description="Project memisahkan key, kebijakan, dan pemakaian per aplikasi."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreating(true)}>
              Tambah project
            </button>
          ) : null
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {projects.error ? <Alert>{projects.error}</Alert> : null}
      {projects.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Status', 'Log konten', 'Retensi konten', 'Dibuat', 'Aksi']}>
          {projects.data && projects.data.length > 0 ? (
            projects.data.map((project) => (
              <tr key={project.id}>
                <td>{project.name}</td>
                <td>
                  <span className={project.status === 'active' ? 'badge ok' : 'badge off'}>{project.status}</span>
                </td>
                <td>{project.logContent ? 'ya' : 'tidak'}</td>
                <td>{project.contentRetentionDays === null ? '—' : `${project.contentRetentionDays} hari`}</td>
                <td>{fmtDateTime(project.createdAt)}</td>
                <td className="actions">
                  {writable ? (
                    <>
                      <button type="button" onClick={() => setEditing(project)}>
                        Edit
                      </button>
                      <button type="button" className="danger" onClick={() => void remove(project)}>
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
            <EmptyRow colSpan={6} />
          )}
        </DataTable>
      )}
      {creating ? (
        <ProjectModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            projects.reload()
          }}
        />
      ) : null}
      {editing ? (
        <ProjectModal
          project={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            projects.reload()
          }}
        />
      ) : null}
    </>
  )
}

function ProjectModal({ project, onClose, onSaved }: { project?: ProjectDto; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(project?.name ?? '')
  const [status, setStatus] = useState(project?.status ?? 'active')
  const [logContent, setLogContent] = useState(project?.logContent ?? false)
  const [contentRetentionDays, setContentRetentionDays] = useState(project?.contentRetentionDays != null ? String(project.contentRetentionDays) : '')
  const [clearRetention, setClearRetention] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (project) {
        await api.patch(`/admin/api/projects/${project.id}`, {
          name,
          status,
          logContent,
          contentRetentionDays: clearRetention ? null : toNumber(contentRetentionDays),
          clearRetention,
        })
      } else {
        await api.post('/admin/api/projects', { name, status, logContent, contentRetentionDays: toNumber(contentRetentionDays) })
      }
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={project ? `Edit project — ${project.name}` : 'Tambah project'} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Status" hint="Suspend menolak request baru pada project ini.">
          <select value={status} onChange={(event) => setStatus(event.target.value)}>
            {PROJECT_STATUSES.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </select>
        </Field>
        <label className="inline">
          <input type="checkbox" checked={logContent} onChange={(event) => setLogContent(event.target.checked)} /> Simpan log isi percakapan
        </label>
        <Field label="Retensi konten (hari)" hint="Kosongkan untuk tanpa retensi; isi 0 atau lebih.">
          <input type="number" min={0} disabled={clearRetention} value={contentRetentionDays} onChange={(event) => setContentRetentionDays(event.target.value)} />
        </Field>
        {project ? (
          <label className="inline">
            <input type="checkbox" checked={clearRetention} onChange={(event) => setClearRetention(event.target.checked)} /> Kosongkan retensi konten
          </label>
        ) : null}
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* --------------------------------- Keys ----------------------------------- */

export function KeysPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const [projectFilter, setProjectFilter] = useState('')
  const projects = useLoad(() => api.get<ProjectDto[]>('/admin/api/projects'), [])
  const keys = useLoad(() => api.get<ApiKeyDto[]>('/admin/api/keys', { projectId: projectFilter || undefined }), [projectFilter])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<ApiKeyDto | null>(null)
  const [plaintext, setPlaintext] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function revoke(key: ApiKeyDto) {
    if (!window.confirm(`Cabut key "${key.name}"? Klien yang memakainya akan langsung ditolak.`)) return
    setError(null)
    try {
      await api.post(`/admin/api/keys/${key.id}/revoke`)
      keys.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="API Key"
        description="Key dikelompokkan per project; nilai penuh hanya tampil sekali saat dibuat."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreating(true)}>
              Terbitkan key
            </button>
          ) : null
        }
      />
      {plaintext ? <SecretPanel label="API key baru" value={plaintext} onDismiss={() => setPlaintext(null)} /> : null}
      {error ? <Alert>{error}</Alert> : null}
      <Toolbar>
        <Field label="Filter project">
          <select value={projectFilter} onChange={(event) => setProjectFilter(event.target.value)}>
            <option value="">Semua project</option>
            {(projects.data ?? []).map((project) => (
              <option key={project.id} value={project.id}>
                {project.name}
              </option>
            ))}
          </select>
        </Field>
      </Toolbar>
      {keys.error ? <Alert>{keys.error}</Alert> : null}
      {keys.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Project', 'Prefix', 'IP diizinkan', 'Kedaluwarsa', 'Terakhir dipakai', 'Status', 'Aksi']}>
          {keys.data && keys.data.length > 0 ? (
            keys.data.map((key) => (
              <tr key={key.id}>
                <td>{key.name}</td>
                <td>{(projects.data ?? []).find((project) => project.id === key.projectId)?.name ?? key.projectId}</td>
                <td className="mono">{key.keyPrefix}…</td>
                <td>{key.allowedIps && key.allowedIps.length > 0 ? key.allowedIps.join(', ') : 'semua'}</td>
                <td>{key.expiresAt ? fmtDateTime(key.expiresAt) : '—'}</td>
                <td>{fmtDateTime(key.lastUsedAt)}</td>
                <td>
                  <span className={key.revokedAt ? 'badge off' : 'badge ok'}>{key.revokedAt ? `dicabut ${fmtDate(key.revokedAt)}` : 'aktif'}</span>
                </td>
                <td className="actions">
                  {writable && !key.revokedAt ? (
                    <>
                      <button type="button" onClick={() => setEditing(key)}>
                        Edit
                      </button>
                      <button type="button" className="danger" onClick={() => void revoke(key)}>
                        Cabut
                      </button>
                    </>
                  ) : (
                    <span className="muted">{writable ? 'dicabut' : 'read-only'}</span>
                  )}
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={8} />
          )}
        </DataTable>
      )}
      {creating ? (
        <KeyModal
          projects={projects.data ?? []}
          onClose={() => setCreating(false)}
          onSaved={(created) => {
            setCreating(false)
            if (created.plaintextKey) setPlaintext(created.plaintextKey)
            keys.reload()
          }}
        />
      ) : null}
      {editing ? (
        <KeyModal
          keyItem={editing}
          projects={projects.data ?? []}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            keys.reload()
          }}
        />
      ) : null}
    </>
  )
}

function KeyModal({ keyItem, projects, onClose, onSaved }: { keyItem?: ApiKeyDto; projects: ProjectDto[]; onClose: () => void; onSaved: (created: ApiKeyDto) => void }) {
  const [projectId, setProjectId] = useState(keyItem?.projectId ?? projects[0]?.id ?? '')
  const [name, setName] = useState(keyItem?.name ?? '')
  const [expiresAt, setExpiresAt] = useState('')
  const [clearExpiry, setClearExpiry] = useState(false)
  const [allowedIps, setAllowedIps] = useState((keyItem?.allowedIps ?? []).join('\n'))
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const ips = linesToArray(allowedIps)
      if (keyItem) {
        await api.patch(`/admin/api/keys/${keyItem.id}`, {
          name,
          expiresAt: clearExpiry ? null : expiresAt ? new Date(expiresAt).toISOString() : undefined,
          clearExpiry,
          allowedIps: ips,
        })
        onSaved(keyItem)
      } else {
        const created = await api.post<ApiKeyDto>('/admin/api/keys', {
          projectId,
          name,
          expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
          allowedIps: ips,
        })
        onSaved(created)
      }
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={keyItem ? `Edit key — ${keyItem.name}` : 'Terbitkan API key'} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        {keyItem ? null : (
          <Field label="Project">
            <select required value={projectId} onChange={(event) => setProjectId(event.target.value)}>
              <option value="">Pilih project…</option>
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          </Field>
        )}
        <Field label="Nama key">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Kedaluwarsa" hint={keyItem?.expiresAt ? `Saat ini: ${fmtDateTime(keyItem.expiresAt)}` : 'Kosongkan untuk tanpa kedaluwarsa.'}>
          <input type="datetime-local" disabled={clearExpiry} value={expiresAt} onChange={(event) => setExpiresAt(event.target.value)} />
        </Field>
        {keyItem ? (
          <label className="inline">
            <input type="checkbox" checked={clearExpiry} onChange={(event) => setClearExpiry(event.target.checked)} /> Hapus kedaluwarsa
          </label>
        ) : null}
        <Field label="IP diizinkan" hint="Satu IP per baris (atau dipisah koma). Kosong = semua IP.">
          <textarea rows={4} value={allowedIps} onChange={(event) => setAllowedIps(event.target.value)} />
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : keyItem ? 'Simpan perubahan' : 'Terbitkan key'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* -------------------------------- Policies -------------------------------- */

export function PoliciesPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const policies = useLoad(() => api.get<PolicyDto[]>('/admin/api/policies'), [])
  const projects = useLoad(() => api.get<ProjectDto[]>('/admin/api/projects'), [])
  const keys = useLoad(() => api.get<ApiKeyDto[]>('/admin/api/keys'), [])
  const [scope, setScope] = useState<'tenant' | 'project' | 'key'>('tenant')
  const [scopeId, setScopeId] = useState('')
  const [error, setError] = useState<string | null>(null)

  const effectiveScopeId = scope === 'tenant' ? (user?.tenantId ?? '') : scopeId
  const current = (policies.data ?? []).find((policy) => policy.scope === scope && policy.scopeId === effectiveScopeId) ?? null

  async function removePolicy(policy: PolicyDto) {
    if (!window.confirm(`Hapus policy ${policy.scope} ini?`)) return
    setError(null)
    try {
      await api.del(`/admin/api/policies/${policy.scope}/${policy.scopeId}`)
      policies.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader title="Policies" description="Batas kuota dan model yang diizinkan per tenant, project, atau API key." />
      {error ? <Alert>{error}</Alert> : null}
      <Toolbar>
        <Field label="Scope">
          <select value={scope} onChange={(event) => setScope(event.target.value as 'tenant' | 'project' | 'key')}>
            <option value="tenant">Tenant</option>
            <option value="project">Project</option>
            <option value="key">API key</option>
          </select>
        </Field>
        {scope === 'project' ? (
          <Field label="Project">
            <select value={scopeId} onChange={(event) => setScopeId(event.target.value)}>
              <option value="">Pilih project…</option>
              {(projects.data ?? []).map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          </Field>
        ) : null}
        {scope === 'key' ? (
          <Field label="API key">
            <select value={scopeId} onChange={(event) => setScopeId(event.target.value)}>
              <option value="">Pilih key…</option>
              {(keys.data ?? []).map((key) => (
                <option key={key.id} value={key.id}>
                  {key.name} ({key.keyPrefix}…)
                </option>
              ))}
            </select>
          </Field>
        ) : null}
      </Toolbar>

      {policies.error ? <Alert>{policies.error}</Alert> : null}
      {effectiveScopeId === '' && scope !== 'tenant' ? (
        <p className="muted">Pilih {scope === 'project' ? 'project' : 'API key'} untuk melihat policy-nya.</p>
      ) : (
        <PolicyEditor
          key={`${scope}:${effectiveScopeId}`}
          scope={scope}
          scopeId={effectiveScopeId}
          policy={current}
          writable={writable}
          onSaved={() => policies.reload()}
        />
      )}

      <section className="card">
        <h2>Semua policy</h2>
        {policies.loading ? (
          <Loading />
        ) : (
          <DataTable head={['Scope', 'ID', 'Max token/req', 'Req/menit', 'Kuota harian', 'Kuota bulanan', 'Budget bulanan', 'Model diizinkan', 'Status', '']}>
            {policies.data && policies.data.length > 0 ? (
              policies.data.map((policy) => (
                <tr key={`${policy.scope}:${policy.scopeId}`}>
                  <td>{policy.scope}</td>
                  <td className="mono">{policy.scopeId}</td>
                  <td>{fmtNumber(policy.maxTokensPerRequest)}</td>
                  <td>{fmtNumber(policy.requestsPerMinute)}</td>
                  <td>{fmtNumber(policy.dailyTokenQuota)}</td>
                  <td>{fmtNumber(policy.monthlyTokenQuota)}</td>
                  <td>{policy.monthlyBudget === null ? '—' : fmtMoney(policy.monthlyBudget)}</td>
                  <td>{policy.allowedModels && policy.allowedModels.length > 0 ? policy.allowedModels.join(', ') : 'semua'}</td>
                  <td>
                    <span className={policy.enabled ? 'badge ok' : 'badge off'}>{policy.enabled ? 'aktif' : 'nonaktif'}</span>
                  </td>
                  <td className="actions">
                    {writable ? (
                      <button type="button" className="danger" onClick={() => void removePolicy(policy)}>
                        Hapus
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))
            ) : (
              <EmptyRow colSpan={10} label="Belum ada policy." />
            )}
          </DataTable>
        )}
      </section>
    </>
  )
}

function PolicyEditor({
  scope,
  scopeId,
  policy,
  writable,
  onSaved,
}: {
  scope: string
  scopeId: string
  policy: PolicyDto | null
  writable: boolean
  onSaved: () => void
}) {
  const [maxTokensPerRequest, setMaxTokensPerRequest] = useState(policy?.maxTokensPerRequest != null ? String(policy.maxTokensPerRequest) : '')
  const [requestsPerMinute, setRequestsPerMinute] = useState(policy?.requestsPerMinute != null ? String(policy.requestsPerMinute) : '')
  const [dailyTokenQuota, setDailyTokenQuota] = useState(policy?.dailyTokenQuota != null ? String(policy.dailyTokenQuota) : '')
  const [monthlyTokenQuota, setMonthlyTokenQuota] = useState(policy?.monthlyTokenQuota != null ? String(policy.monthlyTokenQuota) : '')
  const [monthlyBudget, setMonthlyBudget] = useState(policy?.monthlyBudget != null ? String(policy.monthlyBudget) : '')
  const [allowedModels, setAllowedModels] = useState((policy?.allowedModels ?? []).join('\n'))
  const [enabled, setEnabled] = useState(policy?.enabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    setMessage(null)
    try {
      await api.put(`/admin/api/policies/${scope}/${scopeId}`, {
        maxTokensPerRequest: toNumber(maxTokensPerRequest),
        requestsPerMinute: toNumber(requestsPerMinute),
        dailyTokenQuota: toNumber(dailyTokenQuota),
        monthlyTokenQuota: toNumber(monthlyTokenQuota),
        monthlyBudget: toNumber(monthlyBudget),
        allowedModels: linesToArray(allowedModels),
        enabled,
      })
      setMessage('Policy disimpan.')
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>
        Policy {scope} <span className="mono muted">{scopeId}</span>
      </h2>
      {error ? <Alert>{error}</Alert> : null}
      {message ? <Alert kind="success">{message}</Alert> : null}
      {policy === null ? <p className="muted">Belum ada policy untuk scope ini — isi form untuk membuatnya.</p> : null}
      <form onSubmit={save} className="form">
        <div className="row-grid">
          <Field label="Max token per request" hint="Kosong = tanpa batas.">
            <input type="number" min={1} disabled={!writable} value={maxTokensPerRequest} onChange={(event) => setMaxTokensPerRequest(event.target.value)} />
          </Field>
          <Field label="Request per menit">
            <input type="number" min={1} disabled={!writable} value={requestsPerMinute} onChange={(event) => setRequestsPerMinute(event.target.value)} />
          </Field>
          <Field label="Kuota token harian">
            <input type="number" min={1} disabled={!writable} value={dailyTokenQuota} onChange={(event) => setDailyTokenQuota(event.target.value)} />
          </Field>
          <Field label="Kuota token bulanan">
            <input type="number" min={1} disabled={!writable} value={monthlyTokenQuota} onChange={(event) => setMonthlyTokenQuota(event.target.value)} />
          </Field>
          <Field label="Budget bulanan">
            <input type="number" step="any" min={0} disabled={!writable} value={monthlyBudget} onChange={(event) => setMonthlyBudget(event.target.value)} />
          </Field>
        </div>
        <Field label="Model diizinkan" hint="Satu alias per baris. Kosong = semua model.">
          <textarea rows={4} disabled={!writable} value={allowedModels} onChange={(event) => setAllowedModels(event.target.value)} />
        </Field>
        <label className="inline">
          <input type="checkbox" disabled={!writable} checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Policy aktif
        </label>
        {writable ? (
          <div className="form-actions">
            <button type="submit" disabled={busy}>
              {busy ? 'Menyimpan…' : 'Simpan policy'}
            </button>
          </div>
        ) : (
          <p className="muted">Hanya owner/admin yang bisa mengubah policy.</p>
        )}
      </form>
    </section>
  )
}

/* --------------------------------- Usage ---------------------------------- */

export function UsagePage() {
  const { user } = useAuth()
  const canReadBody = !!user && canConfigure(user)
  const [bodyFor, setBodyFor] = useState<UsageLogItem | null>(null)
  const [from, setFrom] = useState(daysAgo(6))
  const [to, setTo] = useState(today())
  const [groupBy, setGroupBy] = useState<'day' | 'project' | 'model' | 'key'>('day')
  const summary = useLoad(() => api.get<UsageRow[]>('/admin/api/usage/summary', { from, to, groupBy }), [from, to, groupBy])

  const [logFrom, setLogFrom] = useState(startOfTodayIso())
  const [logTo, setLogTo] = useState(endOfTodayIso())
  const [projectId, setProjectId] = useState('')
  const [modelId, setModelId] = useState('')
  const [keyId, setKeyId] = useState('')
  const [status, setStatus] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const projects = useLoad(() => api.get<ProjectDto[]>('/admin/api/projects'), [])
  const models = useLoad(() => api.get<ModelDto[]>('/admin/api/models'), [])
  const keys = useLoad(() => api.get<ApiKeyDto[]>('/admin/api/keys'), [])
  const logs = useLoad(
    () =>
      api.get<Page<UsageLogItem>>('/admin/api/usage/logs', {
        from: isoOrNull(logFrom),
        to: isoOrNull(logTo),
        projectId: projectId || undefined,
        modelId: modelId || undefined,
        keyId: keyId || undefined,
        status: status || undefined,
        page,
        pageSize,
      }),
    [logFrom, logTo, projectId, modelId, keyId, status, page, pageSize],
  )
  const [exportError, setExportError] = useState<string | null>(null)

  async function exportCsv() {
    setExportError(null)
    try {
      await downloadFile(
        '/admin/api/usage/export',
        {
          from: isoOrNull(logFrom),
          to: isoOrNull(logTo),
          projectId: projectId || undefined,
          modelId: modelId || undefined,
          keyId: keyId || undefined,
          status: status || undefined,
        },
        `usage-${logFrom.slice(0, 10)}-${logTo.slice(0, 10)}.csv`,
      )
    } catch (err) {
      setExportError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader title="Usage" description="Ringkasan agregat dan log per request." />

      <section className="card">
        <h2>Ringkasan</h2>
        <Toolbar>
          <Field label="Dari">
            <input type="date" value={from} onChange={(event) => setFrom(event.target.value)} />
          </Field>
          <Field label="Sampai">
            <input type="date" value={to} onChange={(event) => setTo(event.target.value)} />
          </Field>
          <Field label="Kelompokkan">
            <select value={groupBy} onChange={(event) => setGroupBy(event.target.value as typeof groupBy)}>
              <option value="day">Hari</option>
              <option value="project">Project</option>
              <option value="model">Model</option>
              <option value="key">API key</option>
            </select>
          </Field>
        </Toolbar>
        {summary.error ? <Alert>{summary.error}</Alert> : null}
        {summary.loading ? (
          <Loading />
        ) : (
          <DataTable head={[groupBy === 'day' ? 'Hari' : 'Nama', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya']}>
            {summary.data && summary.data.length > 0 ? (
              summary.data.map((row) => (
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
      </section>

      <section className="card">
        <h2>Log request</h2>
        {exportError ? <Alert>{exportError}</Alert> : null}
        <Toolbar>
          <Field label="Dari">
            <input type="datetime-local" value={logFrom} onChange={(event) => { setLogFrom(event.target.value); setPage(1) }} />
          </Field>
          <Field label="Sampai">
            <input type="datetime-local" value={logTo} onChange={(event) => { setLogTo(event.target.value); setPage(1) }} />
          </Field>
          <Field label="Project">
            <select value={projectId} onChange={(event) => { setProjectId(event.target.value); setPage(1) }}>
              <option value="">Semua</option>
              {(projects.data ?? []).map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Model">
            <select value={modelId} onChange={(event) => { setModelId(event.target.value); setPage(1) }}>
              <option value="">Semua</option>
              {(models.data ?? []).map((model) => (
                <option key={model.id} value={model.id}>
                  {model.alias}
                </option>
              ))}
            </select>
          </Field>
          <Field label="API key">
            <select value={keyId} onChange={(event) => { setKeyId(event.target.value); setPage(1) }}>
              <option value="">Semua</option>
              {(keys.data ?? []).map((key) => (
                <option key={key.id} value={key.id}>
                  {key.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Status">
            <select value={status} onChange={(event) => { setStatus(event.target.value); setPage(1) }}>
              <option value="">Semua</option>
              <option value="ok">ok</option>
              <option value="error">error</option>
              <option value="denied">denied</option>
            </select>
          </Field>
          <Field label="Baris per halaman">
            <select value={pageSize} onChange={(event) => { setPageSize(Number(event.target.value)); setPage(1) }}>
              <option value={25}>25</option>
              <option value={50}>50</option>
              <option value={100}>100</option>
              <option value={200}>200</option>
            </select>
          </Field>
          <button type="button" onClick={() => void exportCsv()}>
            Export CSV
          </button>
        </Toolbar>
        {logs.error ? <Alert>{logs.error}</Alert> : null}
        {logs.loading ? (
          <Loading />
        ) : (
          <>
            <DataTable head={['Waktu', 'Request', 'Status', 'HTTP', 'Alasan', 'Input', 'Output', 'Cache', 'Biaya', 'Latensi', 'Attempt', 'Project', 'Model', 'Key', canReadBody ? 'Isi' : '']}>
              {logs.data && logs.data.items.length > 0 ? (
                logs.data.items.map((log) => (
                  <tr key={log.id}>
                    <td>{fmtDateTime(log.createdAt)}</td>
                    <td className="mono">{log.requestId.slice(0, 8)}</td>
                    <td>
                      <span className={log.status === 'ok' ? 'badge ok' : 'badge off'}>{log.status}</span>
                    </td>
                    <td>{log.httpStatus}</td>
                    <td>{log.deniedReason ?? '—'}</td>
                    <td>{fmtNumber(log.inputTokens)}</td>
                    <td>{fmtNumber(log.outputTokens)}</td>
                    <td>{fmtNumber(log.cachedTokens)}</td>
                    <td>{fmtMoney(log.cost)}</td>
                    <td>{log.latencyMs} ms</td>
                    <td>{log.attempts}</td>
                    <td className="mono">{log.projectId ? log.projectId.slice(0, 8) : '—'}</td>
                    <td className="mono">{log.modelId ? log.modelId.slice(0, 8) : '—'}</td>
                    <td className="mono">{log.keyId ? log.keyId.slice(0, 8) : '—'}</td>
                    {canReadBody ? (
                      <td>
                        <button type="button" onClick={() => setBodyFor(log)}>
                          Lihat
                        </button>
                      </td>
                    ) : null}
                  </tr>
                ))
              ) : (
                <EmptyRow colSpan={canReadBody ? 15 : 14} />
              )}
            </DataTable>
            {logs.data ? <Pager page={logs.data.pageNumber} pageSize={logs.data.pageSize} total={logs.data.total} onPage={setPage} /> : null}
          </>
        )}
      </section>

      {bodyFor ? <UsageBodyModal log={bodyFor} onClose={() => setBodyFor(null)} /> : null}
    </>
  )
}

/** Privacy viewer: isi prompt/response hanya tersedia bila project menyimpan konten dan belum kedaluwarsa. */
function UsageBodyModal({ log, onClose }: { log: UsageLogItem; onClose: () => void }) {
  const body = useLoad(() => api.get<UsageBodyDto>(`/admin/api/usage/${log.id}/body`), [log.id])

  return (
    <Modal title={`Isi request — log #${log.id}`} onClose={onClose} wide>
      {body.error ? (
        <Alert>
          {body.error} <span className="muted">Isi hanya tersedia bila project menyimpan konten percakapan dan belum melewati masa retensi.</span>
        </Alert>
      ) : null}
      {body.loading ? (
        <Loading />
      ) : body.data ? (
        <>
          <p className="muted">Kedaluwarsa: {fmtDateTime(body.data.expiresAt)}</p>
          <h3>Request</h3>
          <pre className="detail-block">{prettyJson(body.data.requestJson)}</pre>
          <h3>Response</h3>
          <pre className="detail-block">{prettyJson(body.data.responseJson)}</pre>
        </>
      ) : null}
    </Modal>
  )
}

function prettyJson(value: string | null): string {
  if (!value) return '—'
  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

/* --------------------------------- Users ---------------------------------- */

export function UsersPage() {
  const users = useLoad(() => api.get<AdminUser[]>('/admin/api/users'), [])
  const [inviting, setInviting] = useState(false)
  const [inviteToken, setInviteToken] = useState<string | null>(null)
  const [resetToken, setResetToken] = useState<{ email: string; token: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)

  async function update(user: AdminUser, patch: { role?: Role; isActive?: boolean }) {
    setBusyId(user.id)
    setError(null)
    try {
      await api.patch(`/admin/api/users/${user.id}`, patch)
      users.reload()
    } catch (err) {
      setError(errorMessage(err))
      users.reload()
    } finally {
      setBusyId(null)
    }
  }

  async function resetPassword(user: AdminUser) {
    setBusyId(user.id)
    setError(null)
    try {
      const result = await api.post<{ token: string }>(`/admin/api/users/${user.id}/reset-password`)
      setResetToken({ email: user.email, token: result.token })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusyId(null)
    }
  }

  return (
    <>
      <PageHeader
        title="Pengguna"
        description="Kelola anggota tenant. Hanya owner yang dapat mengubah peran dan mengundang pengguna."
        actions={
          <button type="button" onClick={() => setInviting(true)}>
            Undang pengguna
          </button>
        }
      />
      {inviteToken ? <SecretPanel label="Token undangan" value={inviteToken} onDismiss={() => setInviteToken(null)} /> : null}
      {resetToken ? <SecretPanel label={`Token reset password untuk ${resetToken.email}`} value={resetToken.token} onDismiss={() => setResetToken(null)} /> : null}
      {error ? <Alert>{error}</Alert> : null}
      {users.error ? <Alert>{users.error}</Alert> : null}
      {users.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Email', 'Nama', 'Peran', 'Status', 'Login terakhir', 'Dibuat', 'Aksi']}>
          {users.data && users.data.length > 0 ? (
            users.data.map((member) => (
              <tr key={member.id}>
                <td>{member.email}</td>
                <td>{member.displayName}</td>
                <td>
                  <select
                    value={member.role}
                    disabled={busyId === member.id}
                    aria-label={`Peran ${member.email}`}
                    onChange={(event) => void update(member, { role: event.target.value as Role })}
                  >
                    {ROLES.map((role) => (
                      <option key={role} value={role}>
                        {role}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <label className="inline">
                    <input
                      type="checkbox"
                      checked={member.isActive}
                      disabled={busyId === member.id}
                      aria-label={`Aktif ${member.email}`}
                      onChange={(event) => void update(member, { isActive: event.target.checked })}
                    />
                    {member.isActive ? 'aktif' : 'nonaktif'}
                  </label>
                </td>
                <td>{fmtDateTime(member.lastLoginAt)}</td>
                <td>{fmtDateTime(member.createdAt)}</td>
                <td className="actions">
                  <button type="button" disabled={busyId === member.id} onClick={() => void resetPassword(member)}>
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
      {inviting ? (
        <InviteUserModal
          onClose={() => setInviting(false)}
          onSaved={(token) => {
            setInviting(false)
            setInviteToken(token)
            users.reload()
          }}
        />
      ) : null}
    </>
  )
}

function InviteUserModal({ onClose, onSaved }: { onClose: () => void; onSaved: (token: string) => void }) {
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [role, setRole] = useState<Role>('viewer')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await api.post<{ inviteToken: string }>('/admin/api/users/invite', { email: email.trim(), displayName, role })
      onSaved(result.inviteToken)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Undang pengguna" onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Email">
          <input type="email" required value={email} onChange={(event) => setEmail(event.target.value)} />
        </Field>
        <Field label="Nama tampilan">
          <input required value={displayName} onChange={(event) => setDisplayName(event.target.value)} />
        </Field>
        <Field label="Peran">
          <select value={role} onChange={(event) => setRole(event.target.value as Role)}>
            {ROLES.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </select>
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Mengirim…' : 'Buat undangan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* --------------------------------- Audit ---------------------------------- */

export function AuditPage() {
  const [from, setFrom] = useState(daysAgo(6) + 'T00:00')
  const [to, setTo] = useState(endOfTodayIso())
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const audit = useLoad(
    () =>
      api.get<Page<AuditItem>>('/admin/api/audit', {
        from: isoOrNull(from),
        to: isoOrNull(to),
        page,
        pageSize,
      }),
    [from, to, page, pageSize],
  )

  return (
    <>
      <PageHeader title="Audit" description="Jejak perubahan konfigurasi tenant." />
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
          <DataTable head={['Waktu', 'User', 'Action', 'Entity', 'Entity ID', 'IP', 'Detail']}>
            {audit.data && audit.data.items.length > 0 ? (
              audit.data.items.map((item) => (
                <tr key={item.id}>
                  <td>{fmtDateTime(item.createdAt)}</td>
                  <td>{item.userId ?? '—'}</td>
                  <td className="mono">{item.action}</td>
                  <td>{item.entity ?? '—'}</td>
                  <td className="mono">{item.entityId ?? '—'}</td>
                  <td>{item.ip ?? '—'}</td>
                  <td>
                    {item.detailJson ? (
                      <details>
                        <summary>lihat</summary>
                        <pre className="detail">{item.detailJson}</pre>
                      </details>
                    ) : (
                      '—'
                    )}
                  </td>
                </tr>
              ))
            ) : (
              <EmptyRow colSpan={7} />
            )}
          </DataTable>
          {audit.data ? <Pager page={audit.data.pageNumber} pageSize={audit.data.pageSize} total={audit.data.total} onPage={setPage} /> : null}
        </>
      )}
    </>
  )
}
