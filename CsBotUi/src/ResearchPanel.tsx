import { useEffect, useMemo, useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'
import type { Costs, MarketRef, ParamRange, StrategyIn } from './api'
import type { OptDef } from './OptimizePanel'
import { research, useJob } from './researchApi'
import type { Dist, McResult, SensResult, WfaResult } from './researchApi'
import { LineChart } from './ResearchCharts'

// ---------- shared helpers ----------
const f1 = (x: number | null | undefined, d = 1) => (x == null || !Number.isFinite(x) ? '-' : x.toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d }))
const pc = (x: number | null | undefined) => (x == null || !Number.isFinite(x) ? '-' : `${x >= 0 ? '+' : ''}${f1(x)}%`)
const cls = (x: number | null | undefined) => (x == null ? '' : x >= 0 ? 'pos' : 'neg')
const r4 = (x: number) => Math.round(x * 10000) / 10000
const paramText = (p: Record<string, number>) => Object.entries(p).map(([k, v]) => `${k} ${v}`).join(' \u00B7 ')
const days = (bars: number, intervalMin: number) => Math.round((bars * intervalMin) / 1440)

type SelItem = { on: boolean; from: number; to: number; step: number }
type Sel = Record<string, SelItem>
type Common = {
    data: MarketRef; strategy: StrategyIn; costs: Costs; defs: OptDef[]; current: Record<string, number>
    sel: Sel; setSel: Dispatch<SetStateAction<Sel>>; onApply: (p: Record<string, number>) => void
}

function initial(d: OptDef, current: Record<string, number>): SelItem {
    const c = current[d.key] ?? d.def
    const from = Math.max(d.min, Math.min(c * 0.5, c - d.step)), to = Math.min(d.max, Math.max(c * 1.5, c + d.step))
    const step = Math.max(d.step, Math.round(((to - from) / 6) / d.step) * d.step)
    return { on: true, from: r4(from), to: r4(to), step: r4(step) }
}
const countOf = (r: { from: number; to: number; step: number }) => (r.step > 0 && r.to >= r.from ? Math.floor((r.to - r.from) / r.step + 1e-6) + 1 : 0)
const activeRanges = (defs: OptDef[], sel: Sel): ParamRange[] =>
    defs.filter(d => sel[d.key]?.on).map(d => ({ key: d.key, from: sel[d.key].from, to: sel[d.key].to, step: sel[d.key].step }))
const comboCount = (rs: ParamRange[]) => rs.reduce((a, r) => a * Math.max(1, countOf(r)), 1)

function RangePicker({ defs, current, sel, setSel }: Pick<Common, 'defs' | 'current' | 'sel' | 'setSel'>) {
    if (defs.length === 0) return <div className="note">This strategy has no numeric settings to vary. Pick a built-in strategy, or a script that uses <code>input()</code> settings.</div>
    const toggle = (d: OptDef) => setSel(s => ({ ...s, [d.key]: s[d.key] ? { ...s[d.key], on: !s[d.key].on } : initial(d, current) }))
    const upd = (k: string, f: keyof SelItem, v: number) => setSel(s => ({ ...s, [k]: { ...s[k], [f]: v } }))
    return (<>
        <label>Settings to vary (up to 3)</label>
        {defs.map(d => {
            const s = sel[d.key]
            return (
                <div key={d.key} style={{ borderTop: '1px solid var(--line)', paddingTop: 8, marginTop: 8 }}>
                    <label style={{ display: 'flex', gap: 8, alignItems: 'center', margin: 0, color: 'var(--text)' }}>
                        <input type="checkbox" checked={!!s?.on} onChange={() => toggle(d)} /> {d.label} <span style={{ color: 'var(--muted)' }}>(now {current[d.key] ?? d.def})</span>
                    </label>
                    {s?.on && (
                        <div className="row" style={{ gridTemplateColumns: '1fr 1fr 1fr', marginTop: 6 }}>
                            <div><label>From</label><input type="number" inputMode="decimal" step="any" value={s.from} onChange={e => upd(d.key, 'from', +e.target.value)} /></div>
                            <div><label>To</label><input type="number" inputMode="decimal" step="any" value={s.to} onChange={e => upd(d.key, 'to', +e.target.value)} /></div>
                            <div><label>Step</label><input type="number" inputMode="decimal" step="any" value={s.step} onChange={e => upd(d.key, 'step', +e.target.value)} /></div>
                        </div>)}
                </div>)
        })}
    </>)
}

