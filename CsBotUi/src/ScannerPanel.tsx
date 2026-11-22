import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { Market, ScriptTemplate, StrategyDef } from './api'
import { scannerApi } from './scannerApi'
import type { ScanReq, ScanRow, ScanSnap } from './scannerApi'
import { TIMEFRAMES } from './TopBar'

const LEVS = [2, 3, 5, 10, 15, 20]
const DEFAULT_PICKS = ['t:ema_trend', 't:supertrend', 't:macd_trend', 't:donchian', 't:rsi_pullback', 't:bollinger_revert']
const tfName = (m: number) => TIMEFRAMES.find(t => t[0] === m)?.[1] ?? `${m}m`
const pct = (x: number, d = 1) => `${x >= 0 ? '+' : '\u2212'}${Math.abs(x).toFixed(d)}%`
const ago = (t: number) => { const s = Math.max(0, Math.round(Date.now() / 1000 - t)); return s < 90 ? `${s}s` : s < 5400 ? `${Math.round(s / 60)}m` : s < 172800 ? `${Math.round(s / 3600)}h` : `${Math.round(s / 86400)}d` }
type Sort = 'both' | 'pnl' | 'bt' | 'win' | 'trades' | 'dd'

export default function ScannerPanel({ onPromoted }: { onPromoted: () => void }) {
    const [snap, setSnap] = useState<ScanSnap | null>(null)
    const [err, setErr] = useState('')
    const [msg, setMsg] = useState('')
    const [busy, setBusy] = useState(false)
    const [market, setMarket] = useState<Market>('futures')
    const [symbols, setSymbols] = useState('BTCUSDT')
    const [tfs, setTfs] = useState<number[]>([15, 60, 240])
    const [picks, setPicks] = useState<string[]>(DEFAULT_PICKS)
    const [variants, setVariants] = useState(4)
    const [capital, setCapital] = useState(1000)
    const [lev, setLev] = useState<number | null>(null)   // futures: no default, you choose
    const [fee, setFee] = useState<number | null>(null)
    const [templates, setTemplates] = useState<ScriptTemplate[]>([])
    const [builtins, setBuiltins] = useState<StrategyDef[]>([])
    const [preview, setPreview] = useState<{ cells: number; groups: number; limit: number } | null>(null)
    const [previewErr, setPreviewErr] = useState('')
    const [sort, setSort] = useState<Sort>('both')
    const [minClosed, setMinClosed] = useState(2)
    const [bothPos, setBothPos] = useState(false)

    const load = useCallback(async () => { try { setSnap(await scannerApi.get()); setErr('') } catch (e: any) { setErr(e.message) } }, [])
    useEffect(() => { load(); const id = setInterval(() => { if (!document.hidden) load() }, 5000); return () => clearInterval(id) }, [load])
    useEffect(() => {
        api.scriptTemplates().then(t => setTemplates(t.filter(x => x.id !== 'blank'))).catch(() => { })
        api.strategies().then(s => setBuiltins(s.filter(x => x.id !== 'script' && x.id !== 'rules' && x.id !== 'portfolio'))).catch(() => { })
        api.myFee('coinswitchx', 'BTC').then(r => { if (r.found && r.takerPct != null) setFee(r.takerPct) }).catch(() => { })
    }, [])

    const req = useMemo<ScanReq>(() => ({
        market, symbols: symbols.split(',').map(s => s.trim().toUpperCase()).filter(Boolean), timeframes: tfs, picks, variants, capital,
        costs: market === 'spot' ? (fee != null ? { fee } : undefined) : (lev != null ? { leverage: lev } : undefined),
    }), [market, symbols, tfs, picks, variants, capital, fee, lev])

    const running = !!snap?.running
    useEffect(() => {      // how many combinations will this make? (also catches invalid input before starting)
        if (running || req.symbols.length === 0 || tfs.length === 0 || picks.length === 0 || capital <= 0) { setPreview(null); setPreviewErr(''); return }
        const id = setTimeout(() => { scannerApi.preview(req).then(p => { setPreview(p); setPreviewErr('') }).catch(e => { setPreview(null); setPreviewErr(e.message) }) }, 500)
        return () => clearTimeout(id)
    }, [req, running, tfs.length, picks.length, capital])

    const act = async (fn: () => Promise<unknown>, ok?: string) => {
        setBusy(true); setMsg(''); setErr('')
        try { await fn(); if (ok) setMsg(ok); await load() } catch (e: any) { setErr(e.message) } finally { setBusy(false) }
    }
    const toggle = <T,>(list: T[], v: T, set: (l: T[]) => void) => set(list.includes(v) ? list.filter(x => x !== v) : [...list, v])
    const switchMarket = (m: Market) => { setMarket(m); setLev(null); setSymbols(m === 'spot' ? 'BTC/INR' : 'BTCUSDT'); setCapital(m === 'spot' ? 100000 : 1000) }

    const rows = useMemo(() => {
        const r = (snap?.cells ?? []).filter(c => c.ready && c.closed >= minClosed && (!bothPos || (c.btReturn > 0 && c.pnlPct > 0)))
        const key: Record<Sort, (c: ScanRow) => number> = { both: c => Math.min(c.btReturn, c.pnlPct), pnl: c => c.pnlPct, bt: c => c.btReturn, win: c => c.winRate ?? -1, trades: c => c.closed, dd: c => -c.maxDd }
        return r.sort((a, b) => key[sort](b) - key[sort](a)).slice(0, 30)
    }, [snap, sort, minClosed, bothPos])

    const s = snap?.summary
    const hasCells = (snap?.cells.length ?? 0) > 0

    return (
        <div>
            {err && <div className="a-err">{err}</div>}
            {msg && <div className="a-ok">{msg}</div>}

            <section className="a-panel" style={{ marginBottom: 14 }}>
                <div className="a-ph"><span className="n">S1</span><span className="t">Background scanner</span><span className="tag">{running ? 'running' : 'stopped'}</span></div>
                <div className="a-pb">
                    <div className="a-note" style={{ marginTop: 0 }}>
                        Runs every strategy, setting variation and timeframe you pick at the same time, on live candles, with paper money. When one changes its mind it shows up in the signal feed, and its paper trade is recorded.
                        It never places real orders. A combination only starts trading after its strategy has gone flat once, so it reacts to fresh signals rather than a trend already under way.
                    </div>

                    {!running && (<>
                        <div className="a-two">
                            <div><label>Market</label><select value={market} onChange={e => switchMarket(e.target.value as Market)}><option value="futures">Futures (USDT)</option><option value="spot">Spot</option></select></div>
                            <div><label>Symbols (up to 4, comma separated)</label><input value={symbols} onChange={e => setSymbols(e.target.value)} autoCapitalize="characters" /></div>
                            <div><label>Settings variations per strategy</label><select value={variants} onChange={e => setVariants(+e.target.value)}>{[1, 2, 3, 4, 5, 6, 8].map(n => <option key={n} value={n}>{n === 1 ? '1 (defaults only)' : n}</option>)}</select></div>
                            <div><label>Paper capital per combination</label><input type="number" inputMode="decimal" value={capital} onChange={e => setCapital(+e.target.value)} /></div>
                        </div>
                        {market === 'futures' && (
                            <div style={{ gridColumn: '1 / -1' }}>
                                <label>Leverage &mdash; you choose</label>
                                <div className="a-chips">
                                    {LEVS.map(n => <button key={n} className={`a-chip ${lev === n ? 'on' : ''}`} onClick={() => setLev(n)}>{n}x</button>)}
                                    <input type="number" inputMode="decimal" min={1} max={50} placeholder="other" style={{ width: 90, minHeight: 32 }} value={lev != null && !LEVS.includes(lev) ? lev : ''}
                                        onChange={e => setLev(e.target.value === '' ? null : +e.target.value)} />
                                </div>
                                <div className="a-note" style={{ color: lev == null ? 'var(--down)' : undefined }}>
                                    {lev == null ? 'Pick a leverage to continue. There is no default.' : `Each combination: a position is the strategy's signal x ${lev} x capital, so ${capital} fully in holds ${(capital * lev).toLocaleString('en-US')}. A move of roughly ${(100 / lev).toFixed(1)}% against it would liquidate it (less after fees and maintenance margin). Paper trading does not simulate liquidation yet.`}
                                </div>
                            </div>)}
                        <label>Timeframes</label>
                        <div className="a-chips">{TIMEFRAMES.map(([v, l]) => <button key={v} className={`a-chip ${tfs.includes(v) ? 'on' : ''}`} onClick={() => toggle(tfs, v, setTfs)}>{l}</button>)}</div>
                        <label>Strategies ({picks.length} selected)</label>
                        <div className="a-chips">
                            {templates.map(t => <button key={t.id} className={`a-chip ${picks.includes('t:' + t.id) ? 'on' : ''}`} title={t.description} onClick={() => toggle(picks, 't:' + t.id, setPicks)}>{t.name}</button>)}
                            {builtins.map(b => <button key={b.id} className={`a-chip ${picks.includes('b:' + b.id) ? 'on' : ''}`} title={b.description} onClick={() => toggle(picks, 'b:' + b.id, setPicks)}>{b.name}</button>)}
                        </div>
                        <div className="a-note" style={{ color: previewErr ? 'var(--down)' : undefined }}>
                            {previewErr || (preview ? `${preview.cells} combinations across ${preview.groups} candle feeds (limit ${preview.limit}).` : 'Pick at least one symbol, timeframe and strategy.')}
                        </div>
                        <button className="a-btn main" style={{ width: '100%' }} disabled={busy || !preview} onClick={() => act(() => scannerApi.start(req), 'Scanner started. History loads first, which can take a minute.')}>
                            {busy ? 'Starting...' : 'Start scanning'}
                        </button>
                    </>)}

                    {running && (
                        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                            <button className="a-btn danger" disabled={busy} onClick={() => act(scannerApi.stop)}>Stop scanner</button>
                            <span className="a-note" style={{ margin: 0 }}>{snap!.req ? `${snap!.req.market} · ${snap!.req.symbols.join(', ')} · ${snap!.req.timeframes.map(tfName).join(', ')}` : ''}</span>
                        </div>)}
                    {!running && hasCells && <button className="a-btn" style={{ marginTop: 8 }} disabled={busy} onClick={() => { if (window.confirm('Clear all scanner results and signals?')) act(scannerApi.clear) }}>Clear results</button>}
                    {snap && snap.groups.length > 0 && <div className="a-note">{snap.groups.map(g => `${g.key.replace('|', ' · ')}: ${g.status}`).join('  |  ')}</div>}
                </div>
            </section>

            {s && hasCells && (<>
                <section className="a-kpis">
                    <div className="a-kpi"><span>Combinations</span><b>{s.cells}</b><small>{s.ready} ready</small></div>
                    <div className="a-kpi"><span>Armed</span><b>{s.armed}</b><small>waiting for a fresh signal</small></div>
                    <div className="a-kpi"><span>In a trade</span><b>{s.inPosition}</b><small>paper positions open</small></div>
                    <div className="a-kpi"><span>Profitable so far</span><b>{s.profitable}<em>/ {s.ready}</em></b><small>forward test, before tax</small></div>
                    <div className="a-kpi"><span>Signals, 24h</span><b>{s.signals24h}</b><small>stance changes</small></div>
                </section>
                <div className="a-warn">With {s.cells} combinations running, some will look good by luck alone. Judge a combination by whether it stays positive over weeks, with enough closed trades, and then check it in Research (walk-forward) before trusting it.</div>

                <div className="a-grid">
                    <section className="a-panel s3">
                        <div className="a-ph"><span className="n">S2</span><span className="t">Signal feed</span><span className="tag">{snap!.signals.length} recent</span></div>
                        <div className="a-pb">
                            {snap!.signals.length === 0 ? <div className="a-empty" style={{ minHeight: 120 }}>No signals yet. Strategies act when a candle closes, so the slower timeframes can take hours.</div> : (
                                <div className="a-stream" style={{ maxHeight: 360 }}>
                                    {snap!.signals.map((x, i) => (
                                        <div className="a-sig" key={i}>
                                            <span className="tm">{ago(x.t)} ago</span>
                                            <span><b className={x.to === 'long' ? 'a-pos' : x.to === 'short' ? 'a-neg' : ''}>{x.from} &rarr; {x.to}</b> <small>{x.symbol} {tfName(x.tf)} @ {x.price.toLocaleString('en-US', { maximumFractionDigits: 2 })}</small>
                                                <small style={{ display: 'block' }}>{x.name} &middot; {x.params}</small></span>
                                        </div>))}
                                </div>)}
                        </div>
                    </section>

                    <section className="a-panel s3">
                        <div className="a-ph"><span className="n">S3</span><span className="t">Forward-test leaderboard</span><span className="tag">top {rows.length}</span></div>
                        <div className="a-pb">
                            <div className="a-two" style={{ marginBottom: 6 }}>
                                <div><label style={{ marginTop: 0 }}>Rank by</label><select value={sort} onChange={e => setSort(e.target.value as Sort)}><option value="both">Weaker of backtest and forward</option><option value="bt">Backtest return</option><option value="pnl">Forward (paper) return</option><option value="win">Win rate</option><option value="trades">Closed trades</option><option value="dd">Smallest drawdown</option></select></div>
                                <div><label style={{ marginTop: 0 }}>Min closed trades</label><select value={minClosed} onChange={e => setMinClosed(+e.target.value)}>{[0, 1, 2, 3, 5, 10].map(n => <option key={n} value={n}>{n}</option>)}</select></div>
                            </div>
                            <label className="a-pick" style={{ margin: '0 0 6px' }}><input type="checkbox" checked={bothPos} onChange={e => setBothPos(e.target.checked)} />Only combinations that are positive in the backtest AND forward</label>
                            {rows.length === 0 ? <div className="a-empty" style={{ minHeight: 100 }}>Nothing qualifies yet. Lower the minimum closed trades, or give it time.</div> : (
                                <div className="a-scroll"><table className="a-table">
                                    <thead><tr><th>Strategy</th><th>Backtest</th><th>Forward</th><th>Trades</th><th>Win</th><th>Max DD</th><th>Now</th><th></th></tr></thead>
                                    <tbody>{rows.map(c => (
                                        <tr key={c.id}>
                                            <td><b>{c.name}</b><small>{c.symbol} &middot; {tfName(c.tf)} &middot; {c.params}</small></td>
                                            <td className={c.btReturn >= 0 ? 'a-pos' : 'a-neg'}>{c.btDone ? pct(c.btReturn, 1) : '\u2014'}<small>{c.btDone ? `${c.btTrades} trades \u00B7 ${c.btDays}d \u00B7 DD ${c.btMaxDd.toFixed(0)}%` : ''}</small></td>
                                            <td className={c.pnlPct >= 0 ? 'a-pos' : 'a-neg'}>{pct(c.pnlPct, 2)}</td>
                                            <td>{c.closed}</td><td>{c.winRate != null ? Math.round(c.winRate) + '%' : '\u2014'}</td><td>{c.maxDd.toFixed(1)}%</td>
                                            <td className={c.stance === 'long' ? 'a-pos' : c.stance === 'short' ? 'a-neg' : ''}>{c.stance}{c.armed ? '' : ' (waiting)'}</td>
                                            <td><button className="a-btn tiny" disabled={busy} title="Track this combination as a visible paper agent" onClick={() => act(async () => { await scannerApi.promote(c.id); onPromoted() }, `${c.name} added as a paper agent (see the Agents tab).`)}>Agent</button></td>
                                        </tr>))}</tbody>
                                </table></div>)}
                            <div className="a-note">Backtest = what the combination would have done on the history loaded when the scan started (same costs, so it is the in-sample column). Forward = what it has done since, on prices it had never seen. A combination that is good in both is worth a closer look; one that is good only in the backtest was probably fitted to the past. Paper results use real prices and simulated fees and slippage. &ldquo;Agent&rdquo; copies the exact settings into the Agents tab; from there &ldquo;Go live&rdquo; hands it to the Paper / Live tab, which keeps all of its safety checks.</div>
                        </div>
                    </section>
                </div>
            </>)}
        </div>
    )
}