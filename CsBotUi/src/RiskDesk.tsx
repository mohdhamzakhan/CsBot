import { useMemo, useState } from 'react'
import type { AgentRow } from './agentsApi'
import { alignPnl, combinedEquity, corr, effectiveBets, exposure, maxDrawdown } from './riskMath'

type Tab = 'exposure' | 'agree' | 'whatif'
const fmt = (x: number, d = 2) => x.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d })
const stance = (a: AgentRow) => String(a.account?.side ?? 'flat')
const held = (a: AgentRow) => { const w = Number(a.account?.weight ?? 0); return Number.isFinite(w) ? w : 0 }

/** Two lines on one chart (index on the x axis). */
function Lines({ a, b, height = 110 }: { a: number[]; b: number[]; height?: number }) {
    if (a.length < 2) return <div className="a-spark-empty" style={{ height }}>collecting data...</div>
    const all = [...a, ...b]
    let lo = Math.min(...all), hi = Math.max(...all)
    if (hi - lo < 1e-9) { hi += 1; lo -= 1 }
    const W = 100, X = (i: number) => (i / (a.length - 1)) * W, Y = (v: number) => height - 3 - ((v - lo) / (hi - lo)) * (height - 6)
    const path = (s: number[]) => s.map((v, i) => `${i ? 'L' : 'M'}${X(i).toFixed(2)},${Y(v).toFixed(2)}`).join('')
    return (
        <svg viewBox={`0 0 ${W} ${height}`} preserveAspectRatio="none" style={{ width: '100%', height, display: 'block' }}>
            <path d={path(b)} fill="none" stroke="#9aa5bd" strokeWidth="1.2" strokeDasharray="3 3" vectorEffect="non-scaling-stroke" />
            <path d={path(a)} fill="none" stroke="#2d6bff" strokeWidth="1.8" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
        </svg>
    )
}