function JobBar({ busy, progress, onCancel, text }: { busy: boolean; progress: number; onCancel: () => void; text: string }) {
    if (!busy) return null
    return (
        <div style={{ margin: '10px 0' }}>
            <div style={{ height: 8, borderRadius: 6, background: '#0a0e16', border: '1px solid var(--line)', overflow: 'hidden' }}>
                <div style={{ height: '100%', width: `${Math.max(4, progress * 100)}%`, background: 'linear-gradient(90deg,#3b82f6,#22d3ee)', transition: 'width .4s' }} />
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 6 }}>
                <span className="note" style={{ margin: 0, flex: 1 }}>{text} {progress > 0 ? `${Math.round(progress * 100)}%` : ''}</span>
                <button className="ghost" style={{ minHeight: 30, padding: '2px 12px' }} onClick={onCancel}>Cancel</button>
            </div>
        </div>)
}

const Verdict = ({ tone, title, children }: { tone: 'pass' | 'block'; title: string; children?: React.ReactNode }) => (
    <div className={`gate ${tone}`}><div className="ico">{tone === 'pass' ? '\u2713' : '!'}</div><div><b>{title}</b>{children}</div></div>)

const q = (a: number[], p: number) => { const s = [...a].sort((x, y) => x - y), pos = p * (s.length - 1), lo = Math.floor(pos), hi = Math.ceil(pos); return s[lo] + (s[hi] - s[lo]) * (pos - lo) }

