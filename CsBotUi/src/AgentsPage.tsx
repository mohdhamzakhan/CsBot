import { useCallback, useEffect, useMemo, useState } from 'react'
import './agents.css'
import { api } from './api'
import type { LiveTick, Market, MarketRef, SavedScript, ScriptTemplate, StrategyDef, StrategyIn } from './api'
import { agentsApi } from './agentsApi'
import type { AgentRow, AgentsSnap } from './agentsApi'
import AgentChart from './AgentChart'
import Vortex from './Vortex'
import ScannerPanel from './ScannerPanel'
import { DAYS_FOR, TIMEFRAMES } from './TopBar'

const tfName = (m: number) => TIMEFRAMES.find(t => t[0] === m)?.[1] ?? `${m}m`
const fmt = (x: number, d = 2) => x.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d })
const sgn = (x: number) => (x >= 0 ? '+' : '\u2212')
const stance = (a: AgentRow) => String(a.account?.side ?? 'flat')
const DEFAULT_TEAM = ['ema_trend', 'macd_trend', 'supertrend', 'donchian', 'rsi_pullback', 'bollinger_revert']

/** INR is shown with the rupee sign in front; USDT as a small unit after the number. */
function Money({ v, q, d = 2, sign }: { v: number; q: string | null; d?: number; sign?: boolean }) {
    const s = sign ? sgn(v) : v < 0 ? '\u2212' : ''
    const a = Math.abs(v)
    if (q === 'INR') return <>{s}&#8377;{a.toLocaleString('en-IN', { minimumFractionDigits: d, maximumFractionDigits: d })}</>
    return <>{s}{fmt(a, d)}<em>{q ?? 'USDT'}</em></>
}

function useNow() {
    const [n, setN] = useState(Date.now())
    useEffect(() => { const id = setInterval(() => setN(Date.now()), 1000); return () => clearInterval(id) }, [])
    return n
}

/** A small line (optionally filled) over time. All points are [unixSeconds, value]. */
function Spark({ pts, color, fill, height = 48, range, zero }: { pts: number[][]; color: string; fill?: boolean; height?: number; range?: [number, number]; zero?: boolean }) {
    if (pts.length < 2) return <div className="a-spark-empty" style={{ height }}>collecting data...</div>
    const t0 = pts[0][0], span = Math.max(1, pts[pts.length - 1][0] - t0)
    let lo = range ? range[0] : Math.min(...pts.map(p => p[1])), hi = range ? range[1] : Math.max(...pts.map(p => p[1]))
    if (hi - lo < 1e-9) { hi += 1; lo -= 1 }
    const W = 100, X = (t: number) => ((t - t0) / span) * W, Y = (v: number) => height - 2 - ((v - lo) / (hi - lo)) * (height - 4)
    const d = pts.map((p, i) => `${i ? 'L' : 'M'}${X(p[0]).toFixed(2)},${Y(p[1]).toFixed(2)}`).join('')
    return (
        <svg viewBox={`0 0 ${W} ${height}`} preserveAspectRatio="none" style={{ width: '100%', height, display: 'block' }}>
            {zero && lo < 0 && hi > 0 && <line x1="0" x2={W} y1={Y(0)} y2={Y(0)} stroke="#9aa5bd" strokeDasharray="2 2" vectorEffect="non-scaling-stroke" strokeWidth="1" />}
            {fill && <path d={`${d}L${W},${height}L0,${height}Z`} fill={color} opacity=".12" />}
            <path d={d} fill="none" stroke={color} strokeWidth="1.6" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
        </svg>
    )
}

function Face({ color }: { color: string }) {
    return (
        <svg viewBox="0 0 48 48" className="a-face" aria-hidden>
            <circle cx="24" cy="24" r="22" fill="#10131c" stroke={color} strokeWidth="3" />
            <rect x="13" y="19" width="8" height="8" rx="2" fill="#fff" /><rect x="27" y="19" width="8" height="8" rx="2" fill="#fff" />
            <rect x="16" y="22" width="3" height="3" fill="#10131c" /><rect x="30" y="22" width="3" height="3" fill="#10131c" />
            <rect x="17" y="33" width="14" height="2.5" rx="1" fill={color} />
        </svg>
    )
}