/** Replaces the decorative vortex: tap and toggle things to see how concentrated, how correlated and how risky your six agents really are. */
export default function RiskDesk({ agents, q, onOpen }: { agents: AgentRow[]; q: string | null; onOpen: (id: string) => void }) {
    const [tab, setTab] = useState<Tab>('exposure')
    const [pair, setPair] = useState<[number, number] | null>(null)
    const [off, setOff] = useState<Set<string>>(new Set())

    const money = (v: number, sign = false) => {
        const s = v < 0 ? '\u2212' : sign ? '+' : ''
        return q === 'INR' ? `${s}\u20B9${Math.abs(v).toLocaleString('en-IN', { maximumFractionDigits: 0 })}` : `${s}${fmt(Math.abs(v), 2)} USDT`
    }

    const al = useMemo(() => alignPnl(agents), [agents])
    const matrix = useMemo(() => agents.map((_, i) => agents.map((__, j) => (i === j ? 1 : corr(al.v[i], al.v[j])))), [agents, al])
    const avgCorr = useMemo(() => {
        const xs: number[] = []
        for (let i = 0; i < agents.length; i++) for (let j = i + 1; j < agents.length; j++) { const c = matrix[i][j]; if (c != null) xs.push(c) }
        return xs.length ? xs.reduce((s, x) => s + x, 0) / xs.length : null
    }, [agents.length, matrix])

    const run = agents.filter(a => a.running)
    const ex = exposure(run.map(held))
    const maxAbs = Math.max(1, ...agents.map(a => Math.abs(held(a))))

    const include = agents.map(a => !off.has(a.id))
    const caps = agents.map(a => a.capital)
    const eqSel = useMemo(() => combinedEquity(al.v, caps, include), [al, agents, off])   // eslint-disable-line react-hooks/exhaustive-deps
    const eqAll = useMemo(() => combinedEquity(al.v, caps, caps.map(() => true)), [al, agents])   // eslint-disable-line react-hooks/exhaustive-deps
    const capSel = caps.reduce((s, c, i) => s + (include[i] ? c : 0), 0), capAll = caps.reduce((s, c) => s + c, 0)
    const pnlSel = eqSel.length ? eqSel[eqSel.length - 1] - capSel : 0, pnlAll = eqAll.length ? eqAll[eqAll.length - 1] - capAll : 0
    const ddSel = maxDrawdown(eqSel), ddAll = maxDrawdown(eqAll)
    const nSel = include.filter(Boolean).length

    const shade = (c: number | null) => (c == null ? '#f1f3f9' : c >= 0 ? `rgba(45,107,255,${0.1 + Math.abs(c) * 0.6})` : `rgba(232,125,20,${0.1 + Math.abs(c) * 0.6})`)

    return (
        <div className="a-pb">
            <nav className="a-nav" style={{ width: '100%', marginBottom: 10 }}>
                {([['exposure', 'Exposure'], ['agree', 'Agreement'], ['whatif', 'What-if']] as [Tab, string][]).map(([k, l]) =>
                    <button key={k} style={{ flex: 1 }} className={tab === k ? 'on' : ''} onClick={() => setTab(k)}>{l}</button>)}
            </nav>

            {tab === 'exposure' && (<>
                {agents.map(a => {
                    const w = held(a), pct = (Math.abs(w) / maxAbs) * 50
                    return (
                        <div className="a-rd-row" key={a.id} style={{ opacity: a.running ? 1 : 0.45 }}>
                            <button className="a-rd-name" style={{ color: a.color }} onClick={() => onOpen(a.id)} title="Open this agent">{a.name}</button>
                            <div className="a-rd-bar"><i className="mid" /><i className="fill" style={{ background: a.color, left: w >= 0 ? '50%' : `${50 - pct}%`, width: `${pct}%` }} /></div>
                            <span className="a-rd-val">{stance(a) === 'flat' ? 'flat' : `${stance(a)} ${Math.abs(w * 100).toFixed(0)}%`}</span>
                        </div>)
                })}
                <div className="a-note" style={{ marginTop: 10 }}>
                    {run.length === 0 ? 'No agents are running.' : <>
                        Averaged into one position these {run.length} agents would hold <b>{ex.net >= 0 ? '+' : '\u2212'}{Math.abs(ex.net * 100).toFixed(0)}%</b> net (gross {(ex.gross * 100).toFixed(0)}%).{' '}
                        <b>{ex.sameSide} of {run.length}</b> are {ex.side === 'flat' ? 'flat' : `${ex.side}`}.
                        {run.length > 1 && ex.sameSide === run.length && ex.side !== 'flat' ? ' They all agree, so right now this is one bet, not several.' : ''}
                    </>}
                </div>
                <div className="a-note">Bars show the position each agent holds (left = short, right = long). Tap a name to open that agent.</div>
            </>)}

            {tab === 'agree' && (<>
                {agents.length < 2 ? <div className="a-empty" style={{ minHeight: 100 }}>Add a second agent to compare.</div> : (<>
                    <div className="a-rd-matrix" style={{ gridTemplateColumns: `44px repeat(${agents.length}, 1fr)` }}>
                        <span />{agents.map(a => <span key={a.id} className="hd" style={{ color: a.color }}>{a.name.slice(0, 4)}</span>)}
                        {agents.map((r, i) => (
                            <div key={r.id} style={{ display: 'contents' }}>
                                <span className="hd" style={{ color: r.color }}>{r.name.slice(0, 4)}</span>
                                {agents.map((c, j) => {
                                    const v = matrix[i][j], sel = pair && ((pair[0] === i && pair[1] === j) || (pair[0] === j && pair[1] === i))
                                    return (
                                        <button key={c.id} className={`cell ${sel ? 'sel' : ''}`} style={{ background: i === j ? '#fff' : shade(v) }} onClick={() => setPair(i === j ? null : [i, j])}
                                            aria-label={`${r.name} and ${c.name}`}>
                                            {i === j ? (stance(r) === 'long' ? 'L' : stance(r) === 'short' ? 'S' : '\u2013') : v == null ? '' : Math.round(v * 100)}
                                        </button>)
                                })}
                            </div>))}
                    </div>
                    <div className="a-note">
                        {pair ? (() => {
                            const [i, j] = pair, c = matrix[i][j], A = agents[i], B = agents[j]
                            return <>
                                <b style={{ color: A.color }}>{A.name}</b> and <b style={{ color: B.color }}>{B.name}</b>: {c == null ? 'not enough trades yet to compare' : `correlation ${c.toFixed(2)} (${c > 0.7 ? 'they move almost together' : c > 0.3 ? 'partly together' : c > -0.3 ? 'mostly independent' : 'tend to oppose each other'})`}.
                                {' '}Right now {stance(A) === stance(B) ? `both are ${stance(A)}` : `${A.name} is ${stance(A)} and ${B.name} is ${stance(B)}`}.
                            </>
                        })() : 'Tap a cell to compare two agents. Numbers are the correlation of their results (100 = move together). Blue = together, orange = opposite. The diagonal shows each agent\'s position: L long, S short, \u2013 flat.'}
                    </div>
                    {avgCorr != null
                        ? <div className="a-note"><b>Average correlation {avgCorr.toFixed(2)}</b>: these {agents.length} agents behave like about <b>{effectiveBets(avgCorr, agents.length).toFixed(1)} independent bet{effectiveBets(avgCorr, agents.length) < 1.5 ? '' : 's'}</b>. Fewer independent bets means less protection from running several.</div>
                        : <div className="a-note">Correlation appears once the agents have some results (a few hours of data).</div>}
                </>)}
            </>)}

            {tab === 'whatif' && (<>
                <div className="a-chips">{agents.map(a => (
                    <button key={a.id} className={`a-chip ${off.has(a.id) ? '' : 'on'}`} style={off.has(a.id) ? undefined : { borderColor: a.color, color: a.color }}
                        onClick={() => setOff(s => { const n = new Set(s); if (n.has(a.id)) n.delete(a.id); else n.add(a.id); return n })}>{a.name}</button>))}
                </div>
                {nSel === 0 ? <div className="a-empty" style={{ minHeight: 100 }}>Tick at least one agent.</div> : (<>
                    <div style={{ marginTop: 10 }}><Lines a={eqSel} b={eqAll} /></div>
                    <div className="a-minis" style={{ marginTop: 8 }}>
                        <div className="a-mini"><span>Selected ({nSel})</span><b className={pnlSel >= 0 ? 'a-pos' : 'a-neg'}>{money(pnlSel, true)}</b><small>max drawdown {ddSel.toFixed(1)}%</small></div>
                        <div className="a-mini"><span>All {agents.length} agents</span><b className={pnlAll >= 0 ? 'a-pos' : 'a-neg'}>{money(pnlAll, true)}</b><small>max drawdown {ddAll.toFixed(1)}%</small></div>
                        <div className="a-mini"><span>Difference</span><b className={pnlSel - pnlAll >= 0 ? 'a-pos' : 'a-neg'}>{money(pnlSel - pnlAll, true)}</b><small>drawdown {ddSel - ddAll >= 0 ? '+' : '\u2212'}{Math.abs(ddSel - ddAll).toFixed(1)} pts</small></div>
                    </div>
                </>)}
                <div className="a-note">Untick agents to see what the combined result and drawdown would have been without them. Solid line = selected, dashed = all agents. It is built from the agents&rsquo; equity so far, so with only hours of data it shows noise, not a verdict. Removing the agent that happened to do worst always looks better in hindsight.</div>
            </>)}
        </div>
    )
}