// =====================================================================================
// 1. Monte Carlo
// =====================================================================================
function MonteCarloView({ data, strategy, costs }: Common) {
    const [runs, setRuns] = useState(5000)
    const [mode, setMode] = useState<'BlockBootstrap' | 'Shuffle'>('BlockBootstrap')
    const [block, setBlock] = useState(20)
    const [ruin, setRuin] = useState(50)
    const j = useJob<McResult>()
    const run = () => j.start(() => research.montecarlo(data, strategy, costs, { runs, mode, blockSize: block, ruinDrawdownPct: ruin, samplePaths: 40 }))
    const res = j.result

    const fan = useMemo(() => {
        if (!res) return null
        const P = res.samplePaths.filter(p => p && p.length > 1)
        if (P.length < 5) return null
        const n = Math.min(...P.map(p => p.length)), lo: number[] = [], hi: number[] = [], med: number[] = []
        for (let i = 0; i < n; i++) { const col = P.map(p => p[i]); lo.push(q(col, 0.05)); hi.push(q(col, 0.95)); med.push(q(col, 0.5)) }
        return { P: P.map(p => p.slice(0, n)), lo, hi, med, n }
    }, [res])

    const tone = res ? (res.probRuinPct < 5 ? 'pass' : 'block') : 'pass'
    const rows: [string, Dist, boolean][] = res ? [['Final return', res.finalReturnPct, true], ['Worst drop from a peak', res.maxDrawdownPct, false], ['Yearly growth (CAGR)', res.cagrPct, true]] : []
    return (
        <div>
            <div className="note" style={{ marginTop: 0 }}>Replays your strategy's candle-by-candle results in thousands of reshuffled orders to show how much of the outcome was luck of the sequence. It answers: <b>how bad could the ride have been, and how often would it have blown up?</b> It uses the settings and costs from the other tabs.</div>
            <div className="row">
                <div><label>Simulations</label><select value={runs} onChange={e => setRuns(+e.target.value)}><option value={1000}>1,000 (quick)</option><option value={5000}>5,000</option><option value={10000}>10,000</option></select></div>
                <div><label>Method</label><select value={mode} onChange={e => setMode(e.target.value as 'BlockBootstrap' | 'Shuffle')}><option value="BlockBootstrap">Block resampling</option><option value="Shuffle">Shuffle only</option></select></div>
                <div><label>Block length (candles)</label><input type="number" inputMode="numeric" min={1} value={block} disabled={mode === 'Shuffle'} onChange={e => setBlock(+e.target.value)} /></div>
                <div><label>Count as ruin at a drop of (%)</label><input type="number" inputMode="decimal" min={1} max={100} value={ruin} onChange={e => setRuin(+e.target.value)} /></div>
            </div>
            <button className="primary" style={{ marginTop: 12 }} disabled={j.busy} onClick={run}>{j.busy ? 'Simulating...' : 'Run Monte Carlo'}</button>
            <JobBar busy={j.busy} progress={j.progress} onCancel={j.cancel} text="Running the backtest and simulations..." />
            {j.error && <div className="err">{j.error}</div>}
            {j.cancelled && <div className="note">Cancelled.</div>}

            {res && (<>
                <Verdict tone={tone} title={`Chance of a ${ruin}% crash: ${f1(res.probRuinPct)}%`}>
                    <div className="note" style={{ margin: '4px 0 0' }}>
                        In {f1(res.probRuinPct)}% of {res.runs.toLocaleString()} simulated futures the account fell {ruin}% or more from a peak, and {f1(res.probLossPct)}% finished below the starting capital.
                        Under 5% crash chance is shown green; higher is shown red.
                    </div>
                </Verdict>
                {res.warnings.map((w, i) => <div key={i} className="warnbox">{w}</div>)}

                <div className="kpis">
                    <div className="kpi"><span>Typical return</span><b className={cls(res.finalReturnPct.p50)}>{pc(res.finalReturnPct.p50)}</b><small>median of all runs</small></div>
                    <div className="kpi"><span>Bad case (5%)</span><b className={cls(res.finalReturnPct.p5)}>{pc(res.finalReturnPct.p5)}</b><small>1 run in 20 is worse</small></div>
                    <div className="kpi"><span>Typical worst drop</span><b className="neg">{f1(res.maxDrawdownPct.p50)}%</b><small>median max drawdown</small></div>
                    <div className="kpi"><span>Bad drawdown (95%)</span><b className="neg">{f1(res.maxDrawdownPct.p95)}%</b><small>1 run in 20 is deeper</small></div>
                </div>

                {fan && (
                    <div className="card">
                        <h3>Possible futures (starting at 100)</h3>
                        <LineChart height={280} baseline={100}
                            band={{ lo: fan.lo, hi: fan.hi, color: 'rgba(59,130,246,0.16)' }}
                            series={[...fan.P.map(p => ({ values: p, color: '#8793ab', width: 1, opacity: 0.25 })), { values: fan.med, color: '#3b82f6', width: 2.4 }]}
                            xFormat={i => `${Math.round((i / Math.max(1, fan.n - 1)) * res.horizon * data.intervalMin / 1440)}d`} />
                        <div className="note">Grey lines are individual simulated paths. The blue line is the median and the shaded band covers the middle 90% of paths. The dashed line is your starting capital.</div>
                    </div>)}

                <div className="card">
                    <h3>Spread of outcomes</h3>
                    <div className="tablewrap"><table>
                        <thead><tr><th></th><th>5%</th><th>25%</th><th>Median</th><th>75%</th><th>95%</th><th>Average</th></tr></thead>
                        <tbody>{rows.map(([name, d, signed]) => (
                            <tr key={name}><td>{name}</td>{[d.p5, d.p25, d.p50, d.p75, d.p95, d.mean].map((v, i) => <td key={i} className={signed ? cls(v) : undefined}>{signed ? pc(v) : `${f1(v)}%`}</td>)}</tr>))}</tbody>
                    </table></div>
                    <div className="note">"5%" means only 1 simulation in 20 did worse than this. Your actual backtest is just one draw from this spread. If it sits at the very top, it was likely a lucky sequence.</div>
                    <div className="note">Built from {res.observations.toLocaleString()} candle returns after all fees, GST, slippage, TDS and funding. The bot trades portfolio weights rather than discrete trades, so candles (not trades) are what gets resampled. Block resampling (the better choice) keeps winning and losing streaks together; "shuffle only" always ends at the same final return.</div>
                </div>
            </>)}
        </div>)
}

