import { useEffect, useState } from 'react'
import { api } from './api'
import type { Costs, MarketRef, SessionInfo, StrategyIn, TraderInfo, TraderStatus } from './api'

type Props = {
    data: MarketRef; strategy: StrategyIn; costs: Costs; liveEnabled: boolean; hasKeys: boolean; quote: string; price?: number | null
    st: TraderStatus | null; session: SessionInfo | null; onRefresh: () => void
}
const PHRASE = 'I UNDERSTAND THE RISKS'
const countdown = (sec: number) => { if (sec <= 0) return 'any moment'; const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), x = sec % 60; return `${h > 0 ? h + 'h ' : ''}${m}m ${String(x).padStart(2, '0')}s` }
const tfName = (m: number) => (m >= 1440 ? `${m / 1440}D` : m >= 60 ? `${m / 60}h` : `${m}m`)

// Helper to convert UTC time-only strings (e.g., "01:08:36") to the user's Local Time
const formatLogLine = (line: string) => {
    const timeMatch = line.match(/^(\d{2}):(\d{2}):(\d{2})/);
    if (timeMatch) {
        const [, h, m, s] = timeMatch.map(Number);

        // Create a date object using today's date, but interpreted as UTC
        const now = new Date();
        const utcDate = new Date(Date.UTC(
            now.getUTCFullYear(),
            now.getUTCMonth(),
            now.getUTCDate(),
            h, m, s
        ));

        // Format using local time getters
        const localH = String(utcDate.getHours()).padStart(2, '0');
        const localM = String(utcDate.getMinutes()).padStart(2, '0');
        const localS = String(utcDate.getSeconds()).padStart(2, '0');

        // Replace the timestamp at the beginning of the line
        return line.replace(/^(\d{2}:\d{2}:\d{2})/, `${localH}:${localM}:${localS}`);
    }

    // Fallback for full ISO/UTC timestamps
    return line.replace(/\b(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|UTC)?)\b/g, (match) => {
        const normalized = match.endsWith('UTC') ? match.replace('UTC', 'Z') : match;
        const d = new Date(normalized);
        return isNaN(d.getTime()) ? match : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
    });
}

/** Which strategy is running, with every setting (changed ones highlighted), the costs it assumes, and the script if it is one. */
function StrategyCard({ info, quote }: { info: TraderInfo; quote: string }) {
    const s = info.strategy, c = info.costs
    return (
        <div className="card">
            <h3>Running strategy</h3>
            <div style={{ fontWeight: 700, fontSize: 15 }}>{s.name}</div>
            <div className="note" style={{ marginTop: 2 }}>{s.description}</div>
            <div className="note">{info.mode.toUpperCase()} &middot; {info.symbol} &middot; {tfName(info.intervalMin)} candles &middot; capital {info.capital.toLocaleString('en-IN')} {quote} &middot; started {new Date(info.startedAt * 1000).toLocaleString()}</div>
            {s.parameters.length > 0 && (
                <div className="tablewrap"><table>
                    <thead><tr><th>Setting</th><th>Value</th><th>Default</th></tr></thead>
                    <tbody>{s.parameters.map(p => (
                        <tr key={p.key}><td>{p.label}</td><td><b style={p.value !== p.default ? { color: 'var(--amber)' } : undefined}>{p.value}</b></td><td style={{ color: 'var(--muted)' }}>{p.default}</td></tr>))}</tbody>
                </table></div>)}
            <div className="note">Costs assumed: fee {c.feePct}% + {c.gstPct}% GST &middot; slippage {c.slippagePct}% &middot; TDS {c.tdsPct}%{info.market === 'futures' ? ` \u00B7 funding ${c.fundingPer8hPct}%/8h \u00B7 max leverage ${c.maxLeverage}x` : ''}. Orange values differ from the strategy's defaults.</div>
            {info.risk && <div className="note">Live limits: max order {info.risk.maxOrderNotional} &middot; max position {info.risk.maxPositionNotional} &middot; max leverage {info.risk.maxLeverage}x &middot; daily loss {info.risk.dailyLossPct}%</div>}
            {s.code && <details><summary style={{ cursor: 'pointer', color: 'var(--accent)' }}>View script</summary><pre className="log" style={{ height: 'auto', maxHeight: 320 }}>{s.code}</pre></details>}
        </div>
    )
}

