import { useCallback, useState, type FormEvent } from 'react'
import { api, downloadFile, errorMessage } from '../api'
import { canConfigure, useAuth } from '../auth'
import type { AdminUser, ApiKeyDto, AuditItem, HealthRow, ModelDto, Page, PolicyDto, ProjectDto, Role, SessionUsageRow, UsageBodyDto, UsageHealth, UsageLogItem, UsageRow } from '../types'
import { PROJECT_STATUSES, ROLES } from '../types'
import { Alert, DataTable, EmptyRow, Field, Loading, Modal, PageHeader, Pager, SecretPanel, Toolbar } from '../ui'
import {
  daysAgo,
  endOfTodayIso,
  firstOfMonth,
  fmtDate,
  fmtDateTime,
  fmtMoney,
  fmtNumber,
  isoOrNull,
  linesToArray,
  startOfTodayIso,
  today,
  useLoad,
} from '../ui-helpers'
import { toNumber } from './provider-form'

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

type UsageTotals = Record<'requests' | 'denied' | 'errors' | 'inputTokens' | 'outputTokens' | 'cost' | 'incompleteUsageRequests', number>

/** Strip metrik; `key` harus sama dengan nama field UsageRow. */
const METRICS: { key: keyof UsageTotals; label: string; format: (value: number) => string; cost?: boolean }[] = [
  { key: 'requests', label: 'Request', format: fmtNumber },
  { key: 'denied', label: 'Ditolak', format: fmtNumber },
  { key: 'errors', label: 'Error', format: fmtNumber },
  { key: 'inputTokens', label: 'Token input', format: fmtNumber },
  { key: 'outputTokens', label: 'Token output', format: fmtNumber },
  { key: 'cost', label: 'Biaya', format: fmtMoney, cost: true },
  // Token diketahui tetap dihitung; biaya dari usage incomplete bukan tagihan final.
  { key: 'incompleteUsageRequests', label: 'Usage belum lengkap', format: fmtNumber },
]

/** Token incomplete adalah batas bawah (≥); biaya tercatat bukan tagihan final (≈). */
const lowerBoundNote = 'Batas bawah: usage belum terbukti lengkap.'
const costNote = 'Biaya tercatat dari usage yang diketahui, bukan tagihan final: usage belum terbukti lengkap.'
const approx = (value: number, incomplete: boolean) => (incomplete ? `≥ ${fmtNumber(value)}` : fmtNumber(value))
const approxMoney = (value: number, incomplete: boolean) => (incomplete ? `≈ ${fmtMoney(value)}` : fmtMoney(value))

interface ChartSeries {
  label: string
  value: (row: UsageRow) => number
}

type ChartMetricId = 'requests' | 'tokens' | 'cost' | 'errors'

const CHART_METRICS: { id: ChartMetricId; label: string; series: ChartSeries[] }[] = [
  { id: 'requests', label: 'Request', series: [{ label: 'Request', value: (row) => row.requests }] },
  { id: 'tokens', label: 'Token', series: [{ label: 'Token (input + output)', value: (row) => row.inputTokens + row.outputTokens }] },
  { id: 'cost', label: 'Biaya', series: [{ label: 'Biaya', value: (row) => row.cost }] },
  {
    id: 'errors',
    label: 'Error + ditolak',
    series: [
      { label: 'Error', value: (row) => row.errors },
      { label: 'Ditolak', value: (row) => row.denied },
    ],
  },
]

/* Grafik memakai persentase untuk sumbu X supaya ikut lebar kartu tanpa mengukur DOM. */
const chartTop = 12
const chartBottom = 178
const chartLeft = 12
const chartRight = 98
const axisFormat = new Intl.NumberFormat(undefined, { notation: 'compact', maximumSignificantDigits: 2 })
const dayTickFormat = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' })

function dayTick(value: string): string {
  const date = new Date(`${value}T00:00:00`)
  return Number.isNaN(date.getTime()) ? value : dayTickFormat.format(date)
}

