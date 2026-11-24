import { call } from './api'
import type { MarketRef, StrategyIn } from './api'

export type AgentRow = {
    id: string; name: string; color: string; role: string; data: MarketRef; capital: number
    strategy: StrategyIn; costs: AgentCosts | null
    state: string; running: boolean; price: number; target: number; account: Record<string, any>
    equity: number; pnl: number; realized: number; closed: number; wins: number; winRate: number | null
    signals: number; error: string; note: string; log: string[]; newestCandle: number | null; checkAgeSec: number | null; curve: number[][]
}
export type AgentFill = { t: number; agent: string; name: string; color: string; symbol: string; side: 'buy' | 'sell'; price: number; qty: number; fee: number }
export type AgentsSnap = {
    quote: string | null; sessionStart: number; now: number; max: number; agents: AgentRow[]; fills: AgentFill[]
    combined: {
        capital: number; equity: number; pnl: number; pnlPct: number; closed: number; wins: number; winRate: number | null
        signals: number; fillsTotal: number; active: number; total: number; conviction: number
        curve: number[][]; convictionCurve: number[][]
    }
}
export type AgentCosts = { fee?: number; leverage?: number }
export type AgentBody = { name?: string; data: MarketRef; strategy: StrategyIn; costs?: AgentCosts; capital: number }
export type PresetBody = { data: MarketRef; costs?: AgentCosts; capital: number; picks: string[]; replace?: boolean; protect?: Record<string, number> }

export const agentsApi = {
    list: () => call<AgentsSnap>('/api/agents'),
    add: (b: AgentBody) => call<{ id: string }>('/api/agents', b),
    preset: (b: PresetBody) => call<{ ids: string[] }>('/api/agents/preset', b),
    stop: (id: string) => call<{ ok: boolean }>(`/api/agents/${id}/stop`, {}),
    resume: (id: string) => call<{ ok: boolean }>(`/api/agents/${id}/resume`, {}),
    close: (id: string) => call<{ ok: boolean }>(`/api/agents/${id}/close`, {}),
    remove: (id: string) => call<{ ok: boolean }>(`/api/agents/${id}`, undefined, 'DELETE'),
    stopAll: () => call<{ ok: boolean }>('/api/agents/stopall', {}),
}