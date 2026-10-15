import { useCallback, useEffect, useState } from 'react'
import { errorMessage } from './api'

export interface Loaded<T> {
  data: T | null
  error: string | null
  loading: boolean
  reload: () => void
}

/** Fetch for a stable loader; never expose results from a previous filter or reload. */
export function useLoad<T>(loader: () => Promise<T>): Loaded<T> {
  const [tick, setTick] = useState(0)
  const [result, setResult] = useState<{
    loader: () => Promise<T>
    tick: number
    data: T | null
    error: string | null
  } | null>(null)
  const reload = useCallback(() => setTick((value) => value + 1), [])
  const current = result?.loader === loader && result.tick === tick ? result : null

  useEffect(() => {
    let active = true
    loader()
      .then((data) => {
        if (active) setResult({ loader, tick, data, error: null })
      })
      .catch((error: unknown) => {
        if (active) setResult({ loader, tick, data: null, error: errorMessage(error) })
      })
    return () => {
      active = false
    }
  }, [loader, tick])

  return { data: current?.data ?? null, error: current?.error ?? null, loading: current === null, reload }
}

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

export function fmtDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : dateTime.format(date)
}

export function fmtDate(value: string | null | undefined): string {
  if (!value) return '—'
  const date = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T00:00:00`) : new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString()
}

export function fmtNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toLocaleString()
}

export function fmtMoney(value: number | null | undefined, currency = 'USD'): string {
  if (value === null || value === undefined) return '—'
  return `${currency} ${value.toFixed(4)}`
}

export function today(): string {
  return new Date().toLocaleDateString('sv-SE')
}

export function daysAgo(days: number): string {
  const date = new Date()
  date.setDate(date.getDate() - days)
  return date.toLocaleDateString('sv-SE')
}

export function firstOfMonth(): string {
  const date = new Date()
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-01`
}

/** Local midnight; isoOrNull converts datetime-local input to an explicit UTC instant. */
export function startOfTodayIso(): string {
  return `${today()}T00:00`
}

/** Exclusive upper bound: next local midnight includes every instant of today. */
export function endOfTodayIso(): string {
  const date = new Date()
  date.setDate(date.getDate() + 1)
  return `${date.toLocaleDateString('sv-SE')}T00:00`
}

export function isoOrNull(value: string): string | null {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

export function linesToArray(text: string): string[] {
  return text
    .split(/[\n,]/)
    .map((line) => line.trim())
    .filter(Boolean)
}
