import { useEffect, useState } from 'react'
import type { TriggerResult } from './api'

const fmt = (x: number, quote: string, d?: number) => x.toLocaleString(quote === 'INR' ? 'en-IN' : 'en-US', { maximumFractionDigits: d ?? (Math.abs(x) >= 100 ? 2 : 6) })
export const stanceLabel = (s: string) => (s === 'long' ? 'LONG' : s === 'short' ? 'SHORT' : 'FLAT (in cash)')
/** What happens when the strategy moves from one stance to another. */
export const act = (from: string, to: string) =>
  from === 'flat' && to === 'long' ? 'BUY' : from === 'long' && to === 'flat' ? 'SELL / EXIT' : from === 'flat' && to === 'short' ? 'SHORT' :
  from === 'short' && to === 'flat' ? 'BUY BACK / EXIT' : from === 'long' && to === 'short' ? 'SELL and go SHORT' : from === 'short' && to === 'long' ? 'BUY BACK and go LONG' : 'no change'
export const actColor = (a: string) => (a.startsWith('BUY') && !a.includes('BACK') ? '#26a69a' : a.includes('SHORT') ? '#ef5350' : '#f5b83d')
const isExit = (side: string, a: string) => (side === 'long' ? a.startsWith('SELL') : side === 'short' ? a.startsWith('BUY BACK') : false)
const countdown = (sec: number) => { if (sec <= 0) return 'any moment'; const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), x = sec % 60; return `${h > 0 ? h + 'h ' : ''}${m}m ${String(x).padStart(2, '0')}s` }

export type Holding = { side: string; entry: number; units: number }
type Props = {
  res: TriggerResult | null; error: string; busy: boolean; live: number | null; quote: string; tfLabel: string
  source: 'running' | 'stopped' | 'selected'; name: string; holding: Holding | null; onRefresh: () => void
}

