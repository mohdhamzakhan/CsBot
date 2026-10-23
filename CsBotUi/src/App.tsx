import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api'
import type { BacktestResp, Candle, Costs, LiveTick, Market, MarketRef, RuleSet, StrategyDef } from './api'
import ChartPanel from './ChartPanel'
import Results from './Results'
import RuleBuilder, { defaultRules } from './RuleBuilder'
import TraderPanel from './TraderPanel'
import ScriptEditor from './ScriptEditor'
import TopBar, { TIMEFRAMES } from './TopBar'
import KeysDialog from './KeysDialog'
import LoginScreen from './LoginScreen'

const DAYS_FOR: Record<number, number> = { 15: 60, 30: 90, 60: 120, 240: 365, 360: 730, 720: 730, 1440: 1095 }
const toCosts = (d: any): Costs => ({ fee: d.takerFeePct, gst: d.gstPct, slippage: d.slippagePct, tds: d.tdsPct, funding: d.fundingPer8hPct, leverage: d.maxLeverage })

function Workspace({ onLogout }: { onLogout?: () => void }) {
  const [side, setSide] = useState<'strategy' | 'costs' | 'trade'>('strategy')
  const [health, setHealth] = useState<{ hasKeys: boolean; keyMatchesSecret: boolean; liveEnabled: boolean } | null>(null)
  const [strategies, setStrategies] = useState<StrategyDef[]>([])
  const [defaults, setDefaults] = useState<{ spot: Costs; futures: Costs } | null>(null)
  const [data, setData] = useState<MarketRef>({ market: 'spot', exchange: 'coinswitchx', symbol: 'BTC/INR', intervalMin: 360, days: 730 })
  const [sid, setSid] = useState('script')
  const [code, setCode] = useState('')
  const [params, setParams] = useState<Record<string, number>>({})
  const [rules, setRules] = useState<RuleSet>(defaultRules())
  const [costs, setCosts] = useState<Costs>({ fee: 0.4, gst: 18, slippage: 0.05, tds: 1, funding: 0, leverage: 1 })
  const [startCash, setStartCash] = useState(100000)
  const [bt, setBt] = useState<BacktestResp | null>(null)
  const [chartCandles, setChartCandles] = useState<Candle[]>([])
  const [chartLoading, setChartLoading] = useState(false)
  const [chartErr, setChartErr] = useState('')
  const [live, setLive] = useState<LiveTick | null>(null)
  const [liveErr, setLiveErr] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState('')
  const [info, setInfo] = useState('')
  const reqId = useRef(0)
  const [keysOpen, setKeysOpen] = useState(false)
  const askedKeys = useRef(false)

  useEffect(() => {
    api.health().then(h => { setHealth(h); if (!h.hasKeys && h.canEditKeys !== false && !askedKeys.current) { askedKeys.current = true; setKeysOpen(true) } }).catch(e => setErr(e.message))
    api.strategies().then(setStrategies).catch(e => setErr(e.message))
    api.costDefaults().then(d => { const x = { spot: toCosts(d.spot), futures: toCosts(d.futures) }; setDefaults(x); setCosts(x.spot) }).catch(() => {})
  }, [])

  const def = useMemo(() => strategies.find(s => s.id === sid), [strategies, sid])
  useEffect(() => { if (def) setParams(Object.fromEntries(def.params.map(p => [p.key, p.default]))) }, [def])

  const futures = data.market === 'futures'
  const quote = futures ? 'USDT' : data.symbol.split('/')[1] ?? 'INR'
  const tfLabel = TIMEFRAMES.find(t => t[0] === data.intervalMin)?.[1] ?? `${data.intervalMin}m`
  const strategyIn = useMemo(() => ({ id: sid, params, rules: sid === 'rules' ? rules : undefined, code: sid === 'script' ? code : undefined }), [sid, params, rules, code])

  // ---- candles load automatically whenever market / symbol / timeframe / history changes ----
  const dataKey = `${data.market}|${data.exchange}|${data.symbol}|${data.intervalMin}|${data.days}`
  const loadChart = useCallback(async (silent: boolean) => {
    const id = ++reqId.current
    if (!silent) { setChartLoading(true); setChartErr('') }
    try {
      const r = await api.candles(data)
      if (id !== reqId.current) return
      setChartCandles(r.candles); if (!silent) setBt(null)
    } catch (e: any) { if (!silent && id === reqId.current) setChartErr(e.message) }
    finally { if (!silent && id === reqId.current) setChartLoading(false) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dataKey])
  useEffect(() => { setLive(null); setLiveErr(''); const id = setTimeout(() => loadChart(false), 350); return () => clearTimeout(id) }, [dataKey, loadChart])

  // ---- live price: poll every 3 seconds (paused while the tab is hidden) ----
  useEffect(() => {
    let alive = true, fails = 0
    const tick = async () => {
      if (document.hidden) return
      try { const t = await api.live(data); if (alive) { setLive(t); setLiveErr(''); fails = 0 } }
      catch (e: any) { fails++; if (alive && fails >= 2) { setLiveErr(e.message); setLive(null) } }
    }
    tick(); const id = setInterval(tick, 3000)
    return () => { alive = false; clearInterval(id) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data.market, data.exchange, data.symbol, data.intervalMin])

  const switchMarket = (m: Market) => {
    setData(d => m === 'spot' ? { ...d, market: m, exchange: 'coinswitchx', symbol: 'BTC/INR' } : { ...d, market: m, exchange: 'EXCHANGE_2', symbol: 'BTCUSDT' })
    if (defaults) setCosts(m === 'spot' ? defaults.spot : defaults.futures)
    setBt(null); setChartCandles([])
  }
  const setSymbol = (s: string) => { setData(d => ({ ...d, symbol: s })); setBt(null); setChartCandles([]) }
  const setInterval_ = (m: number) => { setData(d => ({ ...d, intervalMin: m, days: DAYS_FOR[m] ?? d.days })); setBt(null); setChartCandles([]) }

  const run = async (refresh: boolean) => {
    setBusy(true); setErr(''); setInfo(futures ? 'Fetching candles... futures history is rate-limited (30 requests/min), so a long range can take a minute or two.' : 'Running...')
    try { const r = await api.backtest(data, strategyIn, costs, startCash, refresh); setChartCandles(r.candles); setBt(r) }
    catch (e: any) { setErr(e.message) } finally { setBusy(false); setInfo('') }
  }
  const loadMyFee = async () => {
    try { const r = await api.myFee(data.exchange, data.symbol.split('/')[0]); if (r.found && r.takerPct != null) setCosts(c => ({ ...c, fee: r.takerPct! })); else setErr('Could not find your fee for that coin.') }
    catch (e: any) { setErr(e.message) }
  }
  const num = (k: keyof Costs) => (e: React.ChangeEvent<HTMLInputElement>) => setCosts(c => ({ ...c, [k]: +e.target.value }))

  return (
    <>
      <TopBar data={data} onMarket={switchMarket} onSymbol={setSymbol} onInterval={setInterval_} live={live} liveError={liveErr} quote={quote} health={health} onKeys={() => setKeysOpen(true)} onLogout={onLogout} />
      {keysOpen && <KeysDialog onClose={() => setKeysOpen(false)} onSaved={() => { api.health().then(setHealth).catch(() => {}); setChartErr(''); loadChart(false) }} />}
      <div className="workspace">
        <main>
          <ChartPanel candles={chartCandles} overlays={bt?.overlays ?? []} fills={bt?.fills ?? []} intervalSec={data.intervalMin * 60} live={live} liveError={liveErr}
            loading={chartLoading} error={chartErr} title={data.symbol} subtitle={`${tfLabel} · ${futures ? 'USDT perpetual' : 'spot'}`}
            viewKey={dataKey} onNewBar={() => loadChart(true)} onReload={() => loadChart(false)} />
          {bt ? <Results r={bt} def={def} quote={quote} /> : (
            <div className="card empty"><b>Ready.</b><br />Pick a strategy on the right and press <b>Run backtest</b>. Results include fees, GST, slippage and TDS,<br />and are compared with simply buying and holding.</div>)}
        </main>

        <aside className="side">
          <div className="sidetabs">
            <button className={side === 'strategy' ? 'on' : ''} onClick={() => setSide('strategy')}>Strategy</button>
            <button className={side === 'costs' ? 'on' : ''} onClick={() => setSide('costs')}>Costs &amp; data</button>
            <button className={side === 'trade' ? 'on' : ''} onClick={() => setSide('trade')}>Paper / Live</button>
          </div>
          <div className="sidebody">
            {side === 'strategy' && (<>
              <h3>Strategy</h3>
              <select value={sid} onChange={e => setSid(e.target.value)}>{strategies.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
              {def && <div className="note">{def.description}</div>}
              {sid !== 'rules' && sid !== 'script' && def?.params.map(p => (
                <div key={p.key}><label>{p.label}</label>
                  <input type="number" min={p.min} max={p.max} step={p.step} value={params[p.key] ?? p.default} onChange={e => setParams({ ...params, [p.key]: +e.target.value })} /></div>))}
              {sid === 'rules' && <RuleBuilder rules={rules} onChange={setRules} futures={futures} />}
              {sid === 'script' && <ScriptEditor code={code} setCode={setCode} params={params} setParams={setParams} />}
              <div className="note">Lengths are in candles: on {tfLabel} candles, 100 bars is about {Math.round(100 * data.intervalMin / 1440 * 10) / 10} days.</div>
            </>)}

            {side === 'costs' && (<>
              <h3>Data</h3>
              <div className="row">
                <div><label>History for backtests (days)</label><input type="number" value={data.days} onChange={e => setData({ ...data, days: +e.target.value })} /></div>
                <div><label>Exchange</label><input value={data.exchange} disabled={futures} onChange={e => setData({ ...data, exchange: e.target.value })} /></div>
              </div>
              <div className="note">{futures ? 'Futures are USDT-margined perpetuals (symbol like BTCUSDT).' : 'coinswitchx = INR pairs, c2c1 / c2c2 = USDT pairs.'} Changing the timeframe, symbol or history reloads the chart automatically.</div>
              <h3 style={{ marginTop: 16 }}>Costs (percent per side)</h3>
              <div className="row">
                <div><label>Taker fee %</label><input type="number" step="any" value={costs.fee} onChange={num('fee')} /></div>
                <div><label>GST on fee %</label><input type="number" step="any" value={costs.gst} onChange={num('gst')} /></div>
                <div><label>Slippage %</label><input type="number" step="any" value={costs.slippage} onChange={num('slippage')} /></div>
                <div><label>TDS % (on sells)</label><input type="number" step="any" value={costs.tds} onChange={num('tds')} /></div>
                {futures && <div><label>Funding % / 8h</label><input type="number" step="any" value={costs.funding} onChange={num('funding')} /></div>}
                {futures && <div><label>Max leverage</label><input type="number" step="any" value={costs.leverage} onChange={num('leverage')} /></div>}
                <div><label>Start capital ({quote})</label><input type="number" value={startCash} onChange={e => setStartCash(+e.target.value)} /></div>
              </div>
              <div style={{ display: 'flex', gap: 6, marginTop: 10, flexWrap: 'wrap' }}>
                {!futures && <button className="ghost" onClick={loadMyFee}>Load my fee from CoinSwitch</button>}
                {futures && live?.fundingRate != null && <button className="ghost" onClick={() => setCosts(c => ({ ...c, funding: live.fundingRate! * 100 }))}>Use live funding ({(live.fundingRate * 100).toFixed(4)}%)</button>}
              </div>
              <div className="note">{futures ? 'Futures TDS defaults to 0: confirm how CoinSwitch applies it to derivatives, then enter it here.' : 'Default fee is the conservative worst case. Spot TDS is a refundable credit but locks cash on each sell.'}</div>
            </>)}

            {side === 'trade' && <TraderPanel data={data} strategy={strategyIn} costs={costs} liveEnabled={!!health?.liveEnabled} hasKeys={!!health?.hasKeys && !!health?.keyMatchesSecret} quote={quote} />}
          </div>
          {side !== 'trade' && (
            <div className="sidefoot">
              <button className="primary" disabled={busy} onClick={() => run(false)}>{busy ? 'Working...' : 'Run backtest'}</button>
              <button className="ghost" style={{ width: '100%', marginTop: 6 }} disabled={busy} onClick={() => run(true)}>Re-download candles &amp; run</button>
              {info && <div className="note">{info}</div>}
              {err && <div className="err">{err}</div>}
            </div>)}
        </aside>
      </div>
    </>
  )
}

export default function App() {
  const [auth, setAuth] = useState<{ required: boolean; authed: boolean } | null>(null)
  const check = useCallback(() => { api.authStatus().then(setAuth).catch(() => setAuth({ required: false, authed: true })) }, [])
  useEffect(() => {
    check(); window.addEventListener('csbot-login-required', check)
    return () => window.removeEventListener('csbot-login-required', check)
  }, [check])
  if (!auth) return null
  if (auth.required && !auth.authed) return <LoginScreen onDone={check} />
  return <Workspace onLogout={auth.required ? async () => { await api.logout().catch(() => {}); check() } : undefined} />
}
