import { useEffect, useRef, useState } from 'react'
import type { LiveTick, Market, MarketRef, Wallet } from './api'

export const TIMEFRAMES: [number, string][] = [[1, '1m'], [5, '5m'], [15, '15m'], [30, '30m'], [60, '1h'], [240, '4h'], [360, '6h'], [720, '12h'], [1440, '1D']]
/** History loaded by default for each timeframe (kept modest so the first load is quick). */
export const DAYS_FOR: Record<number, number> = { 1: 3, 5: 10, 15: 60, 30: 90, 60: 120, 240: 365, 360: 730, 720: 730, 1440: 1095 }
const SPOT = ['BTC/INR', 'ETH/INR', 'SOL/INR', 'XRP/INR', 'DOGE/INR', 'BTC/USDT', 'ETH/USDT']
const FUT = ['BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'XRPUSDT', 'DOGEUSDT']

const compact = (x: number) => (x >= 1e9 ? (x / 1e9).toFixed(2) + 'B' : x >= 1e7 ? (x / 1e7).toFixed(2) + 'Cr' : x >= 1e6 ? (x / 1e6).toFixed(2) + 'M' : x >= 1e3 ? (x / 1e3).toFixed(1) + 'K' : x.toFixed(2))
const pf = (x: number) => x.toLocaleString(undefined, { minimumFractionDigits: x >= 1000 ? 2 : x >= 1 ? 3 : 6, maximumFractionDigits: x >= 1000 ? 2 : x >= 1 ? 3 : 6 })

/** Sticky top strip: who you are looking at, the live price, and the menu. Nothing else, so it never needs scrolling. */
export function Header({ data, live, liveError, quote, needsAttention, onMenu }: {
  data: MarketRef; live: LiveTick | null; liveError: string; quote: string; needsAttention: boolean; onMenu: () => void
}) {
  const prev = useRef<number | null>(null)
  const [flash, setFlash] = useState<'up' | 'down' | ''>('')
  useEffect(() => {
    if (!live) { prev.current = null; return }
    if (prev.current != null && live.price !== prev.current) {
      setFlash(live.price > prev.current ? 'up' : 'down'); const id = setTimeout(() => setFlash(''), 700)
      prev.current = live.price; return () => clearTimeout(id)
    }
    prev.current = live.price
  }, [live])
  const tf = TIMEFRAMES.find(t => t[0] === data.intervalMin)?.[1] ?? `${data.intervalMin}m`
  const chg = live?.changePct
  return (
    <div className="hdr">
      <div className="logo"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round"><path d="M6 4v16M6 7v6M12 9v10M12 12v5M18 3v14M18 6v7" /></svg></div>
      <div className="pairname"><b>{data.symbol}</b><small>{data.market === 'futures' ? 'Futures' : 'Spot'} &middot; {tf}</small></div>
      <div className="pricebox" title={liveError}>
        {live
          ? <><div className={`price ${flash}`}>{pf(live.price)}<small>{quote}</small></div>
              {chg != null ? <span className={`chg ${chg >= 0 ? 'pos' : 'neg'}`}>{chg >= 0 ? '+' : ''}{chg.toFixed(2)}%</span> : null}</>
          : <><div className="price" style={{ color: 'var(--muted)', fontSize: 16 }}>{liveError ? 'no live price' : '...'}</div><span className="chg na">{liveError ? 'tap menu' : 'connecting'}</span></>}
      </div>
      <button className="iconbtn" onClick={onMenu} aria-label="Menu">
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M4 7h16M4 12h16M4 17h16" /></svg>
        {needsAttention && <span className="pip" />}
      </button>
    </div>
  )
}

/** Market, symbol and timeframe pickers. */
export function Toolbar({ data, onMarket, onSymbol, onInterval }: {
  data: MarketRef; onMarket: (m: Market) => void; onSymbol: (s: string) => void; onInterval: (m: number) => void
}) {
  const [sym, setSym] = useState(data.symbol)
  useEffect(() => setSym(data.symbol), [data.symbol])
  const fut = data.market === 'futures'
  const commit = () => { const s = sym.trim().toUpperCase(); if (s && s !== data.symbol) onSymbol(s) }
  return (
    <div className="toolbar">
      <div className="seg">
        <button className={!fut ? 'on' : ''} onClick={() => onMarket('spot')}>Spot</button>
        <button className={fut ? 'on' : ''} onClick={() => onMarket('futures')}>Futures</button>
      </div>
      <input className="symbox" list="symbols" value={sym} onChange={e => setSym(e.target.value)} onBlur={commit} autoCapitalize="characters" autoCorrect="off"
        onKeyDown={e => { if (e.key === 'Enter') { commit(); (e.target as HTMLInputElement).blur() } }} aria-label="Symbol" />
      <datalist id="symbols">{(fut ? FUT : SPOT).map(s => <option key={s} value={s} />)}</datalist>
      <div className="tfs">{TIMEFRAMES.map(([v, l]) => <button key={v} className={data.intervalMin === v ? 'on' : ''} onClick={() => onInterval(v)}>{l}</button>)}</div>
    </div>
  )
}

/** 24h statistics. On a phone only the first two are shown until you tap "More". */
export function Stats({ live, market, wallet, onWallet }: { live: LiveTick | null; market: Market; wallet: Wallet | null; onWallet: () => void }) {
  const [open, setOpen] = useState(false)
  const n0 = (x: number) => x.toLocaleString('en-IN', { maximumFractionDigits: 2 })
  const bal = !wallet ? null : wallet.market === 'spot'
    ? `\u20B9${n0(wallet.inr.free)} \u00B7 ${n0(wallet.usdt.free)} USDT` : `${n0(wallet.usdt.available)} USDT`
  if (!live && !bal) return null
  return (
    <div className={`stats ${open ? 'open' : ''}`}>
      {bal && <div className="stat" style={{ cursor: 'pointer' }} onClick={onWallet}><span>available balance</span><b>{bal}</b></div>}
      {live && live.high != null && live.low != null && <div className="stat"><span>24h high / low</span><b>{pf(live.high)} / {pf(live.low)}</b></div>}
      {live && live.volume != null && <div className="stat"><span>24h volume</span><b>{compact(live.volume)}</b></div>}
      {live && (live.bid ?? 0) > 0 && (live.ask ?? 0) > 0 && <div className="stat more"><span>bid / ask</span><b>{pf(live.bid!)} / {pf(live.ask!)}</b></div>}
      {live && market === 'futures' && live.fundingRate != null && <div className="stat more"><span>funding / 8h</span><b className={live.fundingRate >= 0 ? 'pos' : 'neg'}>{(live.fundingRate * 100).toFixed(4)}%</b></div>}
      <button className="morebtn" onClick={() => setOpen(!open)}>{open ? 'Show less' : 'More details'}</button>
    </div>
  )
}
