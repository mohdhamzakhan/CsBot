import { useEffect, useState } from 'react'
import { api, TraderStatus } from './api'
import type { Costs, MarketRef, StrategyIn } from './api'

type Props = { data: MarketRef; strategy: StrategyIn; costs: Costs; liveEnabled: boolean; hasKeys: boolean; quote: string }
const PHRASE = 'I UNDERSTAND THE RISKS'
const countdown = (sec: number) => { if (sec <= 0) return 'any moment'; const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), x = sec % 60; return `${h > 0 ? h + 'h ' : ''}${m}m ${String(x).padStart(2, '0')}s` }

export default function TraderPanel({ data, strategy, costs, liveEnabled, hasKeys, quote }: Props) {
  const [mode, setMode] = useState<'paper' | 'live'>('paper')
  const [capital, setCapital] = useState(data.market === 'spot' ? 10000 : 100)
  const [maxOrder, setMaxOrder] = useState(0)
  const [maxPos, setMaxPos] = useState(0)
  const [maxLev, setMaxLev] = useState(1)
  const [dailyLoss, setDailyLoss] = useState(3)
  const [pricePrec, setPricePrec] = useState(0)
  const [qtyPrec, setQtyPrec] = useState(data.market === 'spot' ? 5 : 3)
  const [confirm, setConfirm] = useState('')
  const [ungated, setUngated] = useState(false)
  const [st, setSt] = useState<TraderStatus | null>(null)
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => { setCapital(data.market === 'spot' ? 10000 : 100); setQtyPrec(data.market === 'spot' ? 5 : 3) }, [data.market])
  const [now, setNow] = useState(Math.floor(Date.now() / 1000))
  useEffect(() => { const id = setInterval(() => setNow(Math.floor(Date.now() / 1000)), 1000); return () => clearInterval(id) }, [])

  useEffect(() => {
    let alive = true
    const tick = () => api.traderStatus().then(s => alive && setSt(s)).catch(() => {})
    tick(); const id = setInterval(tick, 2000)
    return () => { alive = false; clearInterval(id) }
  }, [])

  const start = async () => {
    setErr(''); setBusy(true)
    try {
      const body = {
        mode, data, strategy, costs, capital,
        risk: mode === 'live' ? { maxOrderNotional: maxOrder, maxPositionNotional: maxPos, maxLeverage: maxLev, dailyLossPct: dailyLoss } : undefined,
        pricePrecision: pricePrec, qtyPrecision: qtyPrec, confirm: mode === 'live' ? confirm : undefined, allowUngated: mode === 'live' ? ungated : undefined,
      }
      setSt(await api.traderStart(body))
    } catch (e: any) { setErr(e.message) } finally { setBusy(false) }
  }
  const act = async (f: () => Promise<TraderStatus>) => { setErr(''); try { setSt(await f()) } catch (e: any) { setErr(e.message) } }

  const acct = st?.account as Record<string, any> | undefined
  const running = !!st?.running
  const pnl = acct ? Number(acct.equity) - Number(acct.start) : 0

  return (
    <div>
      <div className="card">
        <h3>Run this strategy on live candles</h3>
        <div className="row">
          <div><label>Mode</label>
            <select value={mode} onChange={e => setMode(e.target.value as 'paper' | 'live')} disabled={running}>
              <option value="paper">Paper (simulated money)</option>
              <option value="live">LIVE (real orders)</option></select></div>
          <div><label>Capital allocated ({quote})</label><input type="number" value={capital} onChange={e => setCapital(+e.target.value)} disabled={running} /></div>
        </div>
        <div className="note">Uses the market, strategy, parameters and costs from the left panel. Paper trading applies the same fees, GST, slippage and TDS model as the backtest and decides on each closed candle. Run it for weeks before considering live.</div>

        {mode === 'live' && (<>
          {!liveEnabled && <div className="warnbox">Live trading is switched off on the server. To enable it, set the environment variable <b>CS_LIVE_ENABLED=1</b> and restart the app.</div>}
          <div className="warnbox">
            <b>Real money.</b> Live mode places real orders on CoinSwitch with your API key. It has not been tested against the live exchange by the author, so start with an amount you can afford to lose
            and watch the first trades in the CoinSwitch app. Spot uses limit orders only; the bot tracks its own position from confirmed fills.
          </div>
          <div className="row">
            <div><label>Max single order ({quote})</label><input type="number" value={maxOrder} onChange={e => setMaxOrder(+e.target.value)} /></div>
            <div><label>Max position ({quote})</label><input type="number" value={maxPos} onChange={e => setMaxPos(+e.target.value)} /></div>
            <div><label>Max leverage (futures)</label><input type="number" value={maxLev} onChange={e => setMaxLev(+e.target.value)} /></div>
            <div><label>Daily loss limit %</label><input type="number" value={dailyLoss} onChange={e => setDailyLoss(+e.target.value)} /></div>
            <div><label>Price decimals</label><input type="number" value={pricePrec} onChange={e => setPricePrec(+e.target.value)} /></div>
            <div><label>Quantity decimals</label><input type="number" value={qtyPrec} onChange={e => setQtyPrec(+e.target.value)} /></div>
          </div>
          <div className="note">Check the allowed price/quantity decimals and minimum order size for your pair in CoinSwitch before going live; wrong precision makes orders fail.</div>
          <label>Type <b>{PHRASE}</b> to enable</label>
          <input value={confirm} onChange={e => setConfirm(e.target.value)} />
          <label><input type="checkbox" checked={ungated} onChange={e => setUngated(e.target.checked)} /> Allow even though this strategy has NOT passed the live gate (not recommended)</label>
        </>)}

        {err && <div className="err">{err}</div>}
        {!hasKeys && <div className="err">API keys are not set on the server.</div>}
        {!running
          ? <button className="primary" disabled={busy || !hasKeys || (mode === 'live' && confirm !== PHRASE)} onClick={start}>{busy ? 'Starting...' : mode === 'live' ? 'START LIVE TRADING' : 'Start paper trading'}</button>
          : <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
              <button className="ghost" onClick={() => act(api.traderStop)}>Stop (keep position)</button>
              <button className="danger" onClick={() => { if (confirm !== '' || st?.mode !== 'live' || window.confirm('Stop trading and close the tracked position now?')) act(api.traderKill) }}>KILL SWITCH: stop + close position</button>
            </div>}
      </div>

      {st && st.state !== 'none' && (<>
        <div className="card">
          <h3>Position &middot; {st.mode} &middot; {st.symbol}</h3>
          {acct && String(acct.side) !== 'flat' ? (
            <div>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap' }}>
                <b className={String(acct.side) === 'long' ? 'pos' : 'neg'} style={{ fontSize: 18, textTransform: 'uppercase' }}>{String(acct.side)}</b>
                <span style={{ fontFamily: 'var(--mono)' }}>{Number(acct.units).toFixed(6)} @ {Number(acct.entryPrice).toLocaleString(undefined, { maximumFractionDigits: 2 })}</span>
              </div>
              <div className="kpis" style={{ margin: '10px 0 0' }}>
                <div className="kpi"><span>Unrealized P&amp;L</span><b className={Number(acct.unrealizedPnl) >= 0 ? 'pos' : 'neg'}>{Number(acct.unrealizedPnl) >= 0 ? '+' : ''}{Number(acct.unrealizedPnl).toFixed(2)}</b><small>{Number(acct.unrealizedPct) >= 0 ? '+' : ''}{Number(acct.unrealizedPct).toFixed(2)}% &middot; {quote}</small></div>
                <div className="kpi"><span>Live price</span><b>{st.lastPrice?.toLocaleString(undefined, { maximumFractionDigits: 2 })}</b><small>{st.priceError ? 'price feed error' : st.priceAgeSec != null ? `updated ${Math.round(st.priceAgeSec)}s ago` : '...'}</small></div>
              </div>
            </div>
          ) : (<div className="note" style={{ margin: 0 }}>Flat: no open position. Live price {st.lastPrice?.toLocaleString(undefined, { maximumFractionDigits: 2 })}.{st.priceError ? ` (price feed error: ${st.priceError})` : ''}</div>)}
          <div className="row" style={{ marginTop: 10 }}>
            <div>State: <b className={st.state === 'running' ? 'pos' : st.state === 'halted' || st.state === 'error' ? 'neg' : ''}>{st.state}</b></div>
            <div>Strategy wants: <b>{st.targetWeight?.toFixed(2)}</b> of capital</div>
            {st.nextDecision != null && st.running && <div style={{ gridColumn: '1 / span 2' }}>Next decision (candle close) in <b style={{ fontFamily: 'var(--mono)' }}>{countdown(st.nextDecision - now)}</b></div>}
          </div>
          <div className="note">The strategy acts only when a candle closes, so the position changes at most once per candle. Price, P&amp;L and equity follow the live market every few seconds.</div>
        </div>
        <div className="card">
          <h3>Account</h3>
          <div className="row">
            {acct && <div>Equity: <b>{Number(acct.equity).toLocaleString(undefined, { maximumFractionDigits: 2 })}</b></div>}
            <div>Total P&amp;L: <b className={pnl >= 0 ? 'pos' : 'neg'}>{pnl >= 0 ? '+' : ''}{pnl.toLocaleString(undefined, { maximumFractionDigits: 2 })} {quote}</b></div>
            {acct && acct.fees !== undefined && <div>Fees paid: <b>{Number(acct.fees).toFixed(2)}</b></div>}
            {acct && acct.tdsWithheld !== undefined && <div>TDS withheld: <b>{Number(acct.tdsWithheld).toFixed(2)}</b></div>}
          </div>
          <label>Fills</label>
          <table><thead><tr><th>Time</th><th>Side</th><th>Price</th><th>Qty</th></tr></thead>
            <tbody>{(st.fills ?? []).slice().reverse().map((f, i) => (
              <tr key={i}><td>{new Date(f.t * 1000).toLocaleString()}</td><td className={f.side === 'buy' ? 'pos' : 'neg'}>{f.side}</td><td>{f.price.toFixed(2)}</td><td>{f.qty.toFixed(6)}</td></tr>))}</tbody></table>
          <label>Log</label>
          <div className="log">{(st.log ?? []).join('\n')}</div>
        </div>
      </>)}
    </div>
  )
}