/** The prices at which the strategy will buy, exit or short, in plain words - including what an exit means for the position you hold. */
export default function TriggerCard({ res, error, busy, live, quote, tfLabel, source, name, holding, onRefresh }: Props) {
  const [now, setNow] = useState(Math.floor(Date.now() / 1000))
  useEffect(() => { const id = setInterval(() => setNow(Math.floor(Date.now() / 1000)), 1000); return () => clearInterval(id) }, [])
  const f = (x: number) => fmt(x, quote)
  const ref = live ?? res?.lastClose ?? 0
  const left = res ? res.nextDecisionAt - now : 0
  const rows = (res?.boundaries ?? []).slice().sort((a, b) => a.price - b.price)
  const sign = holding?.side === 'short' ? -1 : 1
  const pnlAt = (p: number) => (holding ? holding.units * (p - holding.entry) * sign : 0)
  const pctAt = (p: number) => (holding && holding.entry ? (p / holding.entry - 1) * 100 * sign : 0)
  const exitRows = holding ? rows.filter(b => { const a = b.below === holding.side ? act(holding.side, b.above) : b.above === holding.side ? act(holding.side, b.below) : ''; return isExit(holding.side, a) }) : []
  const mismatch = holding && res && holding.side !== res.current
  const label = source === 'running' ? 'Running strategy' : source === 'stopped' ? 'Strategy (stopped, position still open)' : 'Selected strategy (not running yet)'
  return (
    <div className="card">
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <h3 style={{ margin: 0, flex: 1 }}>Where the strategy acts</h3>
        <span className={`badge ${source === 'running' ? 'ok' : source === 'stopped' ? 'warn' : ''}`}>{source === 'selected' ? 'preview' : source}</span>
        <button className="ghost" style={{ padding: '2px 10px', minHeight: 30 }} disabled={busy} onClick={onRefresh} aria-label="Recalculate">&#8635;</button>
      </div>
      <div className="note" style={{ marginTop: 4 }}>{label}: <b style={{ color: 'var(--text)' }}>{name}</b> &middot; {tfLabel} candles</div>
      {holding && (
        <div style={{ margin: '4px 0' }}>You hold <b className={holding.side === 'long' ? 'pos' : 'neg'}>{holding.side.toUpperCase()}</b> <span style={{ fontFamily: 'var(--mono)' }}>{holding.units.toFixed(6)} @ {f(holding.entry)}</span>
          {live ? <> &middot; now <b className={pctAt(live) >= 0 ? 'pos' : 'neg'}>{pctAt(live) >= 0 ? '+' : ''}{pctAt(live).toFixed(2)}%</b></> : null}</div>)}
      {error && <div className="err">{error}</div>}
      {!res && !error && <div className="note">{busy ? 'Calculating...' : 'Pick a strategy to see its trigger prices.'}</div>}
      {res && (<>
        {mismatch && <div className="warnbox">The strategy's own signal is <b>{stanceLabel(res.current)}</b> but you hold <b>{holding!.side.toUpperCase()}</b>. At its next decision it will <b>{act(holding!.side, res.current)}</b> to match.</div>}
        <div style={{ margin: '6px 0' }}>Right now the strategy wants to be <b className={res.current === 'long' ? 'pos' : res.current === 'short' ? 'neg' : ''}>{stanceLabel(res.current)}</b>.</div>
        {rows.length === 0 && <div className="note">No candle close within &plusmn;60% of the current price would change that decision at the next candle: this strategy reacts to slow indicators, so it can only change after several candles.</div>}
        {rows.map((b, i) => {
          const above = b.below === res.current ? act(res.current, b.above) : null, below = b.above === res.current ? act(res.current, b.below) : null
          const toLive = ref ? (b.price / ref - 1) * 100 : b.pct
          const exitAct = (above && isExit(holding?.side ?? '', above)) || (below && isExit(holding?.side ?? '', below))
          return (
            <div key={i} style={{ borderTop: '1px solid var(--line)', padding: '8px 0' }}>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
                <b style={{ fontFamily: 'var(--mono)', fontSize: 16 }}>{f(b.price)}</b><span style={{ color: 'var(--muted)', fontSize: 12 }}>{quote} &middot; {toLive >= 0 ? '+' : ''}{toLive.toFixed(2)}% from {live ? 'the live price' : 'the last close'}</span>
              </div>
              {above && <div>If a {tfLabel} candle <b>closes above</b> this: <b style={{ color: actColor(above) }}>{above}</b></div>}
              {below && <div>If a {tfLabel} candle <b>closes below</b> this: <b style={{ color: actColor(below) }}>{below}</b></div>}
              {!above && !below && <div className="note" style={{ margin: 0 }}>Above: {stanceLabel(b.above)} &middot; below: {stanceLabel(b.below)}</div>}
              {exitAct && holding && (
                <div className="note" style={{ margin: '4px 0 0' }}>
                  <b style={{ color: 'var(--text)' }}>This exit is your stop-loss.</b> Selling there would be{' '}
                  <b className={pctAt(b.price) >= 0 ? 'pos' : 'neg'}>{pctAt(b.price) >= 0 ? '+' : ''}{pctAt(b.price).toFixed(2)}%</b> versus your entry
                  (about <b className={pnlAt(b.price) >= 0 ? 'pos' : 'neg'}>{pnlAt(b.price) >= 0 ? '+' : ''}{fmt(pnlAt(b.price), quote, 2)} {quote}</b> on {holding.units.toFixed(6)}).
                  It is checked once per {tfLabel} candle close, so in a sudden crash the real fill can be well below it.
                </div>)}
            </div>)
        })}
        {holding && exitRows.length === 0 && (
          <div className="warnbox"><b>No exit price within &plusmn;60%.</b> At the next candle this strategy would not sell even after a very large move, so it gives you no real stop-loss. Close manually if you need protection, or use a script with <code>stop_loss()</code> / <code>stop_at()</code>.</div>)}
        {res.levels && (
          <div style={{ borderTop: '1px solid var(--line)', padding: '8px 0' }}>
            Orders set by the strategy for the open {res.levels.side}: {res.levels.stop != null && <>stop at <b style={{ fontFamily: 'var(--mono)', color: 'var(--down)' }}>{f(res.levels.stop)}</b> </>}{res.levels.target != null && <>&middot; target at <b style={{ fontFamily: 'var(--mono)', color: 'var(--up)' }}>{f(res.levels.target)}</b></>}
          </div>)}
        <div className="note">The decision is made when the current {tfLabel} candle closes (<b>{left > 0 ? 'in ' + countdown(left) : 'due now'}</b>). The order then goes in at about that price at the next open, so the actual fill will differ a little. Assumes normal trading volume; based on the last {res.barsUsed} candles.</div>
      </>)}
    </div>
  )
}
