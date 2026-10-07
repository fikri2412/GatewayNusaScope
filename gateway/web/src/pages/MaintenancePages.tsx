import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api'
import { canConfigure, useAuth } from '../auth'
import type { AlertEventDto, AlertRuleDto, ApiKeyDto, DeliveryDto, ProjectDto, WebhookDto } from '../types'
import { ALERT_METRICS, ALERT_SCOPES } from '../types'
import {
  Alert,
  DataTable,
  EmptyRow,
  Field,
  fmtDateTime,
  Loading,
  Modal,
  PageHeader,
  useLoad,
} from '../ui'

/* --------------------------------- Alerts --------------------------------- */

export function AlertsPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const alerts = useLoad(() => api.get<AlertRuleDto[]>('/admin/api/alerts'), [])
  const events = useLoad(() => api.get<AlertEventDto[]>('/admin/api/alerts/events', { limit: 50 }), [])
  const webhooks = useLoad(() => api.get<WebhookDto[]>('/admin/api/webhooks'), [])
  const projects = useLoad(() => api.get<ProjectDto[]>('/admin/api/projects'), [])
  const keys = useLoad(() => api.get<ApiKeyDto[]>('/admin/api/keys'), [])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<AlertRuleDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(rule: AlertRuleDto) {
    if (!window.confirm(`Hapus alert "${rule.name}"? Riwayat event rule ini ikut terhapus.`)) return
    setError(null)
    try {
      await api.del(`/admin/api/alerts/${rule.id}`)
      alerts.reload()
      events.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  const webhookName = (id: string | null) => (id ? (webhooks.data ?? []).find((hook) => hook.id === id)?.name ?? id.slice(0, 8) : '—')
  const scopeLabel = (scope: string, scopeId: string | null) => {
    if (scope === 'tenant') return 'tenant'
    if (!scopeId) return scope
    const name = scope === 'project' ? (projects.data ?? []).find((project) => project.id === scopeId)?.name : (keys.data ?? []).find((key) => key.id === scopeId)?.name
    return `${scope}: ${name ?? scopeId.slice(0, 8)}`
  }

  return (
    <>
      <PageHeader
        title="Alerts"
        description="Notifikasi kuota berbasis persentase pemakaian. Disarankan dua rule: 80 (peringatan) dan 100 (kritikal)."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreating(true)}>
              Tambah alert
            </button>
          ) : null
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {alerts.error ? <Alert>{alerts.error}</Alert> : null}
      {alerts.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'Metrik', 'Scope', 'Ambang', 'Webhook', 'Terakhir dipicu', 'Status', 'Aksi']}>
          {alerts.data && alerts.data.length > 0 ? (
            alerts.data.map((rule) => (
              <tr key={rule.id}>
                <td>{rule.name}</td>
                <td className="mono">{rule.metric}</td>
                <td>{scopeLabel(rule.scope, rule.scopeId)}</td>
                <td>{rule.thresholdPercent}%</td>
                <td>{webhookName(rule.webhookId)}</td>
                <td>{fmtDateTime(rule.lastTriggeredAt)}</td>
                <td>
                  <span className={rule.enabled ? 'badge ok' : 'badge off'}>{rule.enabled ? 'aktif' : 'nonaktif'}</span>
                </td>
                <td className="actions">
                  {writable ? (
                    <>
                      <button type="button" onClick={() => setEditing(rule)}>
                        Edit
                      </button>
                      <button type="button" className="danger" onClick={() => void remove(rule)}>
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
            <EmptyRow colSpan={8} />
          )}
        </DataTable>
      )}

      <section className="card">
        <h2>Event terakhir</h2>
        {events.error ? <Alert>{events.error}</Alert> : null}
        {events.loading ? (
          <Loading />
        ) : (
          <DataTable head={['Waktu', 'Rule', 'Metrik', 'Scope', 'Ambang', 'Periode', 'Terpakai', 'Batas']}>
            {events.data && events.data.length > 0 ? (
              events.data.map((event, index) => (
                <tr key={`${event.ruleId}:${event.periodStart}:${index}`}>
                  <td>{fmtDateTime(event.createdAt)}</td>
                  <td>{event.ruleName}</td>
                  <td className="mono">{event.metric}</td>
                  <td>{event.scope}</td>
                  <td>{event.thresholdPercent}%</td>
                  <td>{fmtDateTime(event.periodStart)}</td>
                  <td>{event.observed.toLocaleString()}</td>
                  <td>{event.limit.toLocaleString()}</td>
                </tr>
              ))
            ) : (
              <EmptyRow colSpan={8} label="Belum ada event." />
            )}
          </DataTable>
        )}
      </section>

      {creating ? (
        <AlertRuleModal
          projects={projects.data ?? []}
          keys={keys.data ?? []}
          webhooks={webhooks.data ?? []}
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            alerts.reload()
          }}
        />
      ) : null}
      {editing ? (
        <AlertRuleModal
          rule={editing}
          projects={projects.data ?? []}
          keys={keys.data ?? []}
          webhooks={webhooks.data ?? []}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            alerts.reload()
          }}
        />
      ) : null}
    </>
  )
}

