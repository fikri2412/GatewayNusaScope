// Helper form provider/model/katalog. Nilai form disimpan sebagai string supaya kosong tetap bermakna "tanpa nilai".

/** Tier harga untuk prompt panjang; minInputTokens/input/output wajib, cache opsional. */
export interface TierForm {
  minInputTokens: string
  input: string
  output: string
  cacheRead: string
  cacheWrite: string
}

export const emptyTier = (): TierForm => ({ minInputTokens: '', input: '', output: '', cacheRead: '', cacheWrite: '' })

export function toNumber(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed === '') return null
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : null
}

/** Baris tier yang terisi sebagian akan hilang dari payload; tolak sebelum menyimpan. */
export function tierIssue(tiers: TierForm[]): string | null {
  const index = tiers.findIndex((tier) => {
    const started = [tier.minInputTokens, tier.input, tier.output, tier.cacheRead, tier.cacheWrite].some(
      (value) => value.trim() !== '',
    )
    const missing = [tier.minInputTokens, tier.input, tier.output].some((value) => value.trim() === '')
    return started && missing
  })
  return index === -1 ? null : `Tier ${index + 1} belum lengkap: isi mulai token input, harga input, dan harga output, atau kosongkan barisnya.`
}

/** Baris tier kosong (hasil "Tambah tier") dilewati; baris terisi sebagian dicegah lewat tierIssue. */
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