// =====================================================================================
// 2. Walk-forward
// =====================================================================================
function WalkForwardView({ data, strategy, costs, defs, current, sel, setSel }: Common) {
    const oosD0 = Math.max(1, Math.round(data.days / 24))
    const [isDays, setIsDays] = useState(oosD0 * 6)
    const [oosDays, setOosDays] = useState(oosD0)
    const [stepDays, setStepDays] = useState(oosD0)
    const [anchored, setAnchored] = useState(false)
    const [objective, setObjective] = useState('return')
    const [minTrades, setMinTrades] = useState(5)
    const [radius, setRadius] = useState(1)
    useEffect(() => { const o = Math.max(1, Math.round(data.days / 24)); setIsDays(o * 6); setOosDays(o); setStepDays(o) }, [data.days])   // sensible windows for the history length
    const j = useJob<WfaResult>()
    const [used, setUsed] = useState<{ oosBars: number } | null>(null)

    const barsOf = (d: number) => Math.max(1, Math.round((d * 1440) / data.intervalMin))
    const isBars = barsOf(isDays), oosBars = barsOf(oosDays), stepBars = barsOf(stepDays)
    const ranges = activeRanges(defs, sel), combos = comboCount(ranges)
    const totalBars = barsOf(data.days)
    const windows = totalBars < isBars + oosBars ? 0 : Math.floor((totalBars - isBars - oosBars) / stepBars) + 1
    const runs = windows * (combos + 1)
    const problem = [isDays, oosDays, stepDays].some(x => !Number.isFinite(x) || x < 1) ? 'Enter whole numbers of days (1 or more).'
        : defs.length === 0 ? 'This strategy has no numeric settings to optimise.'
            : ranges.length === 0 ? 'Tick at least one setting to vary.' : ranges.length > 3 ? 'Vary at most 3 settings.'
                : combos > 2000 ? `${combos} combinations per window is too many (limit 2000). Use bigger steps.`
                    : isBars < 50 || oosBars < 10 ? 'Windows are too short for this timeframe.'
                        : stepBars < oosBars ? '"Move forward by" must be at least as long as "Then test on", otherwise the unseen periods overlap.'
                            : windows < 2 ? 'The history is too short for at least 2 windows. Shorten the windows or raise "History" in Costs & data.'
                                : runs > 30000 ? `About ${runs.toLocaleString()} backtests: too many (limit 30,000). Use bigger steps or longer out-of-sample windows.` : ''
    const run = () => {
        setUsed({ oosBars })
        return j.start(() => research.walkforward(data, strategy, costs, ranges, { inSampleBars: isBars, outSampleBars: oosBars, stepBars, anchored, warmup: 200, minTrades, smoothRadius: radius, objective, maxCombos: 2000 }))
    }
    const res = j.result

    return (
        <div>
            <div className="note" style={{ marginTop: 0 }}>The honest test for optimised settings. In each window it picks the best settings on the <b>past</b> (in-sample), then trades the <b>next unseen stretch</b> (out-of-sample) with them. The unseen stretches are stitched together. If the result survives, the optimisation found something real; if not, it was fitting noise.</div>
            <RangePicker defs={defs} current={current} sel={sel} setSel={setSel} />
            <div className="row">
                <div><label>Learn on (days)</label><input type="number" inputMode="numeric" min={1} value={isDays} onChange={e => setIsDays(+e.target.value)} /></div>
                <div><label>Then test on (days)</label><input type="number" inputMode="numeric" min={1} value={oosDays} onChange={e => setOosDays(+e.target.value)} /></div>
                <div><label>Move forward by (days)</label><input type="number" inputMode="numeric" min={1} value={stepDays} onChange={e => setStepDays(+e.target.value)} /></div>
                <div><label>Choose settings by</label><select value={objective} onChange={e => setObjective(e.target.value)}><option value="return">Total return</option><option value="sharpe">Sharpe ratio</option><option value="return_dd">Return vs drawdown</option></select></div>
                <div><label>Min trades to qualify</label><input type="number" inputMode="numeric" min={0} value={minTrades} onChange={e => setMinTrades(+e.target.value)} /></div>
                <div><label>Prefer stable areas</label><select value={radius} onChange={e => setRadius(+e.target.value)}><option value={0}>No (raw best)</option><option value={1}>Yes (neighbours, recommended)</option><option value={2}>Strongly</option></select></div>
            </div>
            <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}><input type="checkbox" checked={anchored} onChange={e => setAnchored(e.target.checked)} /> Anchored (learning period always starts at the beginning and grows)</label>
            <div className="note" style={{ color: problem ? 'var(--down)' : undefined }}>
                {problem || `${windows} windows (${days(isBars, data.intervalMin)} days learn, ${days(oosBars, data.intervalMin)} days test) \u00B7 about ${runs.toLocaleString()} backtests.`}
            </div>
            <button className="primary" disabled={j.busy || !!problem} onClick={run}>{j.busy ? 'Working...' : 'Run walk-forward'}</button>
            <JobBar busy={j.busy} progress={j.progress} onCancel={j.cancel} text="Optimising each window..." />
            {j.error && <div className="err">{j.error}</div>}
            {j.cancelled && <div className="note">Cancelled.</div>}

            {res && (<>
                <div style={{ marginTop: 12 }}>
                    <Verdict tone={res.passed ? 'pass' : 'block'} title={res.passed ? 'Held up on unseen data' : 'Warning signs found'}>
                        {res.flags.length === 0
                            ? <div className="note" style={{ margin: '4px 0 0' }}>The stitched unseen results are positive, most windows were profitable and the chosen settings were reasonably consistent. Still one test on one history: not a guarantee.</div>
                            : <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>{res.flags.map((f, i) => <li key={i} className="note" style={{ margin: '2px 0' }}>{f}</li>)}</ul>}
                    </Verdict>
                </div>
                <div className="kpis">
                    <div className="kpi"><span>Unseen return (stitched)</span><b className={cls(res.combinedOosReturnPct)}>{pc(res.combinedOosReturnPct)}</b><small>the number to trust</small></div>
                    <div className="kpi"><span>Efficiency</span><b className={res.walkForwardEfficiency != null && res.walkForwardEfficiency >= 0.5 ? 'pos' : 'neg'}>{res.walkForwardEfficiency == null ? '-' : `${Math.round(res.walkForwardEfficiency * 100)}%`}</b><small>unseen vs learned (50%+ is good)</small></div>
                    <div className="kpi"><span>Profitable windows</span><b>{Math.round(res.profitableWindowsPct)}%</b><small>of {res.windows.length}</small></div>
                    <div className="kpi"><span>Worst drop (unseen)</span><b className="neg">{f1(res.combinedOosMaxDdPct)}%</b><small>Sharpe {f1(res.combinedOosSharpe, 2)}</small></div>
                </div>

                <div className="card">
                    <h3>Equity on unseen data only (starting at 100)</h3>
                    <LineChart height={240} baseline={100} series={[{ values: res.combinedOosEquity, color: '#3b82f6', width: 2 }]}
                        vlines={used ? Array.from({ length: Math.max(0, res.windows.length - 1) }, (_, k) => (k + 1) * used.oosBars).filter(i => i < res.combinedOosEquity.length) : []}
                        xFormat={i => `${Math.round((i * data.intervalMin) / 1440)}d`} />
                    <div className="note">Dotted verticals mark where the settings were re-chosen. This curve contains only trades made after each optimisation, so it carries no look-ahead.</div>
                </div>

                <div className="card">
                    <h3>Window by window</h3>
                    <div className="tablewrap"><table>
                        <thead><tr><th>#</th><th>Chosen settings</th><th>Learned</th><th>Unseen</th><th>Sharpe</th><th>Trades</th></tr></thead>
                        <tbody>{res.windows.map(w => (
                            <tr key={w.index}>
                                <td>{w.index + 1}</td>
                                <td style={{ whiteSpace: 'normal' }}>{Object.keys(w.params).length ? paramText(w.params) : <span style={{ color: 'var(--amber)' }}>none qualified (stayed flat)</span>}{w.bestWasFragile ? <span title="The raw best was a lone spike" style={{ color: 'var(--amber)' }}> &#9888;</span> : null}</td>
                                <td className={cls(w.isReturnPct)}>{pc(w.isReturnPct)}</td><td className={cls(w.oosReturnPct)}><b>{pc(w.oosReturnPct)}</b></td>
                                <td>{f1(w.oosSharpe, 2)}</td><td>{w.oosTrades}</td>
                            </tr>))}</tbody>
                    </table></div>
                    <div className="note">"Learned" is what the settings earned on the period they were chosen from (always flattering); "Unseen" is what they earned straight afterwards. A big gap between the two columns is overfitting. &#9888; = the single best setting was an isolated spike, so a steadier neighbour was preferred.</div>
                </div>

                <div className="card">
                    <h3>Do the settings stay put?</h3>
                    {Object.entries(res.paramInstability).map(([k, v]) => (
                        <div key={k} style={{ margin: '6px 0' }}>
                            <div style={{ display: 'flex', fontSize: 12.5 }}><span style={{ flex: 1 }}>{k}</span><b className={v > 0.25 ? 'neg' : 'pos'}>{v > 0.25 ? 'jumps around' : 'stable'} ({f1(v, 2)})</b></div>
                            <div style={{ height: 6, background: '#0a0e16', borderRadius: 4, overflow: 'hidden', border: '1px solid var(--line)' }}><div style={{ height: '100%', width: `${Math.min(100, v * 200)}%`, background: v > 0.25 ? 'var(--down)' : 'var(--up)' }} /></div>
                        </div>))}
                    <div className="note">How much each chosen setting wandered between windows, as a share of its search range. Above 0.25 means the "best" value keeps changing, which usually means there is no real optimum to find.</div>
                </div>
                <div className="note">Return figures here are before the estimated 30% tax; costs (fees, GST, slippage, TDS, funding) are included.</div>
            </>)}
        </div>)
}