function AlertRuleModal({
  rule,
  projects,
  keys,
  webhooks,
  onClose,
  onSaved,
}: {
  rule?: AlertRuleDto
  projects: ProjectDto[]
  keys: ApiKeyDto[]
  webhooks: WebhookDto[]
  onClose: () => void
  onSaved: () => void
}) {
  const [name, setName] = useState(rule?.name ?? '')
  const [metric, setMetric] = useState(rule?.metric ?? 'monthly_tokens')
  const [scope, setScope] = useState(rule?.scope ?? 'tenant')
  const [scopeId, setScopeId] = useState(rule?.scopeId ?? '')
  const [thresholdPercent, setThresholdPercent] = useState(rule ? String(rule.thresholdPercent) : '80')
  const [webhookId, setWebhookId] = useState(rule?.webhookId ?? '')
  const [clearWebhook, setClearWebhook] = useState(false)
  const [enabled, setEnabled] = useState(rule?.enabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    const threshold = Number(thresholdPercent)
    if (!Number.isInteger(threshold) || threshold < 1 || threshold > 100) {
      setError('Ambang harus bilangan bulat 1–100.')
      return
    }
    if (scope !== 'tenant' && !scopeId) {
      setError('Pilih project atau API key untuk scope ini.')
      return
    }
    setBusy(true)
    setError(null)
    const body = {
      name,
      metric,
      scope,
      scopeId: scope === 'tenant' ? null : scopeId,
      thresholdPercent: threshold,
      webhookId: clearWebhook || !webhookId ? null : webhookId,
      clearWebhook,
      enabled,
    }
    try {
      if (rule) await api.patch(`/admin/api/alerts/${rule.id}`, body)
      else await api.post('/admin/api/alerts', body)
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={rule ? `Edit alert — ${rule.name}` : 'Tambah alert'} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Metrik">
          <select value={metric} onChange={(event) => setMetric(event.target.value)}>
            {ALERT_METRICS.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Scope">
          <select
            value={scope}
            onChange={(event) => {
              setScope(event.target.value)
              setScopeId('')
            }}
          >
            {ALERT_SCOPES.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </select>
        </Field>
        {scope === 'project' ? (
          <Field label="Project">
            <select value={scopeId} onChange={(event) => setScopeId(event.target.value)}>
              <option value="">Pilih project…</option>
              {projects.map((project) => (
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
              <option value="">Pilih API key…</option>
              {keys.map((key) => (
                <option key={key.id} value={key.id}>
                  {key.name} ({key.keyPrefix}…)
                </option>
              ))}
            </select>
          </Field>
        ) : null}
        <Field label="Ambang (%)" hint="1–100. 80 = peringatan, 100 = kritikal.">
          <input type="number" min={1} max={100} required value={thresholdPercent} onChange={(event) => setThresholdPercent(event.target.value)} />
        </Field>
        <Field label="Webhook" hint={webhooks.length === 0 ? 'Belum ada webhook; alert tetap tercatat di daftar event.' : undefined}>
          <select disabled={clearWebhook} value={clearWebhook ? '' : webhookId} onChange={(event) => setWebhookId(event.target.value)}>
            <option value="">(tanpa webhook)</option>
            {webhooks.map((hook) => (
              <option key={hook.id} value={hook.id}>
                {hook.name}
              </option>
            ))}
          </select>
        </Field>
        {rule?.webhookId ? (
          <label className="inline">
            <input type="checkbox" checked={clearWebhook} onChange={(event) => setClearWebhook(event.target.checked)} /> Lepas webhook dari rule ini
          </label>
        ) : null}
        <label className="inline">
          <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Rule aktif
        </label>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan alert'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/* -------------------------------- Webhooks -------------------------------- */

export function WebhooksPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const webhooks = useLoad(() => api.get<WebhookDto[]>('/admin/api/webhooks'), [])
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<WebhookDto | null>(null)
  const [deliveriesFor, setDeliveriesFor] = useState<WebhookDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(hook: WebhookDto) {
    if (!window.confirm(`Hapus webhook "${hook.name}"? Alert yang memakainya akan dilepas dan riwayat pengiriman dihapus.`)) return
    setError(null)
    try {
      await api.del(`/admin/api/webhooks/${hook.id}`)
      webhooks.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Webhooks"
        description="Notifikasi keluar saat ambang alert terlampaui; payload ditandatangani HMAC-SHA256 (header X-Gateway-Signature)."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreating(true)}>
              Tambah webhook
            </button>
          ) : null
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {webhooks.error ? <Alert>{webhooks.error}</Alert> : null}
      {webhooks.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Nama', 'URL', 'Secret', 'Status', 'Diubah', 'Aksi']}>
          {webhooks.data && webhooks.data.length > 0 ? (
            webhooks.data.map((hook) => (
              <tr key={hook.id}>
                <td>{hook.name}</td>
                <td className="mono small">{hook.url}</td>
                <td className="mono">…{hook.secretHint}</td>
                <td>
                  <span className={hook.enabled ? 'badge ok' : 'badge off'}>{hook.enabled ? 'aktif' : 'nonaktif'}</span>
                </td>
                <td>{fmtDateTime(hook.updatedAt)}</td>
                <td className="actions">
                  <button type="button" onClick={() => setDeliveriesFor(hook)}>
                    Pengiriman
                  </button>
                  {writable ? (
                    <>
                      <button type="button" onClick={() => setEditing(hook)}>
                        Edit
                      </button>
                      <button type="button" className="danger" onClick={() => void remove(hook)}>
                        Hapus
                      </button>
                    </>
                  ) : null}
                </td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={6} />
          )}
        </DataTable>
      )}
      {creating ? (
        <WebhookModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            webhooks.reload()
          }}
        />
      ) : null}
      {editing ? (
        <WebhookModal
          hook={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            webhooks.reload()
          }}
        />
      ) : null}
      {deliveriesFor ? <DeliveriesModal hook={deliveriesFor} onClose={() => setDeliveriesFor(null)} /> : null}
    </>
  )
}

function WebhookModal({ hook, onClose, onSaved }: { hook?: WebhookDto; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(hook?.name ?? '')
  const [url, setUrl] = useState(hook?.url ?? '')
  const [signingSecret, setSigningSecret] = useState('')
  const [enabled, setEnabled] = useState(hook?.enabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    const urlProblem = validateWebhookUrl(url)
    if (urlProblem) {
      setError(urlProblem)
      return
    }
    setBusy(true)
    setError(null)
    const body: Record<string, unknown> = { name, url, enabled }
    if (signingSecret) body.signingSecret = signingSecret
    try {
      if (hook) await api.patch(`/admin/api/webhooks/${hook.id}`, body)
      else {
        body.signingSecret = signingSecret
        await api.post('/admin/api/webhooks', body)
      }
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={hook ? `Edit webhook — ${hook.name}` : 'Tambah webhook'} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Nama">
          <input required value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="URL" hint="https://host/path saja — tanpa ?query, user@, atau #fragment. Host IP privat/loopback ditolak.">
          <input type="url" required pattern="https://.*" placeholder="https://contoh.com/hooks/gateway" value={url} onChange={(event) => setUrl(event.target.value)} />
        </Field>
        <Field label={hook ? 'Signing secret baru (opsional)' : 'Signing secret'} hint="16–200 karakter. Secret tidak pernah ditampilkan kembali setelah disimpan.">
          <input
            type="password"
            autoComplete="off"
            required={!hook}
            value={signingSecret}
            onChange={(event) => setSigningSecret(event.target.value)}
          />
        </Field>
        <label className="inline">
          <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} /> Webhook aktif
        </label>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan webhook'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

function DeliveriesModal({ hook, onClose }: { hook: WebhookDto; onClose: () => void }) {
  const deliveries = useLoad(() => api.get<DeliveryDto[]>(`/admin/api/webhooks/${hook.id}/deliveries`, { limit: 50 }), [hook.id])

  return (
    <Modal title={`Pengiriman — ${hook.name}`} onClose={onClose} wide>
      {deliveries.error ? <Alert>{deliveries.error}</Alert> : null}
      {deliveries.loading ? (
        <Loading />
      ) : (
        <DataTable head={['Dibuat', 'Status', 'Attempt', 'Kode', 'Kirim berikutnya', 'Selesai', 'Error']}>
          {deliveries.data && deliveries.data.length > 0 ? (
            deliveries.data.map((delivery) => (
              <tr key={delivery.id}>
                <td>{fmtDateTime(delivery.createdAt)}</td>
                <td>
                  <span className={delivery.status === 'succeeded' ? 'badge ok' : delivery.status === 'failed' ? 'badge off' : 'badge'}>{delivery.status}</span>
                </td>
                <td>{delivery.attempts}</td>
                <td>{delivery.responseStatus ?? '—'}</td>
                <td>{fmtDateTime(delivery.nextAttemptAt)}</td>
                <td>{fmtDateTime(delivery.deliveredAt)}</td>
                <td className="small">{delivery.lastError ?? '—'}</td>
              </tr>
            ))
          ) : (
            <EmptyRow colSpan={7} label="Belum ada pengiriman." />
          )}
        </DataTable>
      )}
    </Modal>
  )
}

/** Cermin validator server (OutboundSecurity) supaya kesalahan terlihat sebelum request. */
function validateWebhookUrl(value: string): string | null {
  let parsed: URL
  try {
    parsed = new URL(value.trim())
  } catch {
    return 'URL tidak valid.'
  }
  if (parsed.protocol !== 'https:') return 'URL harus memakai https.'
  if (parsed.username || parsed.password) return 'URL tidak boleh memuat userinfo (user@host).'
  if (parsed.search) return 'URL tidak boleh memuat query string (?key=…).'
  if (parsed.hash) return 'URL tidak boleh memuat fragment (#…).'
  return null
}