/** Agents that currently hold the same stance are joined by a line. */
function LinkGraph({ agents }: { agents: AgentRow[] }) {
    const n = agents.length
    const pos = agents.map((_, i) => ({ x: n === 1 ? 100 : 16 + (i * 168) / (n - 1), y: i % 2 ? 62 : 24 }))
    const edges: [number, number][] = []
    for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) if (agents[i].running && agents[j].running && stance(agents[i]) === stance(agents[j])) edges.push([i, j])
    return (
        <svg viewBox="0 0 200 86" preserveAspectRatio="xMidYMid meet" aria-label="Agents holding the same stance are linked">
            {edges.map(([i, j], k) => <line key={k} x1={pos[i].x} y1={pos[i].y} x2={pos[j].x} y2={pos[j].y} stroke={agents[i].color} strokeOpacity=".45" strokeWidth="1.3" />)}
            {pos.map((p, i) => <circle key={agents[i].id} cx={p.x} cy={p.y} r="5" fill={agents[i].color} opacity={agents[i].running ? 1 : 0.35} />)}
        </svg>
    )
}

function Panel({ n, title, tag, cls, children }: { n: string; title: string; tag?: string; cls?: string; children: React.ReactNode }) {
    return (
        <section className={`a-panel ${cls ?? ''}`}>
            <div className="a-ph"><span className="n">{n}</span><span className="t">{title}</span>{tag && <span className="tag">{tag}</span>}</div>
            {children}
        </section>
    )
}

