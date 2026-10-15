import { useCallback, useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api'
import type { SearchProviderDto, SearchProviderStatusDto } from '../types'
import { Alert, DataTable, EmptyRow, Field, Loading, Modal, PageHeader } from '../ui'
import { fmtDateTime, fmtNumber, useLoad } from '../ui-helpers'

/** Cermin adapter terdaftar di server; base URI default juga ditentukan server bila kolom dikosongkan. */
const SEARCH_TYPES = [
  { value: 'tavily', label: 'Tavily', defaultBaseUri: 'https://api.tavily.com' },
  { value: 'exa', label: 'Exa', defaultBaseUri: 'https://api.exa.ai' },
  { value: 'firecrawl', label: 'Firecrawl', defaultBaseUri: 'https://api.firecrawl.dev' },
]

const RESET_POLICIES = [
  { value: 'first_of_month_utc', label: 'Awal bulan (UTC)' },
  { value: 'rolling_30_days', label: '30 hari bergulir' },
  { value: 'manual', label: 'Manual' },
]

const BASE = '/admin/providers'

function optionalNumber(text: string): number | undefined {
  const trimmed = text.trim()
  if (trimmed === '') return undefined
  const value = Number(trimmed)
  return Number.isFinite(value) ? value : Number.NaN
}

export function SearchProvidersPage() {
  const data = useLoad(
    useCallback(
      () => Promise.all([api.get<SearchProviderDto[]>(BASE), api.get<SearchProviderStatusDto[]>(`${BASE}/status`)]),
      [],
    ),
  )
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [formFor, setFormFor] = useState<SearchProviderDto | 'new' | null>(null)

  const providers = data.data?.[0] ?? []
  const statuses = new Map((data.data?.[1] ?? []).map((item) => [item.id, item]))

  async function run(provider: SearchProviderDto, action: () => Promise<void>) {
    setError(null)
    setNotice(null)
    setBusyId(provider.id)
    try {
      await action()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusyId(null)
      data.reload()
    }
  }

  const toggle = (provider: SearchProviderDto) =>
    run(provider, async () => {
      await api.post(`${BASE}/${provider.id}/${provider.isEnabled ? 'disable' : 'enable'}`)
    })

  const reset = (provider: SearchProviderDto) =>
    run(provider, async () => {
      await api.post(`${BASE}/${provider.id}/reset`)
      setNotice({ kind: 'success', text: `Status "${provider.name}" dibersihkan.` })
    })

  const test = (provider: SearchProviderDto) =>
    run(provider, async () => {
      const result = await api.post<{ success: boolean; message: string }>(`${BASE}/${provider.id}/test`)
      setNotice({ kind: result.success ? 'success' : 'error', text: `${provider.name}: ${result.message}` })
    })

  const remove = (provider: SearchProviderDto) => {
    if (!window.confirm(`Hapus provider "${provider.name}"? Pemakaian periode ini ikut disembunyikan.`)) return
    return run(provider, async () => {
      await api.del(`${BASE}/${provider.id}`)
    })
  }

  return (
    <>
      <PageHeader
        title="Web Search"
        description="Provider pencarian/ekstraksi web (Tavily, Exa, Firecrawl). Gateway memilih satu secara acak berbobot dan pindah otomatis bila kredit habis atau error. Key hanya tampil 4 karakter terakhir."
        actions={
          <button type="button" onClick={() => setFormFor('new')}>
            Tambah provider
          </button>
        }
      />
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert kind={notice.kind}>{notice.text}</Alert> : null}
      {data.error ? <Alert>{data.error}</Alert> : null}
      {data.loading ? (
        <Loading />
      ) : data.error ? null : (
        <DataTable head={['Nama', 'Tipe', 'Base URI', 'Key', 'Bobot', 'Status', 'Kredit periode', 'Sukses / Gagal / Failover', 'Error terakhir', 'Aksi']}>
          {providers.length > 0 ? (
            providers.map((provider) => {
              const status = statuses.get(provider.id)
              const available = status?.status === 'available'
              const busy = busyId === provider.id
              return (
                <tr key={provider.id}>
                  <td>{provider.name}</td>
                  <td>{provider.type}</td>
                  <td className="mono">{provider.baseUri}</td>
                  <td>{provider.keyHint ? `…${provider.keyHint}` : '—'}</td>
                  <td>{provider.weight}</td>
                  <td>
                    <span className={available ? 'badge ok' : 'badge off'}>{status?.status ?? '—'}</span>
                    {status?.nextRetryTime ? <div className="muted small">coba lagi {fmtDateTime(status.nextRetryTime)}</div> : null}
                  </td>
                  <td>
                    {status ? fmtNumber(status.usedThisPeriod) : '—'}
                    {provider.monthlyBudget !== null ? ` / ${fmtNumber(provider.monthlyBudget)}` : ''}
                    {status?.estimatedRemainingCredits != null ? (
                      <div className="muted small">sisa estimasi {fmtNumber(status.estimatedRemainingCredits)}</div>
                    ) : null}
                  </td>
                  <td>
                    {status ? `${fmtNumber(status.successCount)} / ${fmtNumber(status.failureCount)} / ${fmtNumber(status.failoverCount)}` : '—'}
                  </td>
                  <td className="small">{status?.lastError ?? '—'}</td>
                  <td className="actions">
                    <button type="button" disabled={busy} onClick={() => setFormFor(provider)}>
                      Edit
                    </button>
                    <button type="button" disabled={busy} onClick={() => void toggle(provider)}>
                      {provider.isEnabled ? 'Nonaktifkan' : 'Aktifkan'}
                    </button>
                    <button type="button" disabled={busy} onClick={() => void test(provider)}>
                      Test
                    </button>
                    <button type="button" disabled={busy} onClick={() => void reset(provider)}>
                      Reset
                    </button>
                    <button type="button" className="danger" disabled={busy} onClick={() => void remove(provider)}>
                      Hapus
                    </button>
                  </td>
                </tr>
              )
            })
          ) : (
            <EmptyRow colSpan={10} />
          )}
        </DataTable>
      )}
      <p className="hint">
        Kredit adalah estimasi lokal, bukan saldo provider. Test memakai satu pencarian nyata (±1 kredit). Reset hanya membersihkan status error; pada
        kebijakan reset manual, reset juga memulai periode kredit baru.
      </p>

      {formFor ? (
        <SearchProviderModal
          provider={formFor === 'new' ? null : formFor}
          onClose={() => setFormFor(null)}
          onSaved={() => {
            setFormFor(null)
            data.reload()
          }}
        />
      ) : null}
    </>
  )
}

