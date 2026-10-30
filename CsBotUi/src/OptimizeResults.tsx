import { useEffect, useMemo, useState } from 'react'
import type { Curves, OptCombo, OptResult } from './api'
import CurveChart, { CURVE_COLORS } from './CurveChart'

type Rank = 'sharpe' | 'ret' | 'calmar'
const f1 = (x: number) => `${x >= 0 ? '+' : ''}${x.toFixed(1)}%`
const cls = (x: number) => (x >= 0 ? 'pos' : 'neg')
const obj = (c: OptCombo, r: Rank) => (r === 'sharpe' ? c.train.sharpe : r === 'ret' ? c.train.ret : c.train.ret / Math.max(1, c.train.dd))
const label = (p: Record<string, number>) => Object.entries(p).map(([k, v]) => `${k} ${v}`).join(' \u00B7 ')
const date = (t: number) => new Date(t * 1000).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: '2-digit' })
const median = (a: number[]) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.floor(s.length / 2)] : 0 }

export default function OptimizeResults({ res, onShow, loadCurves, onClose }: {
  res: OptResult; onShow: (p: Record<string, number>) => void; loadCurves: (sets: Record<string, number>[]) => Promise<Curves>; onClose: () => void
}) {
  const [rank, setRank] = useState<Rank>('sharpe')
  const [stable, setStable] = useState(false)
  const [view, setView] = useState<'train' | 'test'>('test')
  const [applied, setApplied] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [curves, setCurves] = useState<Curves | null>(null)
  const [curvesBusy, setCurvesBusy] = useState(false)
  const [curvesErr, setCurvesErr] = useState('')

  const sorted = useMemo(() => [...res.combos].sort((a, b) => (stable ? b.robust[rank] - a.robust[rank] : obj(b, rank) - obj(a, rank))), [res, rank, stable])
  const bestRaw = useMemo(() => [...res.combos].sort((a, b) => obj(b, rank) - obj(a, rank))[0], [res, rank])
  const bestStable = useMemo(() => [...res.combos].sort((a, b) => b.robust[rank] - a.robust[rank])[0], [res, rank])
  const byLabel = useMemo(() => new Map(res.combos.map(c => [label(c.params), c])), [res])
  const draw = async (labels: string[]) => {
    const sets = labels.map(l => byLabel.get(l)?.params).filter(Boolean) as Record<string, number>[]
    if (sets.length === 0) { setCurves(null); return }
    setCurvesBusy(true); setCurvesErr('')
    try { setCurves(await loadCurves(sets)) } catch (e: any) { setCurvesErr(e.message) } finally { setCurvesBusy(false) }
  }
  // when a search finishes, start with the three best on the training period already drawn
  useEffect(() => {
    const top = [...res.combos].sort((a, b) => obj(b, 'sharpe') - obj(a, 'sharpe')).slice(0, 3).map(c => label(c.params))
    setPicked(top); setApplied(''); void draw(top)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [res])
  const toggle = (l: string) => setPicked(p => p.includes(l) ? p.filter(x => x !== l) : p.length >= 6 ? p : [...p, l])
  if (!bestRaw) return <div className="card">No valid combinations (every one was skipped).</div>

  const profTest = res.combos.filter(c => c.test.ret > 0).length, medTest = median(res.combos.map(c => c.test.ret))
  const trainLoss = bestRaw.train.ret <= 0
  const heldUp = !trainLoss && bestRaw.test.ret > 0 && bestRaw.test.ret >= bestRaw.train.ret * 0.4
  const verdict = trainLoss
    ? `Even the best setting lost money on the training period (${f1(bestRaw.train.ret)}), so nothing worked here. Any gain on the test period is most likely luck; this strategy probably has no edge on this market and timeframe.`
    : bestRaw.test.ret <= 0
    ? 'The best-looking settings LOST money on the unseen period. That is the classic sign of luck: do not trust them.'
    : heldUp ? 'The best-looking settings also made money on the unseen period. That is encouraging, but still only one test.'
      : 'The best-looking settings made much less on the unseen period than on the training period: a likely sign of overfitting.'

  const d0 = res.dims[0], d1 = res.dims[1]
  const cell = (i: number, j?: number) => res.combos.find(c => c.ix[0] === i && (j === undefined || c.ix[1] === j))
  const shade = (v: number | null) => v == null ? 'transparent' : v >= 0 ? `rgba(38,166,154,${Math.min(0.75, 0.12 + Math.abs(v) / 60)})` : `rgba(239,83,80,${Math.min(0.75, 0.12 + Math.abs(v) / 60)})`
  const val = (c?: OptCombo) => (c ? (view === 'train' ? c.train.ret : c.test.ret) : null)
  const mark = (c?: OptCombo) => (c === bestRaw ? '2px solid #fff' : c === bestStable ? '2px dashed #fff' : '1px solid var(--line)')

  return (
    <div className="card">
      <div style={{ display: 'flex', alignItems: 'center' }}><h3 style={{ margin: 0, flex: 1 }}>Settings search &middot; {res.tested} combinations in {res.seconds}s</h3><button className="ghost" style={{ padding: '2px 10px', minHeight: 30 }} onClick={onClose}>&times;</button></div>
      <div className="note">Ranked on <b>{date(res.start)} &rarr; {date(res.trainEnd)}</b> (training). Then judged on <b>{date(res.trainEnd)} &rarr; {date(res.end)}</b> (unseen test). Buy &amp; hold made {f1(res.baseline.train.ret)} in training and {f1(res.baseline.test.ret)} in the test.</div>

      <div className={`gate ${heldUp ? 'pass' : 'block'}`} style={{ marginTop: 8 }}>
        <div className="ico">{heldUp ? '\u2713' : '!'}</div>
        <div>
          <b>Best on training:</b> {label(bestRaw.params)}<br />
          training <b className={cls(bestRaw.train.ret)}>{f1(bestRaw.train.ret)}</b> &rarr; unseen test <b className={cls(bestRaw.test.ret)}>{f1(bestRaw.test.ret)}</b>
          <div className="note" style={{ margin: '4px 0 0' }}>{verdict}</div>
        </div>
      </div>
      {bestStable !== bestRaw && (
        <div className="note" style={{ marginTop: 0 }}><b>Most stable pick</b> (its neighbours are good too): {label(bestStable.params)} &mdash; training <b className={cls(bestStable.train.ret)}>{f1(bestStable.train.ret)}</b> &rarr; test <b className={cls(bestStable.test.ret)}>{f1(bestStable.test.ret)}</b></div>)}
      <div className="note">{profTest} of {res.combos.length} combinations were profitable on the test period (median {f1(medTest)}). {res.skipped > 0 ? `${res.skipped} invalid combinations were skipped. ` : ''}Testing many combinations makes the top result look better than it really is, so favour settings on a broad good area over a single spike.</div>

      <div className="row" style={{ marginTop: 6 }}>
        <div><label>Rank by (training)</label><select value={rank} onChange={e => setRank(e.target.value as Rank)}><option value="sharpe">Risk-adjusted (Sharpe)</option><option value="ret">Total return</option><option value="calmar">Return vs drawdown</option></select></div>
        <div><label>Order</label><select value={stable ? 's' : 'r'} onChange={e => setStable(e.target.value === 's')}><option value="r">Best single result</option><option value="s">Most stable area</option></select></div>
      </div>

      {res.dims.length <= 2 && (<>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12 }}>
          <h3 style={{ margin: 0, flex: 1 }}>Return map ({view === 'test' ? 'unseen test' : 'training'})</h3>
          <div className="seg"><button className={view === 'train' ? 'on' : ''} style={{ padding: '4px 10px' }} onClick={() => setView('train')}>Training</button><button className={view === 'test' ? 'on' : ''} style={{ padding: '4px 10px' }} onClick={() => setView('test')}>Test</button></div>
        </div>
        <div className="tablewrap"><table style={{ borderCollapse: 'separate', borderSpacing: 3 }}>
          {d1 ? (<>
            <thead><tr><th>{d0.key} \ {d1.key}</th>{d1.values.map(v => <th key={v}>{v}</th>)}</tr></thead>
            <tbody>{d0.values.map((a, i) => (<tr key={a}><td><b>{a}</b></td>{d1.values.map((_, j) => { const c = cell(i, j); const v = val(c); return (
              <td key={j} style={{ background: shade(v), textAlign: 'center', borderRadius: 6, outline: mark(c), outlineOffset: -2, minWidth: 44, cursor: c ? 'pointer' : undefined }} onClick={() => { if (c) { onShow(c.params); setApplied(label(c.params)) } }}>{v == null ? '' : v.toFixed(0)}</td>) })}</tr>))}</tbody>
          </>) : (<>
            <thead><tr>{d0.values.map(v => <th key={v}>{v}</th>)}</tr></thead>
            <tbody><tr>{d0.values.map((_, i) => { const c = cell(i); const v = val(c); return <td key={i} style={{ background: shade(v), textAlign: 'center', borderRadius: 6, outline: mark(c), outlineOffset: -2 }}>{v == null ? '' : v.toFixed(0)}</td> })}</tr></tbody>
          </>)}
        </table></div>
        <div className="note">Numbers are % return after costs. White solid outline = best single result on training; dashed = most stable pick. Green areas that are broad (not a single square) are what you want. Tap a square to draw that setting on the main chart.</div>
      </>)}

      <h3 style={{ marginTop: 12 }}>Compare equity curves</h3>
      {curves ? <>
        <CurveChart curves={curves} splitTime={res.trainEnd} />
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px', margin: '6px 0' }}>
          <span className="note" style={{ margin: 0 }}><span style={{ color: '#8793ab' }}>&#9473;&#9473;</span> buy &amp; hold</span>
          {curves.curves.map((c, k) => (
            <span key={k} className="note" style={{ margin: 0 }}><span style={{ color: CURVE_COLORS[k % CURVE_COLORS.length] }}>&#9473;&#9473;</span> {label(c.params)} <b className={cls(c.postTaxPct ?? 0)}>{f1(c.postTaxPct ?? 0)}</b> after tax</span>))}
        </div>
        <div className="note">Lines show equity after all costs but before tax; the percentages are after the estimated 30% tax (fees are not deductible, so tax can apply even when the line ends slightly down). The shaded part is the unseen test period: if a line climbs only before the shading and goes flat or falls after it, that setting was fitted to the past.</div>
      </> : <div className="note">{curvesBusy ? 'Drawing...' : 'Tick settings in the table below and press Draw.'}</div>}
      {curvesErr && <div className="err">{curvesErr}</div>}
      <button className="ghost" disabled={curvesBusy || picked.length === 0} onClick={() => draw(picked)}>{curvesBusy ? 'Drawing...' : `Draw selected (${picked.length}, max 6)`}</button>

      <h3 style={{ marginTop: 12 }}>Top 10</h3>
      <div className="tablewrap"><table>
        <thead><tr><th></th><th>Settings</th><th>Train</th><th>Test</th><th>Sharpe</th><th>Max DD</th><th>Trades</th><th></th></tr></thead>
        <tbody>{sorted.slice(0, 10).map((c, i) => (
          <tr key={i}>
            <td><input type="checkbox" style={{ minHeight: 0 }} checked={picked.includes(label(c.params))} onChange={() => toggle(label(c.params))} /></td>
            <td style={{ whiteSpace: 'normal' }}>{label(c.params)}</td>
            <td className={cls(c.train.ret)}>{f1(c.train.ret)}</td><td className={cls(c.test.ret)}><b>{f1(c.test.ret)}</b></td>
            <td>{c.full.sharpe.toFixed(2)}</td><td>{c.full.dd.toFixed(0)}%</td><td>{c.full.trades}</td>
            <td><button className="ghost" style={{ minHeight: 30, padding: '2px 10px' }} onClick={() => { onShow(c.params); setApplied(label(c.params)) }}>On chart</button></td>
          </tr>))}</tbody>
      </table></div>
      {applied && <div className="note pos">Drawn on the main chart: {applied} (these settings are now selected in the Strategy tab).</div>}
    </div>
  )
}