// =====================================================================================
// add agents
// =====================================================================================
function AddDialog({ quote, onClose, onDone }: { quote: string | null; onClose: () => void; onDone: () => void }) {
    const inr = quote === 'INR'
    const [tab, setTab] = useState<'team' | 'one'>('team')
    const [market, setMarket] = useState<Market>(inr ? 'spot' : 'futures')
    const [symbol, setSymbol] = useState(inr ? 'BTC/INR' : 'BTCUSDT')
    const [tf, setTf] = useState(60)
    const [capital, setCapital] = useState(inr ? 10000 : 100)
    const [lev, setLev] = useState(1)
    const [name, setName] = useState('')
    const [builtins, setBuiltins] = useState<StrategyDef[]>([])
    const [templates, setTemplates] = useState<ScriptTemplate[]>([])
    const [saved, setSaved] = useState<SavedScript[]>([])
    const [pick, setPick] = useState('t:ema_trend')
    const [team, setTeam] = useState<string[]>(DEFAULT_TEAM)
    const [fee, setFee] = useState<number | null>(null)
    const [busy, setBusy] = useState(false)
    const [err, setErr] = useState('')

    useEffect(() => {
        api.strategies().then(s => setBuiltins(s.filter(x => x.id !== 'script' && x.id !== 'rules' && x.id !== 'portfolio'))).catch(() => { })
        api.scriptTemplates().then(t => { setTemplates(t.filter(x => x.id !== 'blank')); setTeam(cur => cur.filter(id => t.some(x => x.id === id))) }).catch(() => { })
        api.scriptSaved().then(setSaved).catch(() => { })
        api.myFee('coinswitchx', 'BTC').then(r => { if (r.found && r.takerPct != null) setFee(r.takerPct) }).catch(() => { })   // use your real spot fee when known
    }, [])

    const switchMarket = (m: Market) => { setMarket(m); setSymbol(m === 'spot' ? 'BTC/INR' : 'BTCUSDT'); setCapital(m === 'spot' ? 10000 : 100) }
    const toggle = (id: string) => setTeam(t => (t.includes(id) ? t.filter(x => x !== id) : [...t, id]))

    const submit = async () => {
        setBusy(true); setErr('')
        try {
            const sym = symbol.trim().toUpperCase()
            const data: MarketRef = { market, exchange: market === 'futures' ? 'EXCHANGE_2' : sym.endsWith('/USDT') ? 'c2c1' : 'coinswitchx', symbol: sym, intervalMin: tf, days: 30 }
            const costs = market === 'spot' ? (fee != null ? { fee } : undefined) : { leverage: lev }
            if (tab === 'team') await agentsApi.preset({ data, costs, capital, picks: team })
            else {
                let strategy: StrategyIn
                if (pick.startsWith('b:')) strategy = { id: pick.slice(2), params: {} }
                else if (pick.startsWith('t:')) strategy = { id: 'script', params: {}, code: templates.find(t => t.id === pick.slice(2))?.code ?? '' }
                else strategy = { id: 'script', params: {}, code: saved.find(s => s.name === pick.slice(2))?.code ?? '' }
                await agentsApi.add({ name: name.trim() || undefined, data, strategy, costs, capital })
            }
            onDone()
        } catch (e: any) { setErr(e.message) } finally { setBusy(false) }
    }

    return (
        <div className="a-modal" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
            <div className="a-dialog">
                <div style={{ display: 'flex', alignItems: 'center' }}><h3 style={{ flex: 1 }}>Add agents</h3><button className="a-btn tiny" onClick={onClose}>Close</button></div>
                <div className="a-note" style={{ marginTop: 0 }}>Each agent is a separate PAPER trader with its own simulated money, running at the same time as the others. No real orders are placed.</div>
                <div className="a-tabs"><button className={tab === 'team' ? 'on' : ''} onClick={() => setTab('team')}>A team</button><button className={tab === 'one' ? 'on' : ''} onClick={() => setTab('one')}>One agent</button></div>

                <div className="a-two">
                    <div><label>Market</label><select value={market} onChange={e => switchMarket(e.target.value as Market)}><option value="futures">Futures (USDT)</option><option value="spot">Spot</option></select></div>
                    <div><label>Symbol</label><input value={symbol} onChange={e => setSymbol(e.target.value)} autoCapitalize="characters" /></div>
                    <div><label>Candles</label><select value={tf} onChange={e => setTf(+e.target.value)}>{TIMEFRAMES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></div>
                    <div><label>Capital per agent ({market === 'spot' ? (symbol.toUpperCase().split('/')[1] ?? 'INR') : 'USDT'})</label><input type="number" inputMode="decimal" value={capital} onChange={e => setCapital(+e.target.value)} /></div>
                    {market === 'futures' && <div><label>Max leverage</label><input type="number" inputMode="decimal" value={lev} onChange={e => setLev(+e.target.value)} /></div>}
                    {tab === 'one' && <div><label>Name (optional)</label><input value={name} maxLength={12} onChange={e => setName(e.target.value)} /></div>}
                </div>

                {tab === 'one' && (<>
                    <label>Strategy</label>
                    <select value={pick} onChange={e => setPick(e.target.value)}>
                        <optgroup label="Ready-made scripts">{templates.map(t => <option key={t.id} value={'t:' + t.id}>{t.name}</option>)}</optgroup>
                        <optgroup label="Built-in strategies">{builtins.map(s => <option key={s.id} value={'b:' + s.id}>{s.name}</option>)}</optgroup>
                        {saved.length > 0 && <optgroup label="My saved scripts">{saved.map(s => <option key={s.name} value={'s:' + s.name}>{s.name}</option>)}</optgroup>}
                    </select>
                    <div className="a-note">Runs with the strategy's default settings. To use tuned settings, save them as a script first (Strategy tab).</div>
                </>)}

                {tab === 'team' && (<>
                    <label>Strategies in the team ({team.length} selected)</label>
                    {templates.map(t => (
                        <label key={t.id} className="a-pick" style={{ textTransform: 'none', letterSpacing: 0, font: 'inherit', color: 'inherit', margin: 0 }}>
                            <input type="checkbox" checked={team.includes(t.id)} onChange={() => toggle(t.id)} />{t.name}
                        </label>))}
                    <div className="a-note">The pre-selected six mix trend followers and pullback/mean-reversion styles. On the same market they will often agree; that is not independent confirmation.</div>
                </>)}

                {market === 'spot' && fee != null && <div className="a-note">Using your real spot fee: {fee}% (+ GST).</div>}
                {err && <div className="a-err" style={{ marginTop: 10 }}>{err}</div>}
                <button className="a-btn main" style={{ width: '100%', marginTop: 14 }} disabled={busy || capital <= 0 || (tab === 'team' && team.length === 0)} onClick={submit}>
                    {busy ? 'Starting...' : tab === 'team' ? `Launch ${team.length} agent${team.length === 1 ? '' : 's'}` : 'Launch agent'}
                </button>
            </div>
        </div>
    )
}

// =====================================================================================
// the page
// =====================================================================================
export default function AgentsPage({ onBack }: { onBack: () => void }) {
    const [snap, setSnap] = useState<AgentsSnap | null>(null)
    const [err, setErr] = useState('')
    const [focus, setFocus] = useState('')
    const [dlg, setDlg] = useState(false)
    const [lv, setLv] = useState<LiveTick | null>(null)
    const [busy, setBusy] = useState(false)
    const [tab, setTab] = useState<'agents' | 'scanner'>('agents')
    const [narrow, setNarrow] = useState(window.innerWidth <= 700)
    const now = useNow()

    useEffect(() => { const f = () => setNarrow(window.innerWidth <= 700); window.addEventListener('resize', f); return () => window.removeEventListener('resize', f) }, [])
    const load = useCallback(async () => { try { setSnap(await agentsApi.list()); setErr('') } catch (e: any) { setErr(e.message) } }, [])
    useEffect(() => { load(); const id = setInterval(() => { if (!document.hidden) load() }, 3000); return () => clearInterval(id) }, [load])
    const act = async (fn: () => Promise<unknown>) => { setBusy(true); try { await fn(); await load() } catch (e: any) { setErr(e.message) } finally { setBusy(false) } }

    /** Copies an agent's market, timeframe and strategy into the workbench and opens its Paper / Live tab. It starts nothing: the live gate and the typed confirmation still apply. */
    const goLive = async (a: AgentRow) => {
        const [tpl, sv] = await Promise.all([api.scriptTemplates().catch(() => [] as ScriptTemplate[]), api.scriptSaved().catch(() => [] as SavedScript[])])
        const s = a.strategy
        let pick = s.id === 'script' ? 'new' : 'b:' + s.id
        if (s.id === 'script') { const t = tpl.find(x => x.code === s.code), v = sv.find(x => x.code === s.code); pick = t ? 't:' + t.id : v ? 's:' + v.name : 'new' }
        let old: Record<string, unknown> = {}
        try { old = JSON.parse(localStorage.getItem('csbot.state.v1') || '{}') || {} } catch { /* start fresh */ }
        localStorage.setItem('csbot.state.v1', JSON.stringify({ ...old, data: { ...a.data, days: DAYS_FOR[a.data.intervalMin] ?? 365 }, sid: s.id, pick, code: s.code ?? '', params: s.params ?? {} }))
        sessionStorage.setItem('csbot.tab', 'trade')
        onBack()
    }

    const agents = snap?.agents ?? []
    const q = snap?.quote ?? null
    const c = snap?.combined
    const fa = agents.find(a => a.id === focus) ?? agents[0]
    const marks = useMemo(() => (fa && snap ? snap.fills.filter(f => f.symbol === fa.data.symbol).map(f => ({ t: f.t, side: f.side, name: f.name, color: f.color })) : []), [snap, fa])
    const symbols = useMemo(() => { const m = new Map<string, number>(); agents.forEach(a => { if (a.price > 0) m.set(a.data.symbol, a.price) }); return [...m] }, [agents])

    const running = agents.filter(a => a.running)
    const sides = running.map(stance)
    const coherence = sides.length ? Math.max(...['long', 'short', 'flat'].map(s => sides.filter(x => x === s).length)) / sides.length * 100 : 0
    const conv = c?.conviction ?? 0
    const pnlUp = (c?.pnl ?? 0) >= 0
    const utc = new Date(now).toISOString().slice(11, 19)
    const el = snap ? Math.max(0, Math.floor(now / 1000 - snap.sessionStart)) : 0
    const session = `${String(Math.floor(el / 3600)).padStart(2, '0')}:${String(Math.floor((el % 3600) / 60)).padStart(2, '0')}:${String(el % 60).padStart(2, '0')}`
    const chg = lv?.changePct ?? null
    const shownPx = lv?.price ?? fa?.price ?? 0

    return (
        <div className="astra">
            <header className="a-top">
                <button className="a-btn tiny" onClick={onBack} aria-label="Back to the workbench">&larr; Workbench</button>
                <div className="a-logo"><svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2" strokeLinecap="round"><path d="M12 3v18M3 12h18M5.6 5.6l12.8 12.8M18.4 5.6L5.6 18.4" /></svg></div>
                <div className="a-brand"><b>ASTRA</b><small>/ agent terminal &middot; {agents.length} agent{agents.length === 1 ? '' : 's'}</small></div>
                <nav className="a-nav"><button className={tab === 'agents' ? 'on' : ''} onClick={() => setTab('agents')}>Agents</button><button className={tab === 'scanner' ? 'on' : ''} onClick={() => setTab('scanner')}>Scanner</button></nav>
                <div className="a-spacer" />
                <span className="a-mode"><i />PAPER TRADING</span>
                <div className="a-clock"><b>{utc}</b><small>UTC / SESSION {session}</small></div>
                <button className="a-btn main" onClick={() => setDlg(true)}>+ Add agents</button>
                {running.length > 0 && <button className="a-btn danger" disabled={busy} onClick={() => act(agentsApi.stopAll)}>Stop all</button>}
            </header>

            <div className="a-ticker">
                <span className="lead">MARKET STREAM</span>
                {symbols.map(([s, p]) => <span key={s}><b>{s}</b> {fmt(p, p >= 1000 ? 2 : 4)}</span>)}
                <span>{running.length}/{agents.length} agents running</span>
                <span>real prices &middot; simulated money</span>
            </div>

            <div className="a-wrap">
                {err && <div className="a-err">{err}</div>}

                {tab === 'scanner' ? <ScannerPanel onPromoted={load} /> : <>
                    {snap && agents.length === 0 && (
                        <div className="a-hero">
                            <h2>No agents running</h2>
                            <p>Launch several strategies at once. Each one trades its own simulated account on live prices, and this page shows them side by side with a combined result.</p>
                            <div className="a-actions"><button className="a-btn main" onClick={() => setDlg(true)}>Launch a team of six</button></div>
                        </div>)}

                    {agents.length > 0 && c && (<>
                        <section className="a-kpis">
                            <div className="a-kpi"><span>System balance</span><b><Money v={c.equity} q={q} /></b><small>initial capital <Money v={c.capital} q={q} d={0} /></small></div>
                            <div className="a-kpi"><span>Combined P&amp;L</span><b className={pnlUp ? 'a-pos' : 'a-neg'}><Money v={c.pnl} q={q} sign /></b><small>{sgn(c.pnlPct)}{Math.abs(c.pnlPct).toFixed(2)}% on capital</small></div>
                            <div className="a-kpi"><span>Win rate</span><b style={{ color: c.winRate != null && c.winRate >= 50 ? 'var(--up)' : undefined }}>{c.winRate != null ? c.winRate.toFixed(1) + '%' : '\u2014'}</b><small>{c.closed} closed trades</small></div>
                            <div className="a-kpi"><span>Signals processed</span><b>{c.signals.toLocaleString('en-US')}</b><small>candles evaluated by agents</small></div>
                            <div className="a-kpi"><span>Active agents</span><b>{String(c.active).padStart(2, '0')}<em>/ {String(c.total).padStart(2, '0')}</em></b><small>running now</small></div>
                        </section>

                        <div className="a-grid">
                            <Panel n="01" title="The vortex" tag="agent conviction" cls="s3">
                                <div className="a-vortex">
                                    <Vortex intensity={0.7 + conv * 1.5} />
                                    <div className="a-readout" style={{ left: 12, bottom: 12 }}>Angular velocity<b>{(0.9 + conv * 2.2).toFixed(2)} rad/s</b></div>
                                    <div className="a-readout" style={{ right: 12, bottom: 12 }}>Agents in flow<b>{c.active} / {c.total}</b></div>
                                </div>
                                <div className="a-link"><LinkGraph agents={agents} /></div>
                                <div className="a-stats4">
                                    <span>Coherence<b>{coherence.toFixed(0)}%</b></span><span>Conviction<b>{(conv * 100).toFixed(0)}%</b></span>
                                    <span>Running<b>{c.active}</b></span><span>Fills<b>{c.fillsTotal}</b></span>
                                </div>
                                <div className="a-note" style={{ padding: '0 14px 10px', marginTop: 0 }}>Decorative: it spins faster the larger the positions the running agents want. Linked dots hold the same stance.</div>
                            </Panel>

                            <Panel n="02" title={fa ? fa.data.symbol : 'Chart'} tag="live tape" cls="s3">
                                {fa ? (<>
                                    <div className="a-chart-head">
                                        <span className={`px ${chg != null ? (chg >= 0 ? 'up' : 'dn') : ''}`}>{shownPx ? fmt(shownPx, shownPx >= 1000 ? 2 : 4) : '...'}</span>
                                        {chg != null && <small className={chg >= 0 ? 'a-pos' : 'a-neg'}>{sgn(chg)}{Math.abs(chg).toFixed(2)}% 24h</small>}
                                        {lv?.high != null && lv?.low != null && <small>H {fmt(lv.high, 2)} &middot; L {fmt(lv.low, 2)}</small>}
                                        <small>{tfName(fa.data.intervalMin)} &middot; {fa.name}{agents.length > 1 ? ' (tap an agent below to switch)' : ''}</small>
                                    </div>
                                    <AgentChart data={fa.data} marks={marks} height={narrow ? 230 : 330} onLive={setLv} />
                                </>) : <div className="a-empty">No agent selected.</div>}
                            </Panel>

                            <Panel n="03" title="Combined profit" tag={`${c.active} agents`} cls="s2">
                                <div className="a-pb">
                                    <div className={pnlUp ? 'a-pos' : 'a-neg'} style={{ font: '500 26px var(--mono)' }}><Money v={c.pnl} q={q} sign /></div>
                                    <div className="a-note" style={{ marginTop: 0 }}>{sgn(c.pnlPct)}{Math.abs(c.pnlPct).toFixed(2)}% net return, after simulated fees and slippage</div>
                                    <Spark pts={c.curve} color={pnlUp ? '#1b9a7f' : '#d6455d'} fill height={120} zero />
                                </div>
                            </Panel>

                            <Panel n="04" title="Hesitation trace" tag={`${(conv * 100).toFixed(0)}% conviction`} cls="s2">
                                <div className="a-pb">
                                    <div className="a-note" style={{ marginTop: 0 }}>Average position size the running agents want. Dips are moments when agents stepped aside.</div>
                                    <Spark pts={c.convictionCurve} color="#3b4660" height={120} range={[0, 1]} />
                                </div>
                            </Panel>

                            <Panel n="05" title="Execution stream" tag={`${c.fillsTotal} fills`} cls="s2">
                                <div className="a-pb">
                                    {snap!.fills.length === 0 ? <div className="a-empty" style={{ minHeight: 120 }}>No trades yet. Agents act when a candle closes.</div> : (
                                        <div className="a-stream">
                                            {snap!.fills.slice(0, 40).map((f, i) => (
                                                <div className="a-srow" key={i}>
                                                    <span className="tm">{new Date(f.t * 1000).toLocaleTimeString('en-GB')}</span>
                                                    <span className="who" style={{ color: f.color }}>{f.name.slice(0, 4)}</span>
                                                    <span><b className={f.side === 'buy' ? 'a-pos' : 'a-neg'}>{f.side.toUpperCase()}</b> <small>{f.symbol} {f.qty.toFixed(5)}</small></span>
                                                    <span className="px">{fmt(f.price, f.price >= 1000 ? 2 : 4)}</span>
                                                </div>))}
                                        </div>)}
                                </div>
                            </Panel>
                        </div>

                        <div className="a-ledger-h"><span>The {agents.length} / agent ledger</span><span>each agent, one strategy &middot; shared P&amp;L</span></div>
                        <div className="a-ledger">
                            {agents.map((a, i) => {
                                const side = stance(a), up = a.pnl >= 0, bad = a.state === 'error' || a.state === 'halted'
                                return (
                                    <article key={a.id} className={`a-agent ${fa?.id === a.id ? 'on' : ''} ${a.running ? '' : 'off'}`} style={{ ['--c' as any]: a.color }} onClick={() => setFocus(a.id)}>
                                        <div className="a-agent-top"><span>AG {String(i + 1).padStart(2, '0')}</span><span>{tfName(a.data.intervalMin)} &middot; {a.data.symbol}</span></div>
                                        <Face color={a.color} />
                                        <h4>{a.name}</h4>
                                        <div className="a-role" title={a.role}>{a.role}</div>
                                        <div className="a-meta"><span>{side}</span><span>win <b>{a.winRate != null ? Math.round(a.winRate) + '%' : '\u2014'}</b></span></div>
                                        <div className={`a-apnl ${up ? 'a-pos' : 'a-neg'}`}><Money v={a.pnl} q={q} sign d={q === 'INR' ? 0 : 2} /></div>
                                        <Spark pts={a.curve} color={a.color} height={32} />
                                        <span className={`a-state ${bad ? 'bad' : ''}`} title={bad ? a.note : a.error}>{a.running ? (bad ? a.state : a.error ? 'retrying' : 'running') : 'stopped'}</span>
                                        <div className="a-ctl" onClick={e => e.stopPropagation()}>
                                            {a.running ? <button className="a-btn tiny" disabled={busy} onClick={() => act(() => agentsApi.stop(a.id))}>Stop</button>
                                                : <button className="a-btn tiny" disabled={busy} onClick={() => act(() => agentsApi.resume(a.id))}>Resume</button>}
                                            {side !== 'flat' && <button className="a-btn tiny" disabled={busy} onClick={() => { if (window.confirm(`Close ${a.name}'s simulated position now?`)) act(() => agentsApi.close(a.id)) }}>Close</button>}
                                            <button className="a-btn tiny" disabled={busy} title="Copy this strategy to the Paper / Live tab (starts nothing)" onClick={() => { if (window.confirm(`Open ${a.name}'s strategy in the Paper / Live tab? Nothing starts: you still run the backtest gate and type the confirmation phrase.`)) goLive(a) }}>Go live</button>
                                            <button className="a-btn tiny danger" disabled={busy} onClick={() => { if (window.confirm(`Remove ${a.name} and its simulated account?`)) act(() => agentsApi.remove(a.id)) }}>&times;</button>
                                        </div>
                                    </article>)
                            })}
                        </div>
                        <div className="a-note" style={{ marginTop: 14 }}>
                            All agents run on paper money with real CoinSwitch prices, so results ignore queue position and real-world fill problems. Agents keep running on the server when this page is closed and are restored after a restart.
                        </div>
                    </>)}
                </>}
            </div>

            {dlg && <AddDialog quote={q} onClose={() => setDlg(false)} onDone={() => { setDlg(false); load() }} />}
        </div>
    )
}