function SearchProviderModal({ provider, onClose, onSaved }: { provider: SearchProviderDto | null; onClose: () => void; onSaved: () => void }) {
  const editing = provider !== null
  const [type, setType] = useState(provider?.type ?? 'tavily')
  const [name, setName] = useState(provider?.name ?? '')
  const [baseUri, setBaseUri] = useState(provider?.baseUri ?? '')
  const [apiKey, setApiKey] = useState('')
  const [weight, setWeight] = useState(String(provider?.weight ?? 1))
  const [canSearch, setCanSearch] = useState(provider ? provider.capabilities.split(',').includes('search') : true)
  const [canExtract, setCanExtract] = useState(provider ? provider.capabilities.split(',').includes('extract') : true)
  const [budget, setBudget] = useState(provider?.monthlyBudget != null ? String(provider.monthlyBudget) : '')
  const [costSearch, setCostSearch] = useState(String(provider?.costPerSearch ?? 1))
  const [costExtract, setCostExtract] = useState(String(provider?.costPerExtract ?? 1))
  const [resetPolicy, setResetPolicy] = useState(provider?.resetPolicy ?? 'first_of_month_utc')
  const [override, setOverride] = useState(provider?.errorMappingOverride ?? '')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const typeInfo = SEARCH_TYPES.find((item) => item.value === type)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    const capabilities = [canSearch ? 'search' : null, canExtract ? 'extract' : null].filter(Boolean).join(',')
    if (!capabilities) return setError('Pilih minimal satu kemampuan (search atau extract).')
    const numbers = { weight: optionalNumber(weight), budget: optionalNumber(budget), costSearch: optionalNumber(costSearch), costExtract: optionalNumber(costExtract) }
    if (Object.values(numbers).some((value) => Number.isNaN(value))) return setError('Bobot, anggaran, dan biaya harus berupa angka.')

    setBusy(true)
    try {
      if (editing) {
        await api.put(`${BASE}/${provider.id}`, {
          name: name || undefined,
          baseUri: baseUri || undefined,
          apiKey: apiKey || undefined,
          weight: numbers.weight,
          capabilities,
          monthlyBudget: numbers.budget,
          clearMonthlyBudget: numbers.budget === undefined && provider.monthlyBudget !== null ? true : undefined,
          costPerSearch: numbers.costSearch,
          costPerExtract: numbers.costExtract,
          resetPolicy,
          errorMappingOverride: override.trim() || undefined,
          clearErrorMappingOverride: !override.trim() && provider.errorMappingOverride ? true : undefined,
        })
      } else {
        await api.post(BASE, {
          type,
          name: name || undefined,
          baseUri: baseUri || undefined,
          apiKey,
          weight: numbers.weight,
          capabilities,
          monthlyBudget: numbers.budget,
          costPerSearch: numbers.costSearch,
          costPerExtract: numbers.costExtract,
          resetPolicy,
          errorMappingOverride: override.trim() || undefined,
        })
      }
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={editing ? `Edit ${provider.name}` : 'Tambah provider web search'} onClose={onClose} wide>
      <form onSubmit={submit} className="form">
        {error ? <Alert>{error}</Alert> : null}
        <Field label="Tipe" hint={editing ? 'Tipe tidak bisa diubah; buat provider baru bila perlu.' : undefined}>
          <select value={type} disabled={editing} onChange={(event) => setType(event.target.value)}>
            {SEARCH_TYPES.map((item) => (
              <option key={item.value} value={item.value}>
                {item.label}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Nama" hint="Opsional; kosong = nama tipe. Isi bila memakai beberapa akun dengan tipe sama.">
          <input value={name} placeholder={type} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Base URI" hint="Opsional; harus HTTPS publik. Kosong = bawaan tipe.">
          <input value={baseUri} placeholder={typeInfo?.defaultBaseUri ?? 'https://…'} onChange={(event) => setBaseUri(event.target.value)} />
        </Field>
        <Field label="API key" hint={editing ? 'Kosongkan bila tidak diganti. Mengganti key memulihkan status broken.' : 'Disimpan terenkripsi; tidak pernah ditampilkan kembali.'}>
          <input type="password" autoComplete="off" required={!editing} value={apiKey} onChange={(event) => setApiKey(event.target.value)} />
        </Field>
        <Field label="Bobot" hint="Makin besar makin sering dipilih (1–100).">
          <input type="number" min={1} max={100} step={1} value={weight} onChange={(event) => setWeight(event.target.value)} />
        </Field>
        <fieldset className="fieldset">
          <legend>Kemampuan</legend>
          <label className="inline">
            <input type="checkbox" checked={canSearch} onChange={(event) => setCanSearch(event.target.checked)} /> Search
          </label>
          <label className="inline">
            <input type="checkbox" checked={canExtract} onChange={(event) => setCanExtract(event.target.checked)} /> Extract
          </label>
        </fieldset>
        <Field label="Anggaran bulanan (kredit)" hint="Opsional. Provider dilewati saat pemakaian lokal mencapai 95%.">
          <input type="number" min={0} step="any" value={budget} onChange={(event) => setBudget(event.target.value)} />
        </Field>
        <Field label="Biaya per unit search" hint="Harga lokal per unit kredit provider (bawaan 1).">
          <input type="number" min={0} step="any" value={costSearch} onChange={(event) => setCostSearch(event.target.value)} />
        </Field>
        <Field label="Biaya per unit extract">
          <input type="number" min={0} step="any" value={costExtract} onChange={(event) => setCostExtract(event.target.value)} />
        </Field>
        <Field label="Kebijakan reset kredit">
          <select value={resetPolicy} onChange={(event) => setResetPolicy(event.target.value)}>
            {RESET_POLICIES.map((item) => (
              <option key={item.value} value={item.value}>
                {item.label}
              </option>
            ))}
          </select>
        </Field>
        <details>
          <summary>Lanjutan: aturan error kustom (JSON)</summary>
          <Field
            label="Error mapping override"
            hint='Kunci: exhaustedStatusCodes, exhaustedBodySubstrings, rateLimitStatusCodes, brokenStatusCodes. Contoh: {"exhaustedStatusCodes":[402]}'
          >
            <textarea rows={3} value={override} onChange={(event) => setOverride(event.target.value)} />
          </Field>
        </details>
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
