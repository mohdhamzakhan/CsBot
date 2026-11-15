import { useCallback, useEffect, useRef, useState } from 'react'
import type { Costs, MarketRef, ParamRange, StrategyIn } from './api'

// ---------- response shapes (match the C# records in CsBot/Research, camelCased) ----------
export type Dist = { p5: number; p25: number; p50: number; p75: number; p95: number; mean: number }
export type McResult = {
  runs: number; observations: number; horizon: number; probRuinPct: number; probLossPct: number
  finalReturnPct: Dist; maxDrawdownPct: Dist; cagrPct: Dist; samplePaths: number[][]; warnings: string[]
}
export type McOptions = { runs: number; mode: 'BlockBootstrap' | 'Shuffle'; blockSize: number; ruinDrawdownPct: number; samplePaths: number }

export type SensResult = {
  axisNames: string[]; axisValues: number[][]; dims: number[]; scores: number[]; valid: boolean[]
  bestParams: Record<string, number>; bestScore: number; stableParams: Record<string, number>; stableScore: number; stableRawScore: number
  spikeRatio: number; bestIsFragile: boolean; plateauShare: number; heatmap: number[][] | null
}

export type WfaOptions = {
  inSampleBars: number; outSampleBars: number; stepBars: number; anchored: boolean; warmup: number
  minTrades: number; smoothRadius: number; objective: string; maxCombos: number
}
export type WfaWindow = {
  index: number; isStart: number; isEnd: number; oosStart: number; oosEnd: number; params: Record<string, number>
  isScore: number | null; isReturnPct: number | null; oosReturnPct: number; oosSharpe: number; oosMaxDdPct: number; oosTrades: number; bestWasFragile: boolean
}
export type WfaResult = {
  windows: WfaWindow[]; combinedOosReturnPct: number; combinedOosSharpe: number; combinedOosMaxDdPct: number
  walkForwardEfficiency: number | null; profitableWindowsPct: number; paramInstability: Record<string, number>
  combinedOosEquity: number[]; passed: boolean; flags: string[]
}

export type JobState = 'queued' | 'running' | 'done' | 'failed' | 'cancelled'
export type Job<T> = { id: string; kind: string; state: JobState; progress: number; error: string | null; result: T | null }

async function call<T>(path: string, body?: unknown, method?: string): Promise<T> {
  const res = await fetch(path, {
    method: method ?? (body === undefined ? 'GET' : 'POST'),
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await res.text()
  let data: any = null
  try { data = text ? JSON.parse(text) : null } catch { /* not json */ }
  if (res.status === 401 && data?.login) window.dispatchEvent(new Event('csbot-login-required'))
  if (!res.ok) throw new Error(data?.error ?? `HTTP ${res.status}: ${text.slice(0, 200)}`)
  return data as T
}

export const research = {
  montecarlo: (data: MarketRef, strategy: StrategyIn, costs: Costs, options: McOptions) =>
    call<{ id: string }>('/api/research/montecarlo', { data, strategy, costs, options }),
  sensitivity: (data: MarketRef, strategy: StrategyIn, costs: Costs, ranges: ParamRange[], objective: string, minTrades: number, radius: number) =>
    call<{ id: string; combinations: number }>('/api/research/sensitivity', { data, strategy, costs, ranges, objective, minTrades, radius }),
  walkforward: (data: MarketRef, strategy: StrategyIn, costs: Costs, ranges: ParamRange[], wfa: WfaOptions) =>
    call<{ id: string; estimatedRuns: number }>('/api/research/walkforward', { data, strategy, costs, ranges, wfa }),
  job: <T,>(id: string) => call<Job<T>>(`/api/research/jobs/${id}`),
  cancel: (id: string) => call<{ cancelled: boolean }>(`/api/research/jobs/${id}`, undefined, 'DELETE'),
}

/** Starts a background job and polls it once a second until it finishes. */
export function useJob<T>() {
  const [job, setJob] = useState<Job<T> | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const timer = useRef<number | undefined>(undefined)
  const jobId = useRef('')

  const stop = () => { if (timer.current !== undefined) { window.clearInterval(timer.current); timer.current = undefined } }
  useEffect(() => () => stop(), [])

  const start = useCallback(async (starter: () => Promise<{ id: string }>) => {
    stop(); setError(''); setJob(null); setBusy(true)
    try {
      const { id } = await starter()
      jobId.current = id
      let finished = false
      const poll = async () => {
        try {
          const j = await research.job<T>(id)
          setJob(j)
          if (j.state === 'done' || j.state === 'failed' || j.state === 'cancelled') {
            finished = true; stop(); setBusy(false)
            if (j.state === 'failed') setError(j.error ?? 'The analysis failed.')
          }
        } catch (e: any) { finished = true; stop(); setBusy(false); setError(e.message) }
      }
      await poll()
      if (!finished) timer.current = window.setInterval(poll, 1000)
    } catch (e: any) { setBusy(false); setError(e.message) }
  }, [])

  const cancel = () => { if (jobId.current) research.cancel(jobId.current).catch(() => {}) }
  return { job, error, busy, start, cancel, result: job?.state === 'done' ? job.result : null, progress: job?.progress ?? 0, cancelled: job?.state === 'cancelled' }
}
