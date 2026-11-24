import { call } from './api'
import type { Market } from './api'

export type ScanReq = { market: Market; symbols: string[]; timeframes: number[]; picks: string[]; variants: number; capital: number; costs?: { fee?: number; leverage?: number }; protect?: Record<string, number> }
export type ScanRow = {
    id: string; name: string; symbol: string; tf: number; params: string; capital: number; equity: number; pnlPct: number
    trades: number; closed: number; winRate: number | null; maxDd: number; stance: string; armed: boolean; ready: boolean
    bars: number; problem: string; lastSignalAt: number; lastSignal: string
    btDone: boolean; btReturn: number; btSharpe: number; btMaxDd: number; btTrades: number; btDays: number
}
export type ScanSignal = { t: number; cell: string; name: string; symbol: string; tf: number; params: string; from: string; to: string; price: number }
export type ScanSnap = {
    running: boolean; startedAt: number; req: ScanReq | null
    summary: { cells: number; ready: number; armed: number; inPosition: number; profitable: number; signals24h: number; maxCells: number }
    groups: { key: string; status: string }[]; cells: ScanRow[]; signals: ScanSignal[]
}

export const scannerApi = {
    get: () => call<ScanSnap>('/api/scanner'),
    preview: (r: ScanReq) => call<{ cells: number; groups: number; limit: number }>('/api/scanner/preview', r),
    start: (r: ScanReq) => call<{ ok: boolean }>('/api/scanner/start', r),
    stop: () => call<{ ok: boolean }>('/api/scanner/stop', {}),
    clear: () => call<{ ok: boolean }>('/api/scanner', undefined, 'DELETE'),
    promote: (id: string) => call<{ agentId: string }>(`/api/scanner/${id}/promote`, {}),
}