const f2 = (x: unknown, d = 2) => Number(x).toLocaleString(undefined, { maximumFractionDigits: d })

export default function TraderPanel({ data, strategy, costs, liveEnabled, hasKeys, quote,price, st, session, onRefresh }: Props) {
    const [mode, setMode] = useState<'paper' | 'live'>('paper')
    const [capital, setCapital] = useState(data.market === 'spot' ? 10000 : 100)
    const [maxOrder, setMaxOrder] = useState(0)
    const [maxPos, setMaxPos] = useState(0)
    const [maxLev, setMaxLev] = useState(1)
    const [dailyLoss, setDailyLoss] = useState(3)
    const [pricePrec, setPricePrec] = useState(0)
    const [qtyPrec, setQtyPrec] = useState(data.market === 'spot' ? 5 : 3)
    const [confirm, setConfirm] = useState('')
    const [resumeConfirm, setResumeConfirm] = useState('')
    const [ungated, setUngated] = useState(false)
    const [err, setErr] = useState('')
    const [busy, setBusy] = useState(false)
    const [now, setNow] = useState(Math.floor(Date.now() / 1000))
    useEffect(() => { setCapital(data.market === 'spot' ? 10000 : 100); setQtyPrec(data.market === 'spot' ? 5 : 3) }, [data.market])
    useEffect(() => { const id = setInterval(() => setNow(Math.floor(Date.now() / 1000)), 1000); return () => clearInterval(id) }, [])

    const run = async (fn: () => Promise<unknown>) => { setErr(''); setBusy(true); try { await fn() } catch (e: any) { setErr(e.message) } finally { setBusy(false); onRefresh() } }
    const start = () => run(() => api.traderStart({
        mode, data, strategy, costs, capital,
        risk: mode === 'live' ? { maxOrderNotional: maxOrder, maxPositionNotional: maxPos, maxLeverage: maxLev, dailyLossPct: dailyLoss } : undefined,
        pricePrecision: pricePrec, qtyPrecision: qtyPrec, confirm: mode === 'live' ? confirm : undefined, allowUngated: mode === 'live' ? ungated : undefined,
    }))

    const acct = st?.account as Record<string, any> | undefined
    const running = !!st?.running
    const side = acct ? String(acct.side) : 'flat'
    const pnl = acct ? Number(acct.equity) - Number(acct.start) : 0
    const saved = !!session?.exists && !running
    const savedLive = session?.mode === 'live'
    const blockedBySaved = saved && !!session?.hasPosition
    const MIN: Record<string, [number, number]> = { BTCUSDT: [0.002, 3], ETHUSDT: [0.02, 2], SOLUSDT: [0.23, 2] }
    const mn = data.market === 'futures' ? MIN[data.symbol.toUpperCase()] : undefined
    useEffect(() => { if (mn) setQtyPrec(mn[1]) }, [data.symbol, data.market])

    return (
        <div>
            {saved && (
                <div className="infobox">
                    <b>{session!.hasPosition ? 'Open position found from your last session' : 'A saved trader session was found'}</b>
                    <div style={{ fontFamily: 'var(--mono)', fontSize: 12.5, margin: '6px 0' }}>
                        {session!.mode?.toUpperCase()} &middot; {session!.symbol} &middot; {session!.intervalMin}m
                        {session!.hasPosition && <><br /><span className={session!.side === 'long' ? 'pos' : 'neg'}>{session!.side?.toUpperCase()}</span> {Number(session!.units).toFixed(6)} @ {f2(session!.entryPrice)}
                            {session!.openedAt ? <> &middot; opened {new Date(session!.openedAt * 1000).toLocaleString()}</> : null}</>}
                    </div>
                    {session!.strategyInfo && <div className="note" style={{ margin: '0 0 6px' }}><b>{session!.strategyInfo.name}</b>{session!.strategyInfo.parameters.length > 0 ? ' \u2014 ' + session!.strategyInfo.parameters.map(p => `${p.label} ${p.value}`).join(' \u00B7 ') : ''}</div>}
                    <div className="note" style={{ marginTop: 0 }}>
                        {savedLive
                            ? 'Live: resuming only restores the bot\'s record of this position; the order itself is on CoinSwitch. Check the CoinSwitch app that it still exists. If you closed it yourself, choose Discard.'
                            : 'Paper: resuming continues exactly where it stopped, with the same position and history.'}
                    </div>
                    {savedLive && <><label>Type <b>{PHRASE}</b> to resume live trading</label><input value={resumeConfirm} onChange={e => setResumeConfirm(e.target.value)} /></>}
                    <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
                        <button className="primary" style={{ flex: 1 }} disabled={busy || !hasKeys || (savedLive && resumeConfirm !== PHRASE)} onClick={() => run(() => api.traderResume(resumeConfirm || undefined))}>Resume</button>
                        <button className="ghost" disabled={busy} onClick={() => { if (window.confirm('Discard the saved session and forget this position? It does NOT close anything on CoinSwitch.')) run(() => api.traderDiscard()) }}>Discard</button>
                    </div>
                </div>
            )}

            {!running && (
                <div className="card">
                    <h3>Run this strategy on live candles</h3>
                    <div className="row">
                        <div><label>Mode</label>
                            <select value={mode} onChange={e => setMode(e.target.value as 'paper' | 'live')}>
                                <option value="paper">Paper (simulated)</option><option value="live">LIVE (real orders)</option></select></div>
                        <div><label>Capital ({quote})</label><input type="number" inputMode="decimal" value={capital} onChange={e => setCapital(+e.target.value)} /></div>
                    </div>
                    {mn && price ? (() => {
                        const notional = mn[0] * price, lv = mode === 'live' ? maxLev : costs.leverage, need = notional / capital
                        return <div className={need > Math.max(1, lv) ? 'warnbox' : 'note'}>
                            Minimum order on {data.symbol}: {mn[0]} (about {notional.toFixed(0)} USDT). With {capital} USDT the strategy must size {need.toFixed(1)}x capital to place one
                            {need > lv ? `, which is above your ${lv}x limit, so no order can ever be placed. Raise the capital.` : need > 1 ? ' (needs a script size above 1).' : '.'}
                        </div>
                    })() : null}
                    <div className="note">Uses the market, strategy and costs from the other tabs. It decides when a candle closes and shows the live price in between. An open position is saved to a file, so it is picked up again if the app restarts.</div>

                    {mode === 'live' && (<>
                        {!liveEnabled && <div className="warnbox">Live trading is switched off on the server. Set the environment variable <b>CS_LIVE_ENABLED=1</b> and restart.</div>}
                        <div className="warnbox"><b>Real money.</b> Live mode places real orders with your API key. It has not been tested against the live exchange by the author: start with an amount you can afford to lose and watch the first trades in the CoinSwitch app.</div>
                        <div className="row">
                            <div><label>Max single order ({quote})</label><input type="number" inputMode="decimal" value={maxOrder} onChange={e => setMaxOrder(+e.target.value)} /></div>
                            <div><label>Max position ({quote})</label><input type="number" inputMode="decimal" value={maxPos} onChange={e => setMaxPos(+e.target.value)} /></div>
                            <div><label>Max leverage</label><input type="number" inputMode="decimal" value={maxLev} onChange={e => setMaxLev(+e.target.value)} /></div>
                            <div><label>Daily loss limit %</label><input type="number" inputMode="decimal" value={dailyLoss} onChange={e => setDailyLoss(+e.target.value)} /></div>
                            <div><label>Price decimals</label><input type="number" inputMode="numeric" value={pricePrec} onChange={e => setPricePrec(+e.target.value)} /></div>
                            <div><label>Quantity decimals</label><input type="number" inputMode="numeric" value={qtyPrec} onChange={e => setQtyPrec(+e.target.value)} /></div>
                        </div>
                        <label>Type <b>{PHRASE}</b> to enable</label>
                        <input value={confirm} onChange={e => setConfirm(e.target.value)} autoCapitalize="characters" />
                        <label><input type="checkbox" checked={ungated} onChange={e => setUngated(e.target.checked)} /> Allow even though this strategy has NOT passed the live gate (not recommended)</label>
                    </>)}

                    {blockedBySaved && <div className="warnbox">Resume or discard the saved position above before starting a new trader.</div>}
                    {err && <div className="err">{err}</div>}
                    {!hasKeys && <div className="err">API keys are not connected. Open the menu (top right).</div>}
                    <button className="primary" style={{ marginTop: 10 }} disabled={busy || !hasKeys || blockedBySaved || (mode === 'live' && confirm !== PHRASE)} onClick={start}>
                        {busy ? 'Working...' : mode === 'live' ? 'START LIVE TRADING' : 'Start paper trading'}</button>
                </div>
            )}

            {running && (
                <div className="card">
                    <h3>Controls</h3>
                    <div style={{ display: 'grid', gap: 8 }}>
                        <button className="ghost" disabled={busy} onClick={() => run(() => api.traderStop())}>Stop trading (keep position on record)</button>
                        {side !== 'flat' && <button className="ghost" disabled={busy} onClick={() => { if (window.confirm('Close the open position now? The trader keeps running and may open a new position at its next signal.')) run(() => api.traderClose()) }}>Close position now</button>}
                        <button className="danger" disabled={busy} onClick={() => { if (st?.mode !== 'live' || window.confirm('Stop trading and close the tracked position now?')) run(() => api.traderKill()) }}>KILL SWITCH: stop + close position</button>
                        {st?.mode === 'live' && side !== 'flat' && <button className="ghost" disabled={busy} onClick={() => { if (window.confirm('Use this only if you ALREADY closed the position yourself on CoinSwitch. The bot will stop tracking it and send no orders.')) run(() => api.traderForget()) }}>I already closed it on CoinSwitch</button>}
                    </div>
                    {err && <div className="err">{err}</div>}
                </div>
            )}

            {st && st.state !== 'none' && (<>
                {st.config && <StrategyCard info={st.config} quote={quote} />}
                <div className="card">
                    <h3>Position &middot; {st.mode} &middot; {st.symbol}</h3>
                    {acct && side !== 'flat' ? (
                        <div>
                            <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap' }}>
                                <b className={side === 'long' ? 'pos' : 'neg'} style={{ fontSize: 18, textTransform: 'uppercase' }}>{side}</b>
                                <span style={{ fontFamily: 'var(--mono)' }}>{Number(acct.units).toFixed(6)} @ {f2(acct.entryPrice)}</span>
                            </div>
                            <div className="kpis" style={{ margin: '10px 0 0' }}>
                                <div className="kpi"><span>Unrealized P&amp;L</span><b className={Number(acct.unrealizedPnl) >= 0 ? 'pos' : 'neg'}>{Number(acct.unrealizedPnl) >= 0 ? '+' : ''}{Number(acct.unrealizedPnl).toFixed(2)}</b><small>{Number(acct.unrealizedPct) >= 0 ? '+' : ''}{Number(acct.unrealizedPct).toFixed(2)}% &middot; {quote}</small></div>
                                <div className="kpi"><span>Live price</span><b>{f2(st.lastPrice)}</b><small>{st.priceError ? 'price feed error' : st.priceAgeSec != null ? `updated ${Math.round(st.priceAgeSec)}s ago` : '...'}</small></div>
                            </div>
                        </div>
                    ) : (<div className="note" style={{ margin: 0 }}>Flat: no open position. Live price {f2(st.lastPrice)}.{st.priceError ? ` (price feed error: ${st.priceError})` : ''}</div>)}
                    <div className="row" style={{ marginTop: 10 }}>
                        <div>State: <b className={st.state === 'running' ? 'pos' : st.state === 'halted' || st.state === 'error' ? 'neg' : ''}>{st.state}</b></div>
                        <div>Strategy wants: <b>{st.targetWeight?.toFixed(2)}</b></div>
                        {st.nextDecision != null && st.running && <div style={{ gridColumn: '1 / span 2' }}>Next decision (candle close) in <b style={{ fontFamily: 'var(--mono)' }}>{countdown(st.nextDecision - now)}</b></div>}
                    </div>
                    {st.newestCandle != null && (() => {
                        const iv = st.config?.intervalMin ?? 5, behind = (now - (st.newestCandle + iv * 60)) / 60, late = behind > 2 * iv + 1
                        return <div className={late ? 'warnbox' : 'note'}>Newest candle from CoinSwitch closed {Math.max(0, behind).toFixed(0)} min ago.{late ? ` That is more than ${2 * iv} minutes late: CoinSwitch is sending ${iv}-minute candles late, so the strategy cannot react in time.` : ''}</div>
                    })()}
                    {acct && acct.lastOrderError && <div className="warnbox"><b>Last order problem</b> ({new Date(Number(acct.lastOrderErrorAt) * 1000).toLocaleString()})<br />{String(acct.lastOrderError)}</div>}
                    {st.lastError && st.running && <div className="warnbox">Trouble reaching CoinSwitch: {st.lastError}<br />Retrying automatically. Your position is kept and the trader keeps running.</div>}
                    <div className="note">The strategy acts only when a candle closes. Price, P&amp;L and equity follow the live market every few seconds.{st.running && st.lastCheckAgeSec != null ? ` Last candle check ${Math.round(st.lastCheckAgeSec)}s ago.` : ''} The trader runs on the server, so closing this page does not stop it.</div>
                </div>
                <div className="card">
                    <h3>Account</h3>
                    <div className="row">
                        {acct && <div>Equity: <b>{f2(acct.equity)}</b></div>}
                        <div>Total P&amp;L: <b className={pnl >= 0 ? 'pos' : 'neg'}>{pnl >= 0 ? '+' : ''}{f2(pnl)} {quote}</b></div>
                        {acct && acct.fees !== undefined && <div>Fees paid: <b>{Number(acct.fees).toFixed(2)}</b></div>}
                        {acct && acct.tdsWithheld !== undefined && <div>TDS withheld: <b>{Number(acct.tdsWithheld).toFixed(2)}</b></div>}
                    </div>
                    <label>Fills</label>
                    <div className="tablewrap"><table><thead><tr><th>Time</th><th>Side</th><th>Price</th><th>Qty</th></tr></thead>
                        <tbody>{(st.fills ?? []).slice().reverse().map((f, i) => (
                            <tr key={i}><td>{new Date(f.t * 1000).toLocaleString()}</td><td className={f.side === 'buy' ? 'pos' : 'neg'}>{f.side}</td><td>{f.price.toFixed(2)}</td><td>{f.qty.toFixed(6)}</td></tr>))}</tbody></table></div>
                    <label>Log</label>
                    {/* <div className="log">{(st.log ?? []).join('\n')}</div> */}
                    <div className="log">{(st.log ?? []).map((l, i) => (
                        <div key={i} style={/FAILED|NOT FILLED|BLOCKED|SKIPPED|HALTED|STOPPED|ERROR/.test(l) ? { color: '#ff8a87' } : undefined}>{formatLogLine(l)}</div>))}</div>
                </div>
            </>)}
        </div>
    )
}