// =====================================================================================
// 3. Parameter sensitivity
// =====================================================================================
function SensitivityView({ data, strategy, costs, defs, current, sel, setSel, onApply }: Common) {
    const [objective, setObjective] = useState('return')
    const [minTrades, setMinTrades] = useState(5)
    const [radius, setRadius] = useState(1)
    const j = useJob<SensResult>()
    const ranges = activeRanges(defs, sel), combos = comboCount(ranges)
    const problem = defs.length === 0 ? 'This strategy has no numeric settings to vary.' : ranges.length === 0 ? 'Tick at least one setting to vary.'
        : ranges.length > 3 ? 'Vary at most 3 settings.' : combos > 1500 ? `${combos} combinations is too many (limit 1500). Use bigger steps or narrower ranges.` : ''
    const run = () => j.start(() => research.sensitivity(data, strategy, costs, ranges, objective, minTrades, radius))
    const res = j.result

    const view = useMemo(() => {
        if (!res) return null
        const d = res.dims, stride = d.map((_, k) => d.slice(k + 1).reduce((a, b) => a * b, 1))
        const idxOf = (k: number, v: number) => res.axisValues[k].findIndex(x => Math.abs(x - v) < 1e-9)
        const at = (p: Record<string, number>) => res.axisNames.map((n, k) => idxOf(k, p[n]))
        return { stride, bestIx: at(res.bestParams), stableIx: at(res.stableParams) }
    }, [res])

    const heat = () => {
        if (!res || !view) return null
        const d = res.dims
        if (d.length === 1) {
            return (<>
                <LineChart height={200} baseline={0} series={[{ values: res.scores, color: '#3b82f6', width: 2 }]}
                    xFormat={i => String(res.axisValues[0][Math.min(i, d[0] - 1)] ?? '')} />
                <div className="note">Score for each value of <b>{res.axisNames[0]}</b>. A wide flat hill is good; a single sharp peak is a warning.</div>
            </>)
        }
        const rest = view.stableIx.slice(2)
        const cell = (i: number, jx: number) => {
            const ix = [i, jx, ...rest]; return ix.reduce((a, v, k) => a + v * view.stride[k], 0)
        }
        let maxAbs = 1e-9
        for (let i = 0; i < d[0]; i++) for (let jx = 0; jx < d[1]; jx++) { const c = cell(i, jx); if (res.valid[c]) maxAbs = Math.max(maxAbs, Math.abs(res.scores[c])) }
        const shade = (v: number) => (v >= 0 ? `rgba(38,166,154,${Math.min(0.8, 0.12 + (v / maxAbs) * 0.65)})` : `rgba(239,83,80,${Math.min(0.8, 0.12 + (-v / maxAbs) * 0.65)})`)
        const sameSlice = (ix: number[]) => rest.every((v, k) => ix[k + 2] === v)
        return (<>
            <div className="tablewrap"><table style={{ borderCollapse: 'separate', borderSpacing: 3 }}>
                <thead><tr><th>{res.axisNames[0]} \ {res.axisNames[1]}</th>{res.axisValues[1].map(v => <th key={v}>{v}</th>)}</tr></thead>
                <tbody>{res.axisValues[0].map((a, i) => (
                    <tr key={a}><td><b>{a}</b></td>{res.axisValues[1].map((_, jx) => {
                        const c = cell(i, jx), ok = res.valid[c], isBest = sameSlice(view.bestIx) && view.bestIx[0] === i && view.bestIx[1] === jx, isStable = view.stableIx[0] === i && view.stableIx[1] === jx
                        return (
                            <td key={jx} style={{ background: ok ? shade(res.scores[c]) : 'transparent', textAlign: 'center', borderRadius: 6, minWidth: 44, outline: isBest ? '2px solid #fff' : isStable ? '2px dashed #fff' : '1px solid var(--line)', outlineOffset: -2, color: ok ? undefined : 'var(--dim)' }}>
                                {ok ? f1(res.scores[c], 0) : '\u2013'}
                            </td>)
                    })}</tr>))}</tbody>
            </table></div>
            <div className="note">Numbers are the chosen score for each pair of settings{res.dims.length > 2 ? `, with the third setting held at its stable value` : ''}. Solid outline = single best; dashed = most stable pick; &ndash; = invalid combination. You want a broad green area, not one bright square.</div>
        </>)
    }

    return (
        <div>
            <div className="note" style={{ marginTop: 0 }}>Tests many neighbouring settings to see whether your result sits on a <b>broad, stable plateau</b> (trustworthy) or a <b>lone spike</b> (likely luck). It uses the whole history, so this checks fragility, not out-of-sample skill: pair it with walk-forward.</div>
            <RangePicker defs={defs} current={current} sel={sel} setSel={setSel} />
            <div className="row">
                <div><label>Score by</label><select value={objective} onChange={e => setObjective(e.target.value)}><option value="return">Total return</option><option value="sharpe">Sharpe ratio</option><option value="return_dd">Return vs drawdown</option></select></div>
                <div><label>Min trades to qualify</label><input type="number" inputMode="numeric" min={0} value={minTrades} onChange={e => setMinTrades(+e.target.value)} /></div>
                <div><label>Neighbourhood size</label><select value={radius} onChange={e => setRadius(+e.target.value)}><option value={1}>1 step each way</option><option value={2}>2 steps each way</option><option value={3}>3 steps each way</option></select></div>
            </div>
            <div className="note" style={{ color: problem ? 'var(--down)' : undefined }}>{problem || `${combos} combinations will be tested.`}</div>
            <button className="primary" disabled={j.busy || !!problem} onClick={run}>{j.busy ? 'Testing...' : 'Run sensitivity analysis'}</button>
            <JobBar busy={j.busy} progress={j.progress} onCancel={j.cancel} text="Testing combinations..." />
            {j.error && <div className="err">{j.error}</div>}
            {j.cancelled && <div className="note">Cancelled.</div>}

            {res && view && (<>
                <div style={{ marginTop: 12 }}>
                    <Verdict tone={res.bestIsFragile ? 'block' : 'pass'} title={res.bestIsFragile ? 'The best setting is a lone spike' : 'The best setting sits on solid ground'}>
                        <div className="note" style={{ margin: '4px 0 0' }}>
                            {res.bestIsFragile
                                ? `It scores far above its neighbours (${f1(res.spikeRatio, 1)} standard deviations higher), so a small change would likely erase the edge. Prefer the stable pick below.`
                                : `Its neighbours score about as well (spike ${f1(res.spikeRatio, 1)}), so the result does not hinge on one exact value.`}
                            {' '}About {Math.round(res.plateauShare * 100)}% of valid settings score close to the stable pick{res.plateauShare < 0.15 ? ': a narrow hill, so treat it carefully' : ''}.
                        </div>
                    </Verdict>
                </div>
                <div className="card">
                    <h3>Picks</h3>
                    <div className="tablewrap"><table>
                        <thead><tr><th></th><th>Settings</th><th>Score</th><th></th></tr></thead>
                        <tbody>
                            <tr><td>Best single</td><td style={{ whiteSpace: 'normal' }}>{paramText(res.bestParams)}</td><td>{f1(res.bestScore)}</td><td><button className="ghost" style={{ minHeight: 30, padding: '2px 10px' }} onClick={() => onApply(res.bestParams)}>Use</button></td></tr>
                            <tr><td>Most stable</td><td style={{ whiteSpace: 'normal' }}>{paramText(res.stableParams)}</td><td>{f1(res.stableRawScore)}</td><td><button className="ghost" style={{ minHeight: 30, padding: '2px 10px' }} onClick={() => onApply(res.stableParams)}>Use</button></td></tr>
                        </tbody>
                    </table></div>
                    <div className="note">"Use" copies those values into the Strategy tab so you can back-test them. Score is on the full history and before tax; "most stable" shows its own score (its neighbourhood average is {f1(res.stableScore)}).</div>
                </div>
                <div className="card"><h3>Map of results</h3>{heat()}</div>
            </>)}
        </div>)
}

