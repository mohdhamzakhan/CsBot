import { useEffect, useRef, useState } from 'react'
import type { LiveTick, Market, MarketRef } from './api'

export const TIMEFRAMES: [number, string][] = [[15, '15m'], [30, '30m'], [60, '1h'], [240, '4h'], [360, '6h'], [720, '12h'], [1440, '1D']]
const SPOT = ['BTC/INR', 'ETH/INR', 'SOL/INR', 'XRP/INR', 'DOGE/INR', 'BTC/USDT', 'ETH/USDT']
const FUT = ['BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'XRPUSDT', 'DOGEUSDT']

type Props = {
  data: MarketRef; onMarket: (m: Market) => void; onSymbol: (s: string) => void; onInterval: (m: number) => void
  live: LiveTick | null; liveError: string; quote: string
  health: { hasKeys: boolean; keyMatchesSecret: boolean; liveEnabled: boolean } | null; onKeys: () => void; onLogout?: () => void
}

const compact = (x: number) => (x >= 1e9 ? (x / 1e9).toFixed(2) + 'B' : x >= 1e7 ? (x / 1e7).toFixed(2) + 'Cr' : x >= 1e6 ? (x / 1e6).toFixed(2) + 'M' : x >= 1e3 ? (x / 1e3).toFixed(1) + 'K' : x.toFixed(2))
const pf = (x: number) => x.toLocaleString(undefined, { minimumFractionDigits: x >= 1000 ? 2 : x >= 1 ? 3 : 6, maximumFractionDigits: x >= 1000 ? 2 : x >= 1 ? 3 : 6 })

export default function TopBar({ data, onMarket, onSymbol, onInterval, live, liveError, quote, health, onKeys, onLogout }: Props) {
  const [sym, setSym] = useState(data.symbol)
  const prev = useRef<number | null>(null)
  const [flash, setFlash] = useState<'up' | 'down' | ''>('')
  useEffect(() => setSym(data.symbol), [data.symbol])
  useEffect(() => {
    if (!live) { prev.current = null; return }
    if (prev.current != null && live.price !== prev.current) {
      setFlash(live.price > prev.current ? 'up' : 'down'); const id = setTimeout(() => setFlash(''), 700)
      prev.current = live.price; return () => clearTimeout(id)
    }
    prev.current = live.price
  }, [live])
  const fut = data.market === 'futures'
  const commit = () => { const s = sym.trim().toUpperCase(); if (s && s !== data.symbol) onSymbol(s) }
  const chg = live?.changePct

  return (
    <div className="topbar">
      <div className="brand">
        <div className="logo"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round"><path d="M6 4v16M6 8h0M6 7v6M12 9v10M12 12v5M18 3v14M18 6v7" /></svg></div>
        <div><b>CsBot</b><small>CoinSwitch PRO</small></div>
      </div>
      <div className="seg">
        <button className={!fut ? 'on' : ''} onClick={() => onMarket('spot')}>Spot</button>
        <button className={fut ? 'on' : ''} onClick={() => onMarket('futures')}>Futures</button>
      </div>
      <input className="symbox" list="symbols" value={sym} onChange={e => setSym(e.target.value)} onBlur={commit}
        onKeyDown={e => { if (e.key === 'Enter') { commit(); (e.target as HTMLInputElement).blur() } }} title="Symbol (press Enter)" />
      <datalist id="symbols">{(fut ? FUT : SPOT).map(s => <option key={s} value={s} />)}</datalist>
      <div className="tfs">{TIMEFRAMES.map(([v, l]) => <button key={v} className={data.intervalMin === v ? 'on' : ''} onClick={() => onInterval(v)}>{l}</button>)}</div>

      <div className="sp" />
      <div className="ticker">
        {live ? (<>
          <div className={`price ${flash}`}>{pf(live.price)} <span style={{ fontSize: 12, color: 'var(--muted)', fontWeight: 500 }}>{quote}</span></div>
          {chg != null && <span className={`chg ${chg >= 0 ? 'pos' : 'neg'}`}>{chg >= 0 ? '+' : ''}{chg.toFixed(2)}%</span>}
          {live.high != null && live.low != null && <div className="stat"><span>24h high / low</span><b>{pf(live.high)} / {pf(live.low)}</b></div>}
          {live.volume != null && <div className="stat"><span>24h volume</span><b>{compact(live.volume)}</b></div>}
          {live.bid != null && live.ask != null && <div className="stat"><span>bid / ask</span><b>{pf(live.bid)} / {pf(live.ask)}</b></div>}
          {fut && live.fundingRate != null && <div className="stat"><span>funding / 8h</span><b className={live.fundingRate >= 0 ? 'pos' : 'neg'}>{(live.fundingRate * 100).toFixed(4)}%</b></div>}
        </>) : <div className="stat" title={liveError}><span>live price</span><b style={{ color: 'var(--muted)' }}>{liveError ? 'unavailable' : 'connecting...'}</b></div>}
      </div>
      {health && <>
        <span className={`badge ${health.hasKeys && health.keyMatchesSecret ? 'ok' : 'bad'}`} style={{ cursor: 'pointer' }} title="Click to manage your CoinSwitch API keys" onClick={onKeys}>{!health.hasKeys ? 'Connect API keys' : health.keyMatchesSecret ? 'keys OK' : 'key mismatch'}</span>
        <span className={`badge ${health.liveEnabled ? 'warn' : ''}`}>{health.liveEnabled ? 'LIVE ORDERS ON' : 'orders off'}</span>
      </>}
      {onLogout && <button className="ghost" style={{ padding: '4px 10px' }} onClick={onLogout}>Log out</button>}
    </div>
  )
}
