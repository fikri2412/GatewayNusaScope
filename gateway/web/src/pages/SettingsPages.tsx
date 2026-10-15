import { useCallback, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api'
import { canConfigure, useAuth } from '../auth'
import type { AppStatusCounts, AppStatusItem, AppStatusList, AppStatusSupportedKey, SettingDto, SettingType } from '../types'
import { SETTING_TYPES } from '../types'
import { Alert, DataTable, EmptyRow, Field, Loading, Modal, PageHeader, Toolbar } from '../ui'
import { fmtDateTime, fmtNumber, useLoad, type Loaded } from '../ui-helpers'

// Cermin validator server supaya kesalahan terlihat sebelum request.
const NAMESPACE_PATTERN = /^[a-z0-9][a-z0-9._-]{0,49}$/
const KEY_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,99}$/
const INT_MIN = -2147483648
const INT_MAX = 2147483647
const VALUE_MAX = 2000
const DESCRIPTION_MAX = 200
const PREVIEW_MAX = 80

export function SettingsPage() {
  const { user } = useAuth()
  const writable = !!user && canConfigure(user)
  const [filter, setFilter] = useState('')
  const namespace = filter.trim()
  const settings = useLoad(useCallback(() => api.get<SettingDto[]>('/admin/api/settings', { namespace: namespace || undefined }), [namespace]))
  const status = useLoad(useCallback(() => api.get<AppStatusList>('/admin/api/app-status', { namespace: namespace || undefined }), [namespace]))
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<SettingDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function remove(setting: SettingDto) {
    if (!window.confirm(`Hapus pengaturan "${setting.namespace}/${setting.key}"?`)) return
    setError(null)
    try {
      await api.del(`/admin/api/settings/${encodeURIComponent(setting.namespace)}/${encodeURIComponent(setting.key)}`)
      settings.reload()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <>
      <PageHeader
        title="Pengaturan"
        description="Nilai key/value per tenant. Nilai disimpan polos dan tampil di UI — bukan tempat untuk menyimpan secret."
        actions={
          writable ? (
            <button type="button" onClick={() => setCreating(true)}>
              Tambah pengaturan
            </button>
          ) : null
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      <Toolbar>
        <Field label="Filter namespace" hint="Kosongkan untuk menampilkan semua namespace.">
          <input type="search" placeholder="mis. billing" value={filter} onChange={(event) => setFilter(event.target.value)} />
        </Field>
      </Toolbar>
      <AppStatusSection status={status} />
      {settings.error ? <Alert>{settings.error}</Alert> : null}
      {settings.loading ? (
        <Loading />
      ) : settings.error ? null : (
        <DataTable head={['Namespace', 'Key', 'Nilai', 'Tipe', 'Deskripsi', 'Diubah', 'Aksi']}>
          {settings.data && settings.data.length > 0 ? (
            settings.data.map((setting) => (
              <tr key={`${setting.namespace}/${setting.key}`}>
                <td className="mono">{setting.namespace}</td>
                <td className="mono">{setting.key}</td>
                <td className="mono" title={setting.value}>
                  {previewValue(setting.value)}
                </td>
                <td>{setting.valueType}</td>
                <td>{setting.description ?? '—'}</td>
                <td>{fmtDateTime(setting.updatedAt)}</td>
                <td className="actions">
                  {writable ? (
                    <>
                      <button type="button" onClick={() => setEditing(setting)}>
                        Ubah
                      </button>
                      <button type="button" className="danger" onClick={() => void remove(setting)}>
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
        <SettingModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            settings.reload()
          }}
        />
      ) : null}
      {editing ? (
        <SettingModal
          setting={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            settings.reload()
          }}
        />
      ) : null}
    </>
  )
}

function SettingModal({ setting, onClose, onSaved }: { setting?: SettingDto; onClose: () => void; onSaved: () => void }) {
  const [namespace, setNamespace] = useState(setting?.namespace ?? '')
  const [key, setKey] = useState(setting?.key ?? '')
  const [valueType, setValueType] = useState<SettingType>(setting?.valueType ?? 'text')
  const [value, setValue] = useState(setting?.value ?? '')
  const [description, setDescription] = useState(setting?.description ?? '')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  function changeType(next: SettingType) {
    setValueType(next)
    if (next === 'bool' && value !== 'true' && value !== 'false') setValue('false')
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    const problem = validateSetting(namespace, key, valueType, value, description)
    if (problem) {
      setError(problem)
      return
    }
    setBusy(true)
    setError(null)
    try {
      await api.put(`/admin/api/settings/${encodeURIComponent(namespace)}/${encodeURIComponent(key)}`, {
        value,
        valueType,
        description: description.trim() || null,
      })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={setting ? `Ubah pengaturan — ${setting.namespace}/${setting.key}` : 'Tambah pengaturan'} onClose={onClose}>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Namespace" hint="Huruf kecil/angka di awal; boleh memuat huruf kecil, angka, titik, garis bawah, dan strip. Maks 50 karakter.">
          <input required readOnly={!!setting} maxLength={50} value={namespace} onChange={(event) => setNamespace(event.target.value)} />
        </Field>
        <Field label="Key" hint="Huruf/angka di awal; boleh memuat huruf, angka, titik, garis bawah, titik dua, dan strip. Maks 100 karakter.">
          <input required readOnly={!!setting} maxLength={100} value={key} onChange={(event) => setKey(event.target.value)} />
        </Field>
        <Field label="Tipe">
          <select value={valueType} onChange={(event) => changeType(event.target.value as SettingType)}>
            {SETTING_TYPES.map((type) => (
              <option key={type} value={type}>
                {type}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Nilai" hint={valueType === 'text' ? 'Disimpan polos dan tampil di UI — jangan simpan secret di sini.' : undefined}>
          {valueType === 'bool' ? (
            <select value={value === 'true' ? 'true' : 'false'} onChange={(event) => setValue(event.target.value)}>
              <option value="true">true</option>
              <option value="false">false</option>
            </select>
          ) : valueType === 'int' ? (
            <input type="number" step={1} min={INT_MIN} max={INT_MAX} required value={value} onChange={(event) => setValue(event.target.value)} />
          ) : (
            <input maxLength={VALUE_MAX} value={value} onChange={(event) => setValue(event.target.value)} />
          )}
        </Field>
        <Field label="Deskripsi" hint="Opsional, maks 200 karakter.">
          <input maxLength={DESCRIPTION_MAX} value={description} onChange={(event) => setDescription(event.target.value)} />
        </Field>
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Menyimpan…' : 'Simpan pengaturan'}
          </button>
          <button type="button" className="ghost" onClick={onClose}>
            Batal
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** Cermin validator server (aturan validasi G6) supaya kesalahan terlihat sebelum request. */
function validateSetting(namespace: string, key: string, valueType: SettingType, value: string, description: string): string | null {
  if (!NAMESPACE_PATTERN.test(namespace)) return 'Namespace harus 1–50 karakter, diawali huruf kecil/angka, dan hanya memuat huruf kecil, angka, titik, garis bawah, atau strip.'
  if (!KEY_PATTERN.test(key)) return 'Key harus 1–100 karakter, diawali huruf/angka, dan hanya memuat huruf, angka, titik, garis bawah, titik dua, atau strip.'
  if (value.length > VALUE_MAX) return `Nilai maksimal ${VALUE_MAX} karakter.`
  if (valueType === 'int') {
    if (!/^-?\d+$/.test(value)) return 'Nilai tipe int harus bilangan bulat.'
    const number = Number(value)
    if (number < INT_MIN || number > INT_MAX) return `Nilai tipe int harus antara ${INT_MIN} dan ${INT_MAX}.`
  }
  if (valueType === 'bool' && value !== 'true' && value !== 'false') return 'Nilai tipe bool harus true atau false.'
  if (description.trim().length > DESCRIPTION_MAX) return `Deskripsi maksimal ${DESCRIPTION_MAX} karakter.`
  return null
}

function previewValue(value: string): string {
  return value.length > PREVIEW_MAX ? `${value.slice(0, PREVIEW_MAX)}…` : value
}

const SYNC_LABELS: Record<AppStatusItem['syncStatus'], string> = {
  local: 'Lokal — belum memakai snapshot gateway',
  synced: 'Tersinkron',
  rejected: 'Ditolak — memakai nilai sebelumnya',
  'last-known-good': 'Snapshot valid terakhir',
}

/**
 * Status runtime yang dilaporkan aplikasi klien (monitoring G6/6A1). Read-only untuk semua peran: perubahan tetap
 * lewat tabel pengaturan di bawah (owner/admin). Tidak ada laporan berarti "belum melapor" — bukan nol palsu.
 */
function AppStatusSection({ status }: { status: Loaded<AppStatusList> }) {
  const items = status.data?.items ?? []
  const scope = status.data?.namespace
  return (
    <section className="card">
      <h2>
        Status aplikasi
        <button type="button" className="ghost" onClick={status.reload}>
          Muat ulang
        </button>
      </h2>
      <p className="muted">
        Laporan terakhir yang dikirim aplikasi klien dengan API key-nya. <strong>Configured</strong> adalah nilai
        pengaturan gateway saat ini; <strong>applied</strong> adalah nilai yang dilaporkan aplikasi. Waktu diambil dari jam
        gateway.
      </p>
      {status.error ? <Alert>{status.error}</Alert> : null}
      {status.loading ? (
        <Loading />
      ) : status.error ? null : items.length === 0 ? (
        <Alert kind="info">
          Belum ada laporan status{scope ? ` untuk namespace “${scope}”` : ''}. Aplikasi mungkin belum pernah mengirim
          laporan atau belum dijalankan.
        </Alert>
      ) : (
        items.map((item) => <AppStatusPanel key={item.id} item={item} requestedVersion={status.data?.configuredVersion ?? null} />)
      )}
    </section>
  )
}

function AppStatusPanel({ item, requestedVersion }: { item: AppStatusItem; requestedVersion: string | null }) {
  const configured = requestedVersion ?? item.configuredVersion
  const compared = configured !== null && item.sourceSettingsVersion !== null
  return (
    <div className="card">
      <h3>
        <span className="mono">{item.apiKeyName ?? item.apiKeyId ?? 'API key'}</span>{' '}
        <span className={`badge ${item.stale ? 'off' : 'ok'}`}>{item.stale ? 'stale' : 'segar'}</span>{' '}
        <span className="badge">{SYNC_LABELS[item.syncStatus]}</span>
      </h3>
      <p className="muted small">
        namespace <span className="mono">{item.namespace}</span> · diterima {fmtDateTime(item.receivedAt)} · laporan
        aplikasi {fmtDateTime(item.observedAt)} · interval {item.reportIntervalSeconds} dtk
      </p>
      <DataTable head={['Versi', 'Nilai', 'Keterangan']}>
        <tr>
          <td>Configured (gateway)</td>
          <td className="mono small">{configured ?? '—'}</td>
          <td>{requestedVersion ? 'Pengaturan namespace ini saat dibaca' : 'Pengaturan namespace laporan saat dibaca'}</td>
        </tr>
        <tr>
          <td>Applied (laporan)</td>
          <td className="mono small">{item.sourceSettingsVersion ?? '—'}</td>
          <td>
            {!compared
              ? 'Aplikasi tidak melaporkan versi sumber'
              : configured === item.sourceSettingsVersion
                ? 'Sama dengan configured'
                : 'Berbeda dari configured — aplikasi belum memakai nilai terbaru'}
          </td>
        </tr>
        <tr>
          <td>Sinkron berhasil terakhir</td>
          <td>{fmtDateTime(item.lastConfigSuccessAt)}</td>
          <td>{item.reason ?? '—'}</td>
        </tr>
        <tr>
          <td>Hash kebijakan / prompt</td>
          <td className="mono small">
            {shortHash(item.policyHash)} / {shortHash(item.promptHash)}
          </td>
          <td>Hash yang dilaporkan aplikasi; isi prompt tidak pernah dikirim</td>
        </tr>
      </DataTable>
      <h3>Nilai efektif dilaporkan</h3>
      {Object.keys(item.effectiveValues).length === 0 ? (
        <p className="muted">Tidak ada nilai efektif yang dilaporkan.</p>
      ) : (
        <DataTable head={['Key', 'Nilai']}>
          {Object.entries(item.effectiveValues).map(([key, value]) => (
            <tr key={key}>
              <td className="mono">{key}</td>
              <td className="mono">{value}</td>
            </tr>
          ))}
        </DataTable>
      )}
      <h3>Antrean dan pekerjaan</h3>
      <DataTable head={['Bagian', 'Pending', 'Processing', 'Selesai', 'Gagal']}>
        <CountRow label="Queue" counts={item.queue} />
        <CountRow label="Job" counts={item.jobs} />
      </DataTable>
      <h3>Kebijakan pada antrean aktif</h3>
      {item.activePolicies.length === 0 ? (
        <p className="muted">Tidak ada pekerjaan Pending/Processing saat laporan dibuat.</p>
      ) : (
        <DataTable head={['Policy hash', 'Queue', 'Job']}>
          {item.activePolicies.map((policy) => (
            <tr key={policy.policyHash}>
              <td className="mono small">{policy.policyHash}</td>
              <td>{fmtNumber(policy.queueItems)}</td>
              <td>{fmtNumber(policy.jobs)}</td>
            </tr>
          ))}
        </DataTable>
      )}
      {item.rejectedKeys.length > 0 ? <Alert kind="warn">Key ditolak aplikasi: {item.rejectedKeys.join(', ')}</Alert> : null}
      {item.ignoredKeys.length > 0 ? <Alert kind="info">Key diabaikan aplikasi (tidak didukung): {item.ignoredKeys.join(', ')}</Alert> : null}
      {item.supportedKeys.length > 0 ? (
        <details className="table-details">
          <summary>Key yang didukung aplikasi ({item.supportedKeys.length})</summary>
          <DataTable head={['Key', 'Tipe', 'Batas menurut aplikasi', 'Deskripsi']}>
            {item.supportedKeys.map((key) => (
              <tr key={key.key}>
                <td className="mono">{key.key}</td>
                <td>{key.valueType}</td>
                <td>{bounds(key)}</td>
                <td>
                  {key.description ?? '—'}
                  {key.redacted ? ' (nilai tidak dikirim)' : ''}
                </td>
              </tr>
            ))}
          </DataTable>
        </details>
      ) : null}
    </div>
  )
}

function CountRow({ label, counts }: { label: string; counts: AppStatusCounts }) {
  return (
    <tr>
      <td>{label}</td>
      <td>{fmtNumber(counts.pending)}</td>
      <td>{fmtNumber(counts.processing)}</td>
      <td>{fmtNumber(counts.done)}</td>
      <td>{fmtNumber(counts.failed)}</td>
    </tr>
  )
}

/** Bantuan input dari metadata aplikasi (advisory); validasi sebenarnya tetap di server dan worker. */
function bounds(key: AppStatusSupportedKey): string {
  const parts: string[] = []
  if (key.allowedValues && key.allowedValues.length > 0) parts.push(key.allowedValues.join(' | '))
  if (key.min !== null && key.max !== null) parts.push(`${key.min}–${key.max}`)
  if (key.maxLength !== null) parts.push(`maks ${key.maxLength} karakter`)
  return parts.length > 0 ? parts.join(' · ') : '—'
}

function shortHash(hash: string): string {
  return hash.length > 16 ? `${hash.slice(0, 16)}…` : hash
}