// =====================================================================================
// the tab
// =====================================================================================
type Props = {
    data: MarketRef; strategy: StrategyIn; costs: Costs; defs: OptDef[]; current: Record<string, number>
    strategyName: string; tfLabel: string; onApply: (p: Record<string, number>) => void
}

export default function ResearchPanel({ data, strategy, costs, defs, current, strategyName, tfLabel, onApply }: Props) {
    const [tab, setTab] = useState<'mc' | 'wfa' | 'sens'>('mc')
    const [sel, setSel] = useState<Sel>({})
    const common: Common = { data, strategy, costs, defs, current, sel, setSel, onApply }
    return (
        <div>
            <h3>Research</h3>
            <div className="note" style={{ marginTop: 0 }}>Testing <b style={{ color: 'var(--text)' }}>{strategyName}</b> on <b style={{ color: 'var(--text)' }}>{data.symbol}</b> &middot; {tfLabel} candles &middot; {data.days} days of history, with the costs from the Costs tab. Change any of those in the other tabs first.</div>
            <div className="seg" style={{ width: '100%', margin: '8px 0 12px' }}>
                <button style={{ flex: 1 }} className={tab === 'mc' ? 'on' : ''} onClick={() => setTab('mc')}>Monte Carlo</button>
                <button style={{ flex: 1 }} className={tab === 'wfa' ? 'on' : ''} onClick={() => setTab('wfa')}>Walk-forward</button>
                <button style={{ flex: 1 }} className={tab === 'sens' ? 'on' : ''} onClick={() => setTab('sens')}>Sensitivity</button>
            </div>
            {/* all three stay mounted so a running job and its results survive switching tabs */}
            <div style={{ display: tab === 'mc' ? 'block' : 'none' }}><MonteCarloView {...common} /></div>
            <div style={{ display: tab === 'wfa' ? 'block' : 'none' }}><WalkForwardView {...common} /></div>
            <div style={{ display: tab === 'sens' ? 'block' : 'none' }}><SensitivityView {...common} /></div>
            <div className="note">Research results are a guide to how fragile a strategy is, not a promise. Past data cannot rule out a change in how the market behaves.</div>
        </div>)
}