/** Langkah "cantik" 1/2/2,5/5×10^n supaya empat tick selalu menutupi nilai maksimum. */
function niceStep(raw: number): number {
  if (!(raw > 0)) return 1
  const magnitude = 10 ** Math.floor(Math.log10(raw))
  const normalized = raw / magnitude
  const step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10
  return step * magnitude
}

/** Titik polyline 112×24 seperti mockup; hanya dipanggil bila ada dua hari atau lebih. */
function sparkPoints(values: number[]): string {
  const min = Math.min(...values)
  const span = Math.max(...values) - min || 1
  return values.map((value, index) => `${((index / (values.length - 1)) * 108 + 2).toFixed(1)},${(22 - ((value - min) / span) * 20).toFixed(1)}`).join(' ')
}

function UsageChart({ rows, metric, label }: { rows: UsageRow[]; metric: ChartMetricId; label: string }) {
  const spec = CHART_METRICS.find((item) => item.id === metric) ?? CHART_METRICS[0]
  const max = Math.max(...rows.map((row) => Math.max(...spec.series.map((series) => series.value(row)))))
  const step = niceStep(max / 4)
  const top = step * 4
  const plot = chartBottom - chartTop
  const group = (chartRight - chartLeft) / rows.length
  const gap = spec.series.length > 1 ? group * 0.06 : 0
  const barWidth = (group * 0.62 - gap * (spec.series.length - 1)) / spec.series.length
  const labelEvery = Math.ceil(rows.length / 7)
  const money = metric === 'cost'

  return (
    <svg className="usage-chart" width="100%" height="210" role="img" aria-label={label}>
      {[0, 1, 2, 3, 4].map((tick) => {
        const y = chartBottom - (tick / 4) * plot
        return (
          <g key={tick}>
            <line className={tick === 0 ? 'chart-axis-line' : 'chart-grid-line'} x1={`${chartLeft}%`} x2={`${chartRight}%`} y1={y} y2={y} />
            <text className="chart-axis-label" x={`${chartLeft - 1}%`} y={y + 3} textAnchor="end">
              {axisFormat.format(step * tick)}
            </text>
          </g>
        )
      })}
      {rows.map((row, index) => {
        const cluster = barWidth * spec.series.length + gap * (spec.series.length - 1)
        const start = chartLeft + index * group + (group - cluster) / 2
        return (
          <g key={row.key}>
            {spec.series.map((series, seriesIndex) => {
              const value = series.value(row)
              const height = Math.max((value / top) * plot, 0)
              return (
                <rect
                  key={series.label}
                  className={seriesIndex === 0 ? 'chart-bar' : 'chart-bar-alt'}
                  x={`${(start + seriesIndex * (barWidth + gap)).toFixed(2)}%`}
                  y={chartBottom - height}
                  width={`${barWidth.toFixed(2)}%`}
                  height={height}
                >
                  <title>{`${row.label} · ${series.label}: ${money ? fmtMoney(value) : fmtNumber(value)}`}</title>
                </rect>
              )
            })}
            {index % labelEvery === 0 ? (
              <text className="chart-axis-label" x={`${(chartLeft + (index + 0.5) * group).toFixed(2)}%`} y={chartBottom + 16} textAnchor="middle">
                {dayTick(row.label)}
              </text>
            ) : null}
          </g>
        )
      })}
    </svg>
  )
}

