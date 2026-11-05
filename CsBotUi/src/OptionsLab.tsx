import { useEffect, useMemo, useState } from 'react'
import PayoffChart from './PayoffChart'
import { PRESETS, analyze, greeks, netCost, newLeg, niceStep, pnl, priceLegs } from './options'
import type { Leg, LegKind } from './options'

type Base = 'BTC' | 'ETH' | 'SOL'
const money = (x: number, d = 2) => x.toLocaleString('en-US', { maximumFractionDigits: d, minimumFractionDigits: d })
const num = (v: string) => (v === '' || isNaN(+v) ? 0 : +v)

/** Pricing and payoff workbench for crypto options strategies. It places no orders and reads no exchange prices for options: premiums come from a Black-Scholes model (editable). */
export default function OptionsLab({ baseHint, hv, loadPrice }: { baseHint: Base; hv: number | null; loadPrice: (base: Base) => Promise<number> }) {
  const [base, setBase] = useState<Base>(baseHint)
  const [spot, setSpot] = useState(85000)
  const [ivPct, setIvPct] = useState(60)
  const [days, setDays] = useState(7)
  const [ratePct, setRatePct] = useState(0)
  const [passed, setPassed] = useState(0)
  const [ivShift, setIvShift] = useState(0)
  const [legs, setLegs] = useState<Leg[]>([])
  const [preset, setPreset] = useState('iron_condor')
  const [msg, setMsg] = useState('')
  const [loading, setLoading] = useState(false)

  const T = Math.max(days, 0.01) / 365, sigma = Math.max(ivPct, 0.1) / 100, r = ratePct / 100
  const step = niceStep(spot), atm = Math.round(spot / step) * step

  const applyPreset = (id: string, s = spot) => {
    const st = niceStep(s), a = Math.round(s / st) * st
    setLegs(PRESETS.find(p => p.id === id)!.build(a, st)); setPreset(id)
  }
  const fetchPrice = async (b: Base) => {
    setLoading(true); setMsg('')
    try { const p = await loadPrice(b); setSpot(Math.round(p * 100) / 100); if (legs.length === 0) applyPreset(preset, p) }
    catch (e: any) { setMsg(`Could not read the live ${b} price (${e.message}). Type the price yourself.`); if (legs.length === 0) applyPreset(preset) }
    finally { setLoading(false) }
  }
  useEffect(() => { fetchPrice(base) /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [base])

  const priced = useMemo(() => priceLegs(legs, spot, T, r, sigma), [legs, spot, T, r, sigma])
  const a = useMemo(() => (priced.length ? analyze(priced, spot, T, r, sigma) : null), [priced, spot, T, r, sigma])
  const g = useMemo(() => greeks(priced, spot, T, r, sigma), [priced, spot, T, r, sigma])
  const cost = netCost(priced)

  const view = useMemo(() => {
    const strikes = priced.filter(l => l.kind !== 'stock').map(l => l.strike), span = 3 * sigma * Math.sqrt(T)
    const lo = Math.max(spot * 0.02, Math.min(spot * Math.exp(-span), ...strikes) * 0.92), hi = Math.max(spot * Math.exp(span), ...strikes, spot) * 1.08
    const xs = Array.from({ length: 241 }, (_, i) => lo + (hi - lo) * i / 240)
    const expiry = xs.map(x => pnl(priced, x, 0, 0, 0))
    const shifted = passed > 0 || ivShift !== 0
    const scenario = shifted ? xs.map(x => pnl(priced, x, Math.max(days - passed, 0) / 365, r, Math.max(sigma + ivShift / 100, 0.01))) : null
    return { xs, expiry, scenario }
  }, [priced, spot, sigma, T, passed, ivShift, days, r])

  const upd = (id: number, patch: Partial<Leg>) => setLegs(ls => ls.map(l => (l.id === id ? { ...l, ...patch } : l)))

  return (
    <div>
      <h3>Options lab</h3>
      <div className="note" style={{ marginTop: 0 }}>Design a strategy and see what it earns or loses at expiry and along the way. Prices come from a Black-Scholes model, not from CoinSwitch's order book, and nothing here places an order. Type the real premiums from the CoinSwitch app to compare.</div>

      <div className="card">
        <div className="row">
          <div><label>Underlying</label><select value={base} onChange={e => setBase(e.target.value as Base)}><option>BTC</option><option>ETH</option><option>SOL</option></select></div>
          <div><label>Price (USDT)</label><input type="number" inputMode="decimal" step="any" value={spot} onChange={e => setSpot(num(e.target.value))} /></div>
          <div><label>Implied volatility %</label><input type="number" inputMode="decimal" step="any" value={ivPct} onChange={e => setIvPct(num(e.target.value))} /></div>
          <div><label>Days to expiry</label><input type="number" inputMode="decimal" step="any" value={days} onChange={e => setDays(num(e.target.value))} /></div>
        </div>
        <div style={{ display: 'flex', gap: 6, marginTop: 8, flexWrap: 'wrap' }}>
          <button className="ghost" disabled={loading} onClick={() => fetchPrice(base)}>{loading ? '...' : 'Use live price'}</button>
          {hv != null && <button className="ghost" onClick={() => setIvPct(Math.round(hv))}>Use recent volatility ({hv.toFixed(0)}%)</button>}
        </div>
        {msg && <div className="note">{msg}</div>}
        <label>Interest rate % (leave 0 for crypto)</label>
        <input type="number" inputMode="decimal" step="any" value={ratePct} onChange={e => setRatePct(num(e.target.value))} />
      </div>

      <div className="card">
        <div className="row" style={{ alignItems: 'end' }}>
          <div><label>Strategy</label><select value={preset} onChange={e => applyPreset(e.target.value)}>{PRESETS.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></div>
          <div><button className="ghost" style={{ width: '100%' }} onClick={() => setLegs(ls => ls.map(l => ({ ...l, auto: true })))}>Reset prices to model</button></div>
        </div>
        <div className="note">Strikes are spaced {money(step, 0)} apart around {money(atm, 0)}. Edit any value. Quantities are in {base} units: use your contract size.</div>
        {legs.map(l => (
          <div key={l.id} className="legrow">
            <label className="lg">Side<select value={l.side} onChange={e => upd(l.id, { side: +e.target.value as 1 | -1 })}><option value={1}>Buy</option><option value={-1}>Sell</option></select></label>
            <label className="lg">Type<select value={l.kind} onChange={e => upd(l.id, { kind: e.target.value as LegKind })}><option value="call">Call</option><option value="put">Put</option><option value="stock">{base}</option></select></label>
            <label className="lg">Strike<input type="number" inputMode="decimal" step="any" disabled={l.kind === 'stock'} value={l.kind === 'stock' ? '' : l.strike} onChange={e => upd(l.id, { strike: num(e.target.value) })} /></label>
            <label className="lg">Quantity<input type="number" inputMode="decimal" step="any" value={l.qty} onChange={e => upd(l.id, { qty: num(e.target.value) })} /></label>
            <label className="lg">{l.auto ? 'Premium (model)' : 'Premium (yours)'}<input type="number" inputMode="decimal" step="any" value={priced.find(p => p.id === l.id)?.premium ?? 0} style={l.auto ? undefined : { borderColor: 'var(--amber)' }} onChange={e => upd(l.id, { premium: num(e.target.value), auto: false })} /></label>
            <button className="ghost" style={{ minHeight: 38, padding: 0, alignSelf: 'end' }} onClick={() => setLegs(ls => ls.filter(x => x.id !== l.id))} aria-label="Remove leg">&times;</button>
          </div>))}
        <button className="ghost" style={{ marginTop: 6 }} onClick={() => setLegs(ls => [...ls, newLeg('call', 1, atm)])}>+ Add leg</button>
      </div>

      {a && (<>
        <div className="kpis">
          <div className="kpi"><span>{cost >= 0 ? 'You pay' : 'You receive'}</span><b>{money(Math.abs(cost))}</b><small>USDT to open</small></div>
          <div className="kpi"><span>Max profit</span><b className="pos">{a.maxProfit === Infinity ? 'Unlimited' : money(a.maxProfit)}</b><small>at expiry</small></div>
          <div className="kpi"><span>Max loss</span><b className="neg">{a.maxLoss === -Infinity ? 'Unlimited' : money(a.maxLoss)}</b><small>at expiry</small></div>
          <div className="kpi"><span>Chance of profit</span><b>{a.pop == null ? '-' : (a.pop * 100).toFixed(0) + '%'}</b><small>model estimate</small></div>
        </div>
        <div className="card">
          <h3>Profit / loss by price</h3>
          <PayoffChart spot={spot} xs={view.xs} expiry={view.expiry} scenario={view.scenario} scenarioLabel={`after ${passed} day(s), vol ${ivShift >= 0 ? '+' : ''}${ivShift}`} breakevens={a.breakevens} />
          <div className="note" style={{ marginTop: 4 }}>Break-even price{a.breakevens.length === 1 ? '' : 's'}: <b>{a.breakevens.length ? a.breakevens.map(b => money(b, 0)).join('  \u00B7  ') : 'none'}</b></div>
          <div className="row" style={{ marginTop: 6 }}>
            <div><label>What if: days pass ({passed})</label><input type="range" min={0} max={Math.max(days, 1)} step={0.5} value={passed} onChange={e => setPassed(+e.target.value)} style={{ minHeight: 0, padding: 0 }} /></div>
            <div><label>What if: volatility {ivShift >= 0 ? '+' : ''}{ivShift} pts</label><input type="range" min={-40} max={40} step={1} value={ivShift} onChange={e => setIvShift(+e.target.value)} style={{ minHeight: 0, padding: 0 }} /></div>
          </div>
        </div>
        <div className="card">
          <h3>Greeks (whole position, today)</h3>
          <div className="tablewrap"><table><tbody>
            <tr><td>Delta (profit per +1 USDT move)</td><td>{g.delta.toFixed(3)}</td></tr>
            <tr><td>Gamma (change of delta per +1 USDT)</td><td>{g.gamma.toFixed(5)}</td></tr>
            <tr><td>Theta (profit per day, time decay)</td><td className={g.theta >= 0 ? 'pos' : 'neg'}>{g.theta.toFixed(2)}</td></tr>
            <tr><td>Vega (profit per +1 volatility point)</td><td className={g.vega >= 0 ? 'pos' : 'neg'}>{g.vega.toFixed(2)}</td></tr>
          </tbody></table></div>
          <div className="note">The model assumes constant volatility and a log-normal price. Real crypto moves have fatter tails and the market prices strikes with a volatility skew, so treat the chance of profit as a rough guide, not a promise.</div>
        </div>
      </>)}
      <div className="note">CoinSwitch offers BTC, ETH and SOL options, but its options API is in private beta (email api@coinswitch.co to ask for access). Until it is connected, this lab is for analysis only.</div>
    </div>
  )
}
