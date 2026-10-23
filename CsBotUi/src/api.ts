export type Market = 'spot' | 'futures'
export type KeysStatus = { canEditKeys: boolean; hasKeys: boolean; source: string | null; savePath: string; apiKeyPreview: string; canRemove: boolean }
export type Candle = { t: number; o: number; h: number; l: number; c: number; v: number }
export type Pt = { t: number; v: number }
export type LiveBar = { t: number; o: number; h: number; l: number; c: number }
export type LiveTick = {
  price: number; bid: number | null; ask: number | null; changePct: number | null; high: number | null; low: number | null; volume: number | null
  at: number; fundingRate: number | null; markPrice: number | null; nextFunding: number | null; bar: LiveBar | null
}
export type Overlay = { name: string; color: string; pane?: number; points: Pt[] }
export type FillPt = { t: number; side: 'buy' | 'sell'; price: number; qty: number }
export type ParamDef = { key: string; label: string; default: number; min: number; max: number; step: number }
export type StrategyDef = { id: string; name: string; description: string; evidence: string; params: ParamDef[] }

export type Operand = { kind: string; period?: number; value?: number }
export type Cond = { left: Operand; op: string; right: Operand }
export type RuleSet = {
  entryLong: Cond[]; exitLong: Cond[]; entryShort: Cond[]; exitShort: Cond[]
  stopLossPct: number; takeProfitPct: number; size: number
}

export type Costs = { fee: number; gst: number; slippage: number; tds: number; funding: number; leverage: number }
export type MarketRef = { market: Market; exchange: string; symbol: string; intervalMin: number; days: number }
export type StrategyIn = { id: string; params: Record<string, number>; rules?: RuleSet; code?: string }
export type ScriptInput = { name: string; label: string; default: number; min: number | null; max: number | null; step: number }
export type ScriptCheck = { ok: boolean; error?: string; line?: number; inputs?: ScriptInput[]; warmupBars?: number }
export type ScriptTemplate = { id: string; name: string; description: string; evidence: string; code: string }
export type SavedScript = { name: string; code: string }

export type Metrics = {
  name: string; startEquity: number; finalEquity: number | null; returnPct: number | null; postTaxReturnPct: number | null
  cagrPct: number | null; sharpe: number | null; maxDrawdownPct: number | null; trades: number; fees: number | null
  slippage: number | null; tdsWithheld: number | null; funding: number | null; turnover: number | null
  feeDragPct: number | null; estTax: number | null; busted: boolean
}
export type BacktestResp = {
  candles: Candle[]; overlays: Overlay[]; equity: Pt[]; benchEquity: Pt[]; fills: FillPt[]
  intervalSec: number; metrics: Metrics; benchmark: Metrics; walkForward: (number | null)[]; gate: { pass: boolean; reason: string }; warmupBars: number
}

export type TraderStatus = {
  state: string; running: boolean; mode?: string; symbol?: string; market?: string; strategy?: string; lastPrice?: number
  lastClose?: number; priceAgeSec?: number | null; priceError?: string; nextDecision?: number | null
  targetWeight?: number; account?: Record<string, number | boolean | string>
  fills?: { t: number; side: string; price: number; qty: number; fee: number }[]; log?: string[]
}

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

export const api = {
  health: () => call<{ hasKeys: boolean; keyMatchesSecret: boolean; error: string | null; liveEnabled: boolean; keySource: string | null; canEditKeys: boolean }>('/api/health'),
  authStatus: () => call<{ required: boolean; authed: boolean }>('/api/auth/status'),
  login: (password: string) => call<{ ok: boolean }>('/api/auth/login', { password }),
  logout: () => call<{ ok: boolean }>('/api/auth/logout', {}),
  keysStatus: () => call<KeysStatus>('/api/keys/status'),
  keysSave: (apiKey: string, secretKey: string) => call<{ saved: boolean; path: string; warning: string | null }>('/api/keys/save', { apiKey, secretKey }),
  keysRemove: () => call<{ removed: boolean }>('/api/keys', undefined, 'DELETE'),
  strategies: () => call<StrategyDef[]>('/api/strategies'),
  costDefaults: () => call<{ spot: any; futures: any }>('/api/costs/defaults'),
  myFee: (exchange: string, coin: string) =>
    call<{ takerPct: number | null; makerPct: number | null; found: boolean }>(`/api/fees/spot?exchange=${encodeURIComponent(exchange)}&coin=${encodeURIComponent(coin)}`),
  candles: (d: MarketRef) => call<{ candles: Candle[]; intervalSec: number }>('/api/candles', d),
  live: (d: MarketRef) => call<LiveTick>(`/api/live?market=${d.market}&exchange=${encodeURIComponent(d.exchange)}&symbol=${encodeURIComponent(d.symbol)}&intervalMin=${d.intervalMin}`),
  backtest: (d: MarketRef, s: StrategyIn, costs: Costs, startCash: number, refresh: boolean) =>
    call<BacktestResp>('/api/backtest', { data: d, strategy: s, costs, startCash, refresh }),
  scriptTemplates: () => call<ScriptTemplate[]>('/api/script/templates'),
  scriptCheck: (code: string) => call<ScriptCheck>('/api/script/check', { code }),
  scriptSaved: () => call<SavedScript[]>('/api/script/saved'),
  scriptSave: (name: string, code: string) => call<{ saved: string }>('/api/script/save', { name, code }),
  scriptDelete: (name: string) => call<{ deleted: string }>(`/api/script/saved?name=${encodeURIComponent(name)}`, undefined, 'DELETE'),
  traderStart: (body: unknown) => call<TraderStatus>('/api/trader/start', body),
  traderStatus: () => call<TraderStatus>('/api/trader/status'),
  traderStop: () => call<TraderStatus>('/api/trader/stop', {}),
  traderKill: () => call<TraderStatus>('/api/trader/kill', {}),
}