export function DashboardPage() {
  const [range, setRange] = useState<RangeId>('today')
  const [metric, setMetric] = useState<ChartMetricId>('requests')
  const [asTable, setAsTable] = useState(false)
  const { from, to } = rangeParams(range)
  const daily = useLoad(useCallback(() => api.get<UsageRow[]>('/admin/api/usage/summary', { from, to, groupBy: 'day' }), [from, to]))
  const byModel = useLoad(useCallback(() => api.get<UsageRow[]>('/admin/api/usage/summary', { from, to, groupBy: 'model' }), [from, to]))

  const dayRows = daily.data ?? []
  const modelRows = byModel.data ?? []
  const totals = dayRows.reduce<UsageTotals>(
    (acc, row) => ({
      requests: acc.requests + row.requests,
      denied: acc.denied + row.denied,
      errors: acc.errors + row.errors,
      inputTokens: acc.inputTokens + row.inputTokens,
      outputTokens: acc.outputTokens + row.outputTokens,
      cost: acc.cost + row.cost,
      incompleteUsageRequests: acc.incompleteUsageRequests + row.incompleteUsageRequests,
    }),
    { requests: 0, denied: 0, errors: 0, inputTokens: 0, outputTokens: 0, cost: 0, incompleteUsageRequests: 0 },
  )
  const modelRequests = modelRows.reduce((sum, row) => sum + row.requests, 0)
  const chart = CHART_METRICS.find((item) => item.id === metric) ?? CHART_METRICS[0]

  return (
    <>
      <PageHeader title="Dashboard" description={`Pemakaian ${from} s/d ${to}.`} />
      <div className="dashboard-controls">
        <div className="inline">
          <span className="range-label">Rentang waktu</span>
          <div className="segmented" role="group" aria-label="Rentang waktu">
            {RANGES.map((item) => (
              <button key={item.id} type="button" className="ghost" aria-pressed={range === item.id} onClick={() => setRange(item.id)}>
                {item.label}
              </button>
            ))}
          </div>
        </div>
        <span className="hint">Semua nilai biaya dalam USD.</span>
      </div>

      <section className="metric-strip" aria-label="Ringkasan pemakaian">
        {METRICS.map((item) => (
          <div className="metric" key={item.key}>
            <span className="metric-label">{item.label}</span>
            <strong className={item.cost ? 'metric-value cost' : 'metric-value'}>
              {daily.loading ? <span className="skeleton" aria-hidden="true"></span> : daily.error ? '—' : item.format(totals[item.key])}
            </strong>
            {!daily.loading && !daily.error && dayRows.length > 1 ? (
              <svg className="metric-sparkline" viewBox="0 0 112 24" aria-hidden="true">
                <polyline points={sparkPoints(dayRows.map((row) => row[item.key]))} fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" strokeLinecap="round" />
              </svg>
            ) : null}
          </div>
        ))}
      </section>
      {!daily.loading && !daily.error && totals.incompleteUsageRequests > 0 ? (
        <p className="hint">
          {totals.incompleteUsageRequests} request pada rentang ini belum melaporkan usage lengkap: token adalah
          batas bawah yang diketahui; biaya tercatat berdasarkan token tersebut, bukan tagihan final.
        </p>
      ) : null}

      <section className="card">
        <h2>Tren pemakaian</h2>
        <div className="chart-heading">
          <div className="chart-tabs">
            <div className="segmented" role="group" aria-label="Metrik grafik">
              {CHART_METRICS.map((item) => (
                <button key={item.id} type="button" className="ghost" aria-pressed={metric === item.id} onClick={() => setMetric(item.id)}>
                  {item.label}
                </button>
              ))}
            </div>
          </div>
          <button type="button" className="ghost" onClick={() => setAsTable((value) => !value)}>
            {asTable ? 'Lihat sebagai grafik' : 'Lihat sebagai tabel'}
          </button>
        </div>
        {daily.error ? (
          <Alert>{daily.error}</Alert>
        ) : daily.loading ? (
          <Loading />
        ) : dayRows.length === 0 ? (
          <div className="empty-state">
            <strong>Belum ada pemakaian pada rentang ini.</strong>
          </div>
        ) : asTable ? (
          <DataTable head={['Hari', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya', 'Belum lengkap']}>
            {dayRows.map((row) => (
              <tr key={row.key}>
                <td>{row.label}</td>
                <td>{fmtNumber(row.requests)}</td>
                <td>{fmtNumber(row.denied)}</td>
                <td>{fmtNumber(row.errors)}</td>
                <td>{fmtNumber(row.inputTokens)}</td>
                <td>{fmtNumber(row.outputTokens)}</td>
                <td title={row.incompleteUsageRequests > 0 ? costNote : undefined}>
                  {approxMoney(row.cost, row.incompleteUsageRequests > 0)}
                </td>
                <td title={row.incompleteUsageRequests > 0 ? lowerBoundNote : undefined}>{fmtNumber(row.incompleteUsageRequests)}</td>
              </tr>
            ))}
          </DataTable>
        ) : (
          <div className="chart-container">
            <UsageChart rows={dayRows} metric={metric} label={`Grafik ${chart.label} per hari, ${from} s/d ${to}. Nilai harian yang sama tersedia lewat tombol "Lihat sebagai tabel".`} />
          </div>
        )}
      </section>

      <section className="card">
        <h2>Bauran model</h2>
        {byModel.error ? (
          <Alert>{byModel.error}</Alert>
        ) : byModel.loading ? (
          <Loading />
        ) : modelRows.length === 0 ? (
          <div className="empty-state">
            <strong>Belum ada pemakaian pada rentang ini.</strong>
          </div>
        ) : (
          <>
            <div className="share-list">
              {modelRows.map((row) => {
                const share = modelRequests > 0 ? (row.requests / modelRequests) * 100 : 0
                return (
                  <div className="share-item" key={row.key}>
                    <div className="share-head">
                      <span className="share-name mono">{row.label}</span>
                      <span className="share-value">
                        {fmtNumber(row.requests)} · {share.toFixed(1)}%
                      </span>
                    </div>
                    <div className="share-bar">
                      <div className="share-fill" style={{ width: `${share.toFixed(1)}%` }} />
                    </div>
                    <span className="share-meta">
                      {fmtNumber(row.inputTokens + row.outputTokens)} token · {fmtMoney(row.cost)}
                    </span>
                  </div>
                )
              })}
            </div>
            <details className="table-details">
              <summary>Nilai rinci per model</summary>
              <DataTable head={['Model', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya']}>
                {modelRows.map((row) => (
                  <tr key={row.key}>
                    <td>{row.label}</td>
                    <td>{fmtNumber(row.requests)}</td>
                    <td>{fmtNumber(row.denied)}</td>
                    <td>{fmtNumber(row.errors)}</td>
                    <td>{fmtNumber(row.inputTokens)}</td>
                    <td>{fmtNumber(row.outputTokens)}</td>
                    <td>{fmtMoney(row.cost)}</td>
                  </tr>
                ))}
              </DataTable>
            </details>
          </>
        )}
      </section>
    </>
  )
}

/* -------------------------------- Projects -------------------------------- */

export function ProjectsPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const projects = useLoad(useCallback(() => api.get<ProjectDto[]>('/admin/api/projects'), []))
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
      ) : projects.error ? null : (
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
        await api.post('/admin/api/projects', { name, logContent, contentRetentionDays: toNumber(contentRetentionDays) })
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
        {project ? (
          <Field label="Status" hint="Suspend menolak request baru pada project ini.">
            <select value={status} onChange={(event) => setStatus(event.target.value)}>
              {PROJECT_STATUSES.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </Field>
        ) : null}
        <label className="inline">
          <input type="checkbox" checked={logContent} onChange={(event) => setLogContent(event.target.checked)} /> Simpan log isi percakapan
        </label>
        <Field
          label="Retensi konten (hari)"
          hint={project ? 'Isi 1–3650 hari. Kosong = tidak diubah; centang opsi di bawah untuk menghapus.' : 'Kosongkan untuk tanpa retensi; isi 1–3650 hari.'}
        >
          <input type="number" min={1} disabled={clearRetention} value={contentRetentionDays} onChange={(event) => setContentRetentionDays(event.target.value)} />
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
  const projects = useLoad(useCallback(() => api.get<ProjectDto[]>('/admin/api/projects'), []))
  const keys = useLoad(useCallback(() => api.get<ApiKeyDto[]>('/admin/api/keys', { projectId: projectFilter || undefined }), [projectFilter]))
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
      {projects.error ? <Alert>{projects.error}</Alert> : null}
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
      ) : keys.error ? null : (
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
  const policies = useLoad(useCallback(() => api.get<PolicyDto[]>('/admin/api/policies'), []))
  const projects = useLoad(useCallback(() => api.get<ProjectDto[]>('/admin/api/projects'), []))
  const keys = useLoad(useCallback(() => api.get<ApiKeyDto[]>('/admin/api/keys'), []))
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
      {projects.error ? <Alert>{projects.error}</Alert> : null}
      {keys.error ? <Alert>{keys.error}</Alert> : null}
      <Toolbar>
        <Field label="Scope">
          <select
            value={scope}
            onChange={(event) => {
              setScope(event.target.value as 'tenant' | 'project' | 'key')
              setScopeId('')
            }}
          >
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
      {policies.error ? null : effectiveScopeId === '' && scope !== 'tenant' ? (
        <p className="muted">Pilih {scope === 'project' ? 'project' : 'API key'} untuk melihat policy-nya.</p>
      ) : (
        <PolicyEditor
          key={`${scope}:${effectiveScopeId}:${current ? 'set' : 'new'}`}
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
        ) : policies.error ? null : (
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
    const models = linesToArray(allowedModels)
    try {
      await api.put(`/admin/api/policies/${scope}/${scopeId}`, {
        maxTokensPerRequest: toNumber(maxTokensPerRequest),
        requestsPerMinute: toNumber(requestsPerMinute),
        dailyTokenQuota: toNumber(dailyTokenQuota),
        monthlyTokenQuota: toNumber(monthlyTokenQuota),
        monthlyBudget: toNumber(monthlyBudget),
        allowedModels: models.length > 0 ? models : null,
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
  const summary = useLoad(useCallback(() => api.get<UsageRow[]>('/admin/api/usage/summary', { from, to, groupBy }), [from, to, groupBy]))

  const [logFrom, setLogFrom] = useState(startOfTodayIso())
  const [logTo, setLogTo] = useState(endOfTodayIso())
  const [projectId, setProjectId] = useState('')
  const [modelId, setModelId] = useState('')
  const [keyId, setKeyId] = useState('')
  const [status, setStatus] = useState('')
  const [sessionId, setSessionId] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const projects = useLoad(useCallback(() => api.get<ProjectDto[]>('/admin/api/projects'), []))
  const models = useLoad(useCallback(() => api.get<ModelDto[]>('/admin/api/models'), []))
  const keys = useLoad(useCallback(() => api.get<ApiKeyDto[]>('/admin/api/keys'), []))
  const logs = useLoad(
    useCallback(
      () =>
        api.get<Page<UsageLogItem>>('/admin/api/usage/logs', {
          from: isoOrNull(logFrom),
          to: isoOrNull(logTo),
          projectId: projectId || undefined,
          modelId: modelId || undefined,
          keyId: keyId || undefined,
          status: status || undefined,
          sessionId: sessionId.trim() || undefined,
          page,
          pageSize,
        }),
      [logFrom, logTo, projectId, modelId, keyId, status, sessionId, page, pageSize],
    ),
  )
  // Sesi teratas memakai rentang dan filter log yang sama, tanpa filter sesi (itu yang sedang dicari).
  const sessions = useLoad(
    useCallback(
      () =>
        api.get<SessionUsageRow[]>('/admin/api/usage/sessions', {
          from: isoOrNull(logFrom),
          to: isoOrNull(logTo),
          projectId: projectId || undefined,
          modelId: modelId || undefined,
          keyId: keyId || undefined,
          status: status || undefined,
          limit: 10,
        }),
      [logFrom, logTo, projectId, modelId, keyId, status],
    ),
  )
  const [healthWindow, setHealthWindow] = useState<'1h' | '6h' | '24h' | '7d'>('24h')
  const health = useLoad(useCallback(() => api.get<UsageHealth>('/admin/api/usage/health', { window: healthWindow }), [healthWindow]))
  const [exportError, setExportError] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)

  async function exportCsv() {
    setExportError(null)
    setExporting(true)
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
          sessionId: sessionId.trim() || undefined,
        },
        `usage-${logFrom.slice(0, 10)}-${logTo.slice(0, 10)}.csv`,
      )
    } catch (err) {
      setExportError(errorMessage(err))
    } finally {
      setExporting(false)
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
        ) : summary.error ? null : (
          <DataTable head={[groupBy === 'day' ? 'Hari' : 'Nama', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya', 'Belum lengkap']}>
            {summary.data && summary.data.length > 0 ? (
              summary.data.map((row) => (
                <tr key={row.key}>
                  <td>{row.label}</td>
                  <td>{fmtNumber(row.requests)}</td>
                  <td>{fmtNumber(row.denied)}</td>
                  <td>{fmtNumber(row.errors)}</td>
                  <td>{fmtNumber(row.inputTokens)}</td>
                  <td>{fmtNumber(row.outputTokens)}</td>
                  <td title={row.incompleteUsageRequests > 0 ? costNote : undefined}>
                    {approxMoney(row.cost, row.incompleteUsageRequests > 0)}
                  </td>
                  <td title={row.incompleteUsageRequests > 0 ? lowerBoundNote : undefined}>
                    {fmtNumber(row.incompleteUsageRequests)}
                  </td>
                </tr>
              ))
            ) : (
              <EmptyRow colSpan={8} />
            )}
          </DataTable>
        )}
      </section>

      <section className="card">
        <h2>Kesehatan model dan provider</h2>
        <p className="muted">
          Diturunkan dari log request pada jendela terpilih, <strong>bukan probe aktif</strong>: provider yang tidak dipakai tidak
          muncul dan angkanya hanya berubah saat ada request. Hanya request yang diteruskan ke provider; pembatalan klien dan
          penolakan kebijakan tidak dihitung. Error = 5xx, 429, timeout, atau stream yang putus (4xx dari request klien tidak). Latensi
          dan TTFB dari request sukses. Provider dikaitkan ke jawaban akhir; percobaan yang gagal lalu dialihkan tidak tercatat sendiri.
        </p>
        <Toolbar>
          <Field label="Jendela">
            <select value={healthWindow} onChange={(event) => setHealthWindow(event.target.value as typeof healthWindow)}>
              <option value="1h">1 jam</option>
              <option value="6h">6 jam</option>
              <option value="24h">24 jam</option>
              <option value="7d">7 hari</option>
            </select>
          </Field>
        </Toolbar>
        {health.error ? <Alert>{health.error}</Alert> : null}
        {health.loading ? (
          <Loading />
        ) : health.error || !health.data ? null : (
          <>
            <HealthTable title="Per model" rows={health.data.models} minSample={health.data.minSample} />
            <HealthTable title="Per provider" rows={health.data.providers} minSample={health.data.minSample} />
          </>
        )}
      </section>

      <section className="card">
        <h2>Log request</h2>
        {exportError ? <Alert>{exportError}</Alert> : null}
        {projects.error ? <Alert>{projects.error}</Alert> : null}
        {models.error ? <Alert>{models.error}</Alert> : null}
        {keys.error ? <Alert>{keys.error}</Alert> : null}
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
          <Field label="Sesi">
            <input
              type="text"
              value={sessionId}
              maxLength={200}
              placeholder="id sesi persis"
              onChange={(event) => { setSessionId(event.target.value); setPage(1) }}
            />
          </Field>
          <Field label="Baris per halaman">
            <select value={pageSize} onChange={(event) => { setPageSize(Number(event.target.value)); setPage(1) }}>
              <option value={25}>25</option>
              <option value={50}>50</option>
              <option value={100}>100</option>
              <option value={200}>200</option>
            </select>
          </Field>
          <button type="button" onClick={() => void exportCsv()} disabled={exporting}>
            {exporting ? 'Mengekspor…' : 'Export CSV'}
          </button>
        </Toolbar>
        {logs.error ? <Alert>{logs.error}</Alert> : null}
        {logs.loading ? (
          <Loading />
        ) : logs.error ? null : (
          <>
            <DataTable head={['Waktu', 'Request', 'Status', 'HTTP', 'Alasan', 'Input', 'Output', 'Cache', 'Biaya', 'Latensi', 'Attempt', 'Sesi', 'Project', 'Model', 'Key', canReadBody ? 'Isi' : '']}>
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
                    <td title={log.usageIncomplete ? lowerBoundNote : undefined}>{approx(log.inputTokens, log.usageIncomplete)}</td>
                    <td title={log.usageIncomplete ? lowerBoundNote : undefined}>{approx(log.outputTokens, log.usageIncomplete)}</td>
                    <td title={log.usageIncomplete ? lowerBoundNote : undefined}>{approx(log.cachedTokens, log.usageIncomplete)}</td>
                    <td title={log.usageIncomplete ? costNote : undefined}>{approxMoney(log.cost, log.usageIncomplete)}</td>
                    <td>{log.latencyMs} ms</td>
                    <td>{log.attempts}</td>
                    <td className="mono" title={log.sessionId ?? undefined}>
                      {log.sessionId ? (
                        <button type="button" className="link" onClick={() => { setSessionId(log.sessionId ?? ''); setPage(1) }}>
                          {log.sessionId.length > 12 ? `${log.sessionId.slice(0, 8)}…` : log.sessionId}
                        </button>
                      ) : (
                        '—'
                      )}
                    </td>
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
                <EmptyRow colSpan={canReadBody ? 16 : 15} />
              )}
            </DataTable>
            {logs.data ? <Pager page={logs.data.pageNumber} pageSize={logs.data.pageSize} total={logs.data.total} onPage={setPage} /> : null}
          </>
        )}
      </section>

      <section className="card">
        <h2>Sesi teratas</h2>
        <p className="muted">
          Sepuluh sesi klien dengan biaya terbesar pada rentang dan filter log di atas, dihitung dari log request (mengikuti
          masa simpan log). Hanya request yang mengirim header <code>x-opencode-session</code>. Klik id sesi untuk memfilter log.
        </p>
        {sessions.error ? <Alert>{sessions.error}</Alert> : null}
        {sessions.loading ? (
          <Loading />
        ) : sessions.error ? null : (
          <DataTable head={['Sesi', 'Request', 'Ditolak', 'Error', 'Token input', 'Token output', 'Biaya', 'Belum lengkap', 'Terakhir']}>
            {sessions.data && sessions.data.length > 0 ? (
              sessions.data.map((row) => (
                <tr key={row.sessionId}>
                  <td className="mono" title={row.sessionId}>
                    <button type="button" className="link" onClick={() => { setSessionId(row.sessionId); setPage(1) }}>
                      {row.sessionId.length > 24 ? `${row.sessionId.slice(0, 20)}…` : row.sessionId}
                    </button>
                  </td>
                  <td>{fmtNumber(row.requests)}</td>
                  <td>{fmtNumber(row.denied)}</td>
                  <td>{fmtNumber(row.errors)}</td>
                  <td title={row.incompleteUsageRequests > 0 ? lowerBoundNote : undefined}>{approx(row.inputTokens, row.incompleteUsageRequests > 0)}</td>
                  <td title={row.incompleteUsageRequests > 0 ? lowerBoundNote : undefined}>{approx(row.outputTokens, row.incompleteUsageRequests > 0)}</td>
                  <td title={row.incompleteUsageRequests > 0 ? costNote : undefined}>{approxMoney(row.cost, row.incompleteUsageRequests > 0)}</td>
                  <td>{fmtNumber(row.incompleteUsageRequests)}</td>
                  <td>{fmtDateTime(row.lastAt)}</td>
                </tr>
              ))
            ) : (
              <EmptyRow colSpan={9} />
            )}
          </DataTable>
        )}
      </section>

      {bodyFor ? <UsageBodyModal log={bodyFor} onClose={() => setBodyFor(null)} /> : null}
    </>
  )
}

const pct = (value: number) => `${(value * 100).toFixed(1)}%`
const ms = (value: number | null) => (value === null ? '—' : `${fmtNumber(value)} ms`)

/** Tabel kesehatan; jendela tanpa request tampil "Tidak ada data", bukan 0% atau 100%. */
function HealthTable({ title, rows, minSample }: { title: string; rows: HealthRow[]; minSample: number }) {
  return (
    <>
      <h3>{title}</h3>
      <DataTable head={['Nama', 'Request', 'Error', 'Fallback', 'Latensi p50', 'Latensi p95', 'TTFB p50', 'TTFB p95', 'Sampel']}>
        {rows.length > 0 ? (
          rows.map((row) => (
            <tr key={row.id ?? row.label}>
              <td>{row.label}</td>
              <td>{fmtNumber(row.requests)}</td>
              <td title={`${fmtNumber(row.failures)} dari ${fmtNumber(row.requests)} request gagal di sisi provider`}>{pct(row.errorRate)}</td>
              <td title="Porsi request sukses yang dilayani lewat fallback">{row.fallbackRate === null ? '—' : pct(row.fallbackRate)}</td>
              <td>{ms(row.latencyP50Ms)}</td>
              <td>{ms(row.latencyP95Ms)}</td>
              <td>{ms(row.ttfbP50Ms)}</td>
              <td>{ms(row.ttfbP95Ms)}</td>
              <td>{row.smallSample ? <span className="badge off" title={`Kurang dari ${minSample} request: belum bisa diandalkan`}>sampel kecil</span> : 'cukup'}</td>
            </tr>
          ))
        ) : (
          <tr>
            <td colSpan={9}>Tidak ada data pada jendela ini.</td>
          </tr>
        )}
      </DataTable>
    </>
  )
}

/** Privacy viewer: isi prompt/response hanya tersedia bila project menyimpan konten dan belum kedaluwarsa. */
function UsageBodyModal({ log, onClose }: { log: UsageLogItem; onClose: () => void }) {
  const body = useLoad(useCallback(() => api.get<UsageBodyDto>(`/admin/api/usage/${log.id}/body`), [log.id]))

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
  const { user: currentUser } = useAuth()
  const users = useLoad(useCallback(() => api.get<AdminUser[]>('/admin/api/users'), []))
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
      ) : users.error ? null : (
        <DataTable head={['Email', 'Nama', 'Peran', 'Status', 'Login terakhir', 'Dibuat', 'Aksi']}>
          {users.data && users.data.length > 0 ? (
            users.data.map((member) => (
              <tr key={member.id}>
                <td>{member.email}</td>
                <td>{member.displayName}</td>
                <td>
                  <select
                    value={member.role}
                    disabled={busyId === member.id || member.id === currentUser?.id}
                    aria-label={`Peran ${member.email}`}
                    title={member.id === currentUser?.id ? 'Tidak bisa mengubah peran atau status akun sendiri.' : undefined}
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
                      disabled={busyId === member.id || member.id === currentUser?.id}
                      aria-label={`Aktif ${member.email}`}
                      title={member.id === currentUser?.id ? 'Tidak bisa mengubah peran atau status akun sendiri.' : undefined}
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
    useCallback(
      () =>
        api.get<Page<AuditItem>>('/admin/api/audit', {
          from: isoOrNull(from),
          to: isoOrNull(to),
          page,
          pageSize,
        }),
      [from, to, page, pageSize],
    ),
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
      ) : audit.error ? null : (
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
