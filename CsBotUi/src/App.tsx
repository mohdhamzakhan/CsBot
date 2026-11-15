import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api'
import type { BacktestResp, Candle, Costs, LiveTick, Market, MarketRef, OptResult, ParamRange, RuleSet, SavedScript, ScriptInput, ScriptTemplate, SessionInfo, StrategyDef, TraderStatus, Wallet } from './api'
import ChartPanel from './ChartPanel'
import Results from './Results'
import RuleBuilder, { defaultRules } from './RuleBuilder'
import TraderPanel from './TraderPanel'
import ScriptEditor from './ScriptEditor'
import { Header, Toolbar, Stats, TIMEFRAMES, DAYS_FOR } from './TopBar'
import KeysDialog from './KeysDialog'
import LoginScreen from './LoginScreen'
import MenuSheet from './MenuSheet'
import BottomNav from './BottomNav'
import type { NavKey } from './BottomNav'
import PositionStrip from './PositionStrip'
import WalletPanel from './WalletPanel'
import OptimizePanel from './OptimizePanel'
import type { OptDef } from './OptimizePanel'
import OptimizeResults from './OptimizeResults'
import ResearchPanel from './ResearchPanel'
import OptionsLab from './OptionsLab'
import TriggerCard, { act, actColor } from './TriggerCard'
import type { TriggerResult } from './api'
import type { Curves } from './api'

/** Your last choices (market, symbol, timeframe, strategy, settings, costs) are kept in this browser so the app opens where you left it. */
const STORE = 'csbot.state.v1'
function readStore(): any { try { return JSON.parse(localStorage.getItem(STORE) || '{}') || {} } catch { return {} } }
type LastView = { exchange: string; symbol: string; intervalMin: number; days: number }

const toCosts = (d: any): Costs => ({ fee: d.takerFeePct, gst: d.gstPct, slippage: d.slippagePct, tds: d.tdsPct, funding: d.fundingPer8hPct, leverage: d.maxLeverage })

/** Phone vs desktop, and a chart height that suits the screen. */
function useViewport() {
    const get = () => ({ w: window.innerWidth, h: window.innerHeight })
    const [v, setV] = useState(get)
    useEffect(() => {
        const f = () => setV(get())
        window.addEventListener('resize', f); window.addEventListener('orientationchange', f)
        return () => { window.removeEventListener('resize', f); window.removeEventListener('orientationchange', f) }
    }, [])
    const mobile = v.w <= 900
    return { mobile, chartH: mobile ? Math.round(Math.min(Math.max(v.h * 0.42, 260), 380)) : 480, indH: mobile ? 130 : 170 }
}

function Workspace({ onLogout }: { onLogout?: () => void }) {
    const vp = useViewport()
    const [saved0] = useState(readStore)                       // what was remembered from the last visit
    const last = useRef<Record<string, LastView>>(saved0.last ?? {})   // last symbol / timeframe used on each market
    const restoredParams = useRef(!!saved0.params && !!saved0.sid)
    const [side, setSide] = useState<'strategy' | 'costs' | 'trade' | 'wallet' | 'options' | 'research'>('strategy')
    const [view, setView] = useState<'chart' | 'panel'>('chart')   // phone only: which full screen is showing
    const [menuOpen, setMenuOpen] = useState(false)
    const [health, setHealth] = useState<{ hasKeys: boolean; keyMatchesSecret: boolean; liveEnabled: boolean } | null>(null)
    const [strategies, setStrategies] = useState<StrategyDef[]>([])
    const [defaults, setDefaults] = useState<{ spot: Costs; futures: Costs } | null>(null)
    const [data, setData] = useState<MarketRef>(saved0.data ?? { market: 'spot', exchange: 'coinswitchx', symbol: 'BTC/INR', intervalMin: 360, days: 730 })
    const [sid, setSid] = useState<string>(saved0.sid ?? 'script')
    const [pick, setPick] = useState<string>(saved0.pick ?? '')                      // what is chosen in the single strategy list
    const [templates, setTemplates] = useState<ScriptTemplate[]>([])
    const [saved, setSaved] = useState<SavedScript[]>([])
    const [code, setCode] = useState<string>(saved0.code ?? '')
    const [params, setParams] = useState<Record<string, number>>(saved0.params ?? {})
    const [rules, setRules] = useState<RuleSet>(defaultRules())
    const [costs, setCosts] = useState<Costs>(saved0.costs ?? { fee: 0.4, gst: 18, slippage: 0.05, tds: 1, funding: 0, leverage: 1 })
    const [startCash, setStartCash] = useState<number>(saved0.startCash ?? 100000)
    const [bt, setBt] = useState<BacktestResp | null>(null)
    const [chartCandles, setChartCandles] = useState<Candle[]>([])
    const [chartLoading, setChartLoading] = useState(false)
    const [chartErr, setChartErr] = useState('')
    const [live, setLive] = useState<LiveTick | null>(null)
    const [liveErr, setLiveErr] = useState('')
    const [trader, setTrader] = useState<TraderStatus | null>(null)
    const [session, setSession] = useState<SessionInfo | null>(null)
    const [wallet, setWallet] = useState<Wallet | null>(null)
    const [walletErr, setWalletErr] = useState('')
    const [walletLoading, setWalletLoading] = useState(false)
    const [busy, setBusy] = useState(false)
    const [err, setErr] = useState('')
    const [info, setInfo] = useState('')
    const reqId = useRef(0)
    const optRef = useRef<HTMLDivElement>(null)
    const [trig, setTrig] = useState<TriggerResult | null>(null)
    const [trigErr, setTrigErr] = useState('')
    const [trigBusy, setTrigBusy] = useState(false)
    const trigReq = useRef(0)
    const [scriptInputs, setScriptInputs] = useState<ScriptInput[]>([])
    const [opt, setOpt] = useState<OptResult | null>(null)
    const [optBusy, setOptBusy] = useState(false)
    const [optErr, setOptErr] = useState('')
    const myFee = useRef<number | null>(null)               // your real taker fee from CoinSwitch, once known
    const resultsRef = useRef<HTMLDivElement>(null)
    const [keysOpen, setKeysOpen] = useState(false)
    const askedKeys = useRef(false)

    useEffect(() => {
        api.health().then(h => { setHealth(h); if (!h.hasKeys && h.canEditKeys !== false && !askedKeys.current) { askedKeys.current = true; setKeysOpen(true) } }).catch(e => setErr(e.message))
        api.strategies().then(setStrategies).catch(e => setErr(e.message))
        api.scriptTemplates().then(setTemplates).catch(() => { })
        api.scriptSaved().then(setSaved).catch(() => { })
        api.costDefaults().then(d => { const x = { spot: toCosts(d.spot), futures: toCosts(d.futures) }; setDefaults(x); if (!saved0.costs) setCosts(x.spot) }).catch(() => { })
    }, [])

    const def = useMemo(() => strategies.find(s => s.id === sid), [strategies, sid])
    const builtins = useMemo(() => strategies.filter(s => s.id !== 'script' && s.id !== 'rules'), [strategies])
    const readyMade = useMemo(() => templates.filter(t => t.id !== 'blank'), [templates])

    // settings that can be searched: the built-in strategy's parameters, or the input() settings of the open script
    const optDefs: OptDef[] = useMemo(() => sid === 'script'
        ? scriptInputs.map(i => ({ key: i.name, label: i.label, def: i.default, min: i.min ?? Math.min(0, i.default), max: i.max ?? Math.max(i.default * 4, 1), step: i.step || 1 }))
        : sid === 'rules' ? [] : (def?.params ?? []).map(p => ({ key: p.key, label: p.label, def: p.default, min: p.min, max: p.max, step: p.step })), [sid, scriptInputs, def])
    useEffect(() => { setOpt(null); setOptErr('') }, [pick, sid, data.market, data.symbol, data.intervalMin, data.days])
    const runOptimize = async (ranges: ParamRange[], trainPct: number) => {
        setOptBusy(true); setOptErr('')
        try {
            setOpt(await api.optimize(data, strategyIn, costs, startCash, ranges, trainPct))
            if (vp.mobile) { setView('chart'); setTimeout(() => optRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 150) }
        } catch (e: any) { setOptErr(e.message) } finally { setOptBusy(false) }
    }

    // first run: start on the first ready-made script
    useEffect(() => { if (!pick && !code && templates.length) { setCode(templates[0].code); setPick('t:' + templates[0].id) } }, [templates, pick, code])

    /** One list for everything: built-in strategies, ready-made scripts, your saved scripts, and the build-your-own options. */
    const choose = (v: string) => {
        setPick(v)
        if (v.startsWith('b:')) { setSid(v.slice(2)); return }
        if (v === 'rules') { setSid('rules'); return }
        setSid('script'); setParams({})                          // new script = fresh settings (do not inherit another script's inputs)
        if (v.startsWith('t:')) setCode(templates.find(t => t.id === v.slice(2))?.code ?? '')
        else if (v.startsWith('s:')) setCode(saved.find(s => s.name === v.slice(2))?.code ?? '')
        else if (v === 'new') setCode(templates.find(t => t.id === 'blank')?.code ?? '')
    }
    const deleteSaved = async () => {
        const name = pick.slice(2)
        if (!window.confirm(`Delete saved script "${name}"?`)) return
        await api.scriptDelete(name).catch(() => { }); setSaved(await api.scriptSaved().catch(() => [])); choose(templates.length ? 't:' + templates[0].id : 'new')
    }
    useEffect(() => {
        if (!def) return
        if (restoredParams.current) { restoredParams.current = false; return }   // keep the settings remembered from the last visit once
        setParams(Object.fromEntries(def.params.map(p => [p.key, p.default])))
    }, [def])
    useEffect(() => {
        last.current[data.market] = { exchange: data.exchange, symbol: data.symbol, intervalMin: data.intervalMin, days: data.days }
        try { localStorage.setItem(STORE, JSON.stringify({ data, sid, pick, code, params, costs, startCash, last: last.current })) } catch { /* storage unavailable */ }
    }, [data, sid, pick, code, params, costs, startCash])

    const futures = data.market === 'futures'
    const quote = futures ? 'USDT' : data.symbol.split('/')[1] ?? 'INR'
    const tfLabel = TIMEFRAMES.find(t => t[0] === data.intervalMin)?.[1] ?? `${data.intervalMin}m`
    const strategyIn = useMemo(() => ({ id: sid, params, rules: sid === 'rules' ? rules : undefined, code: sid === 'script' ? code : undefined }), [sid, params, rules, code])

    // ---- candles load automatically whenever market / symbol / timeframe / history changes ----
    const dataKey = `${data.market}|${data.exchange}|${data.symbol}|${data.intervalMin}|${data.days}`
    const loadChart = useCallback(async (silent: boolean) => {
        const id = ++reqId.current
        if (!silent) { setChartLoading(true); setChartErr('') }
        try {
            const r = await api.candles(data)
            if (id !== reqId.current) return
            setChartCandles(r.candles); if (!silent) setBt(null)
        } catch (e: any) { if (!silent && id === reqId.current) setChartErr(e.message) }
        finally { if (!silent && id === reqId.current) setChartLoading(false) }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [dataKey])
    useEffect(() => { setLive(null); setLiveErr(''); const id = setTimeout(() => loadChart(false), 350); return () => clearTimeout(id) }, [dataKey, loadChart])

    // ---- where the strategy acts: the running strategy if there is one on this chart, otherwise the selected one ----
    const pickName = pick.startsWith('b:') ? builtins.find(s => s.id === pick.slice(2))?.name : pick.startsWith('t:') ? templates.find(t => t.id === pick.slice(2))?.name
        : pick.startsWith('s:') ? pick.slice(2) : pick === 'rules' ? 'Point-and-click rules' : 'Custom script'
    const tfName = (m: number) => (m >= 1440 ? `${m / 1440}D` : m >= 60 ? `${m / 60}h` : `${m}m`)
    // Follow the trader that is actually running (or still holding a position) on ITS OWN timeframe - even if the chart shows a different one.
    const trigSrc = useMemo(() => {
        const c = trader?.config, acct = trader?.account as Record<string, any> | undefined
        const side = acct ? String(acct.side) : 'flat'
        if (trader && c && (trader.running || side !== 'flat') && c.strategy.id !== 'rules')
            return {
                source: (trader.running ? 'running' : 'stopped') as 'running' | 'stopped', name: c.strategy.name, tf: tfName(c.intervalMin),
                market: c.market, symbol: c.symbol, quote: c.market === 'futures' ? 'USDT' : (c.symbol.split('/')[1] ?? 'INR'),
                data: { market: c.market as Market, exchange: c.exchange, symbol: c.symbol, intervalMin: c.intervalMin, days: 30 },
                strategy: { id: c.strategy.id, params: Object.fromEntries(c.strategy.parameters.map(p => [p.key, p.value])), code: c.strategy.code ?? undefined },
                holding: side !== 'flat' ? { side, entry: Number(acct!.entryPrice), units: Number(acct!.units) } : null,
            }
        return { source: 'selected' as const, name: pickName ?? 'strategy', tf: tfLabel, market: data.market, symbol: data.symbol, quote, data, strategy: strategyIn, holding: null }
    }, [trader, data, pickName, strategyIn, tfLabel, quote])
    const trigKey = JSON.stringify(trigSrc.strategy) + '|' + trigSrc.data.market + trigSrc.data.exchange + trigSrc.data.symbol + trigSrc.data.intervalMin + '|' + (trigSrc.source === 'selected' ? chartCandles.length : trader?.lastBar)
    const loadTriggers = useCallback(() => {
        const id = ++trigReq.current
        if (trigSrc.strategy.id === 'script' && !trigSrc.strategy.code) return
        if (trigSrc.source === 'selected' && chartCandles.length < 60) return
        setTrigBusy(true)
        api.triggers(trigSrc.data, trigSrc.strategy as any).then(r => { if (id === trigReq.current) { setTrig(r); setTrigErr('') } })
            .catch(e => { if (id === trigReq.current) { setTrigErr(e.message); setTrig(null) } }).finally(() => { if (id === trigReq.current) setTrigBusy(false) })
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [trigKey])
    useEffect(() => { const t = setTimeout(loadTriggers, 700); return () => clearTimeout(t) }, [loadTriggers])
    useEffect(() => { if (trigSrc.source === 'selected') return; const id = setInterval(() => { if (!document.hidden) loadTriggers() }, 60000); return () => clearInterval(id) }, [loadTriggers, trigSrc.source])
    const triggerLines = useMemo(() => {
        if (!trig || trigSrc.symbol !== data.symbol || trigSrc.market !== data.market) return []     // price lines only make sense on the chart of the same instrument
        const out: { price: number; color: string; title: string }[] = []
        for (const b of trig.boundaries) {
            const a = b.below === trig.current ? act(trig.current, b.above) : b.above === trig.current ? act(trig.current, b.below) : null
            out.push({ price: b.price, color: a ? actColor(a) : '#8793ab', title: a ? `${a.split(' ')[0]} ${b.below === trig.current ? 'above' : 'below'}` : 'trigger' })
        }
        if (trig.levels?.stop != null) out.push({ price: trig.levels.stop, color: '#ef5350', title: 'stop order' })
        if (trig.levels?.target != null) out.push({ price: trig.levels.target, color: '#26a69a', title: 'target order' })
        return out
    }, [trig, trigSrc.symbol, trigSrc.market, data.symbol, data.market])

    // ---- live price: poll every 3 seconds (paused while the tab is hidden) ----
    useEffect(() => {
        let alive = true, fails = 0
        const tick = async () => {
            if (document.hidden) return
            try { const t = await api.live(data); if (alive) { setLive(t); setLiveErr(''); fails = 0 } }
            catch (e: any) { fails++; if (alive && fails >= 2) { setLiveErr(e.message); setLive(null) } }
        }
        tick(); const id = setInterval(tick, 3000)
        return () => { alive = false; clearInterval(id) }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [data.market, data.exchange, data.symbol, data.intervalMin])

    // ---- trader + saved position: shared by the position strip and the Paper / Live tab ----
    const refreshTrader = useCallback(async () => {
        try { setTrader(await api.traderStatus()) } catch { /* ignore */ }
        try { setSession(await api.traderSession()) } catch { /* ignore */ }
    }, [])
    useEffect(() => { refreshTrader(); const id = setInterval(() => { if (!document.hidden) refreshTrader() }, 3000); return () => clearInterval(id) }, [refreshTrader])

    // ---- real wallet: refreshed every 20 seconds while the page is visible, and when you open the Wallet tab ----
    const loadWallet = useCallback(async (showSpinner: boolean) => {
        if (showSpinner) setWalletLoading(true)
        try { setWallet(await api.portfolio(data)); setWalletErr('') }
        catch (e: any) { setWalletErr(e.message); setWallet(null) }
        finally { if (showSpinner) setWalletLoading(false) }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [data.market, data.symbol])
    useEffect(() => { setWallet(null); loadWallet(false); const id = setInterval(() => { if (!document.hidden) loadWallet(false) }, 20000); return () => clearInterval(id) }, [loadWallet])

    const switchMarket = (m: Market) => {
        const l = last.current[m]                                // come back to the symbol and timeframe you last used on that market
        setData(d => l ? { ...d, market: m, ...l } : m === 'spot' ? { ...d, market: m, exchange: 'coinswitchx', symbol: 'BTC/INR' } : { ...d, market: m, exchange: 'EXCHANGE_2', symbol: 'BTCUSDT' })
        if (defaults) setCosts(m === 'spot' ? { ...defaults.spot, fee: myFee.current ?? defaults.spot.fee } : defaults.futures)
        setBt(null); setChartCandles([])
    }
    const setSymbol = (s: string) => { setData(d => ({ ...d, symbol: s })); setBt(null); setChartCandles([]) }
    const changeTimeframe = (m: number) => { setData(d => ({ ...d, intervalMin: m, days: DAYS_FOR[m] ?? d.days })); setBt(null); setChartCandles([]) }

    const nav = (k: NavKey) => {
        if (k === 'chart') { setView('chart'); return }
        setSide(k); setView('panel'); window.scrollTo({ top: 0 })
        if (k === 'wallet') loadWallet(true)
    }
    const run = async (refresh: boolean, override?: Record<string, number>, focus: 'results' | 'chart' = 'results') => {
        setBusy(true); setErr(''); setInfo(futures ? 'Fetching candles... futures history is rate-limited (30 requests/min), so a long range can take a minute or two.' : 'Running...')
        try {
            const st = override ? { ...strategyIn, params: { ...strategyIn.params, ...override } } : strategyIn
            const r = await api.backtest(data, st, costs, startCash, refresh); setChartCandles(r.candles); setBt(r)
            if (override) setParams(cur => ({ ...cur, ...override }))
            if (vp.mobile) { setView('chart'); setTimeout(() => (focus === 'chart' ? window.scrollTo({ top: 0, behavior: 'smooth' }) : resultsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })), 150) }
            else if (focus === 'chart') window.scrollTo({ top: 0, behavior: 'smooth' })
        } catch (e: any) { setErr(e.message) } finally { setBusy(false); setInfo('') }
    }
    const loadMyFee = async () => {
        try { const r = await api.myFee(data.exchange, data.symbol.split('/')[0]); if (r.found && r.takerPct != null) setCosts(c => ({ ...c, fee: r.takerPct! })); else setErr('Could not find your fee for that coin.') }
        catch (e: any) { setErr(e.message) }
    }
    const num = (k: keyof Costs) => (e: React.ChangeEvent<HTMLInputElement>) => setCosts(c => ({ ...c, [k]: +e.target.value }))
    // recent realised volatility (annualised, %) of the chart's candles: a sensible starting point for option prices
    const hv = useMemo(() => {
        const c = chartCandles.slice(-240).map(x => x.c); if (c.length < 30) return null
        const r = c.slice(1).map((v, i) => Math.log(v / c[i])), m = r.reduce((a, b) => a + b, 0) / r.length
        return Math.sqrt(r.reduce((a, b) => a + (b - m) * (b - m), 0) / (r.length - 1)) * Math.sqrt(365.25 * 1440 / data.intervalMin) * 100
    }, [chartCandles, data.intervalMin])
    const optBase = (['BTC', 'ETH', 'SOL'].find(b => data.symbol.toUpperCase().startsWith(b)) ?? 'BTC') as 'BTC' | 'ETH' | 'SOL'
    const keysOk = !!health?.hasKeys && !!health?.keyMatchesSecret
    // Use YOUR real spot fee instead of the conservative 0.4% default (otherwise every result looks about 10x more expensive than it is)
    useEffect(() => {
        if (!keysOk || myFee.current != null) return
        api.myFee('coinswitchx', 'BTC').then(r => {
            if (r.found && r.takerPct != null) { myFee.current = r.takerPct; setCosts(c => (data.market === 'spot' ? { ...c, fee: r.takerPct! } : c)) }
        }).catch(() => { })
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [keysOk])
    const tradeActive = !!trader?.running || !!session?.hasPosition

    return (
        <div className="app" data-view={view}>
            <Header data={data} live={live} liveError={liveErr} quote={quote} needsAttention={!!health && !keysOk} onMenu={() => setMenuOpen(true)} />
            {menuOpen && <MenuSheet health={health} onClose={() => setMenuOpen(false)} onKeys={() => setKeysOpen(true)} onLogout={onLogout} />}
            {keysOpen && <KeysDialog onClose={() => setKeysOpen(false)} onSaved={() => { api.health().then(setHealth).catch(() => { }); setChartErr(''); loadChart(false) }} />}
            <Toolbar data={data} onMarket={switchMarket} onSymbol={setSymbol} onInterval={changeTimeframe} />
            <Stats live={live} market={data.market} wallet={wallet} onWallet={() => nav('wallet')} />

            <div className="workspace" data-view={view} data-opt={side === 'options' || side === 'research' ? '1' : '0'}>
                <main>
                    <ChartPanel candles={chartCandles} overlays={bt?.overlays ?? []} fills={bt?.fills ?? []} splitTime={opt?.trainEnd ?? null} levels={triggerLines} intervalSec={data.intervalMin * 60} live={live} liveError={liveErr}
                        loading={chartLoading} error={chartErr} title={data.symbol} subtitle={`${tfLabel} · ${futures ? 'USDT perpetual' : 'spot'}`}
                        viewKey={dataKey} onNewBar={() => loadChart(true)} onReload={() => loadChart(false)} height={vp.chartH} indHeight={vp.indH} />
                    <PositionStrip trader={trader} session={session} quote={quote} onManage={() => nav('trade')} />
                    <TriggerCard res={trig} error={trigErr} busy={trigBusy} live={trigSrc.symbol === data.symbol && trigSrc.market === data.market ? live?.price ?? null : null} quote={trigSrc.quote} tfLabel={trigSrc.tf} source={trigSrc.source} name={trigSrc.name} holding={trigSrc.holding} onRefresh={loadTriggers} />
                    {opt && <div ref={optRef} style={{ scrollMarginTop: 70 }}><OptimizeResults res={opt} onShow={p => run(false, p, 'chart')} loadCurves={sets => api.curves(data, strategyIn, costs, startCash, sets) as Promise<Curves>} onClose={() => setOpt(null)} /></div>}
                    <div ref={resultsRef} style={{ scrollMarginTop: 70 }}>
                        {bt ? <Results r={bt} def={def} quote={quote} /> : (
                            <div className="card empty"><b>Ready.</b><br />Choose a strategy in the <b>Strategy</b> tab, then tap <b>Run backtest</b>.<br />Results include fees, GST, slippage and TDS and are compared with simply buying and holding.</div>)}
                    </div>
                </main>

                <aside className="side">
                    <div className="sidetabs">
                        <button className={side === 'strategy' ? 'on' : ''} onClick={() => setSide('strategy')}>Strategy</button>
                        <button className={side === 'costs' ? 'on' : ''} onClick={() => setSide('costs')}>Costs &amp; data</button>
                        <button className={side === 'trade' ? 'on' : ''} onClick={() => setSide('trade')}>Paper / Live{tradeActive ? ' \u25CF' : ''}</button>
                        <button className={side === 'wallet' ? 'on' : ''} onClick={() => { setSide('wallet'); loadWallet(true) }}>Wallet</button>
                        <button className={side === 'options' ? 'on' : ''} onClick={() => setSide('options')}>Options</button>
                        <button className={side === 'research' ? 'on' : ''} onClick={() => setSide('research')}>Research</button>
                    </div>
                    <div className="sidebody">
                        {side === 'strategy' && (<>
                            <h3>Strategy ({builtins.length + readyMade.length + saved.length + 2} to choose from)</h3>
                            <select value={pick} onChange={e => choose(e.target.value)}>
                                <optgroup label={`Built-in strategies (${builtins.length})`}>{builtins.map(s => <option key={s.id} value={'b:' + s.id}>{s.name}</option>)}</optgroup>
                                <optgroup label={`Ready-made scripts (${readyMade.length})`}>{readyMade.map(t => <option key={t.id} value={'t:' + t.id}>{t.name}</option>)}</optgroup>
                                {saved.length > 0 && <optgroup label={`My saved scripts (${saved.length})`}>{saved.map(s => <option key={s.name} value={'s:' + s.name}>{s.name}</option>)}</optgroup>}
                                <optgroup label="Build your own"><option value="new">Blank script (write code)</option><option value="rules">Point-and-click rules</option></optgroup>
                            </select>
                            {pick.startsWith('s:') && <button className="ghost" style={{ marginTop: 6, minHeight: 32, padding: '4px 12px' }} onClick={deleteSaved}>Delete this saved script</button>}
                            {sid !== 'script' && def && <div className="note">{def.description}</div>}
                            {sid !== 'rules' && sid !== 'script' && def?.params.map(p => (
                                <div key={p.key}><label>{p.label}</label>
                                    <input type="number" inputMode="decimal" min={p.min} max={p.max} step={p.step} value={params[p.key] ?? p.default} onChange={e => setParams({ ...params, [p.key]: +e.target.value })} /></div>))}
                            {sid === 'rules' && <RuleBuilder rules={rules} onChange={setRules} futures={futures} />}
                            {sid === 'script' && <ScriptEditor code={code} setCode={setCode} params={params} setParams={setParams} hidePickers onInputs={setScriptInputs}
                                onSavedChanged={name => { api.scriptSaved().then(setSaved).catch(() => { }); if (name) setPick('s:' + name) }} />}
                            {sid !== 'rules' && <OptimizePanel defs={optDefs} current={params} busy={optBusy} error={optErr} onRun={runOptimize} />}
                            <div className="note">Lengths are in candles: on {tfLabel} candles, 100 bars is about {Math.round(100 * data.intervalMin / 1440 * 10) / 10} days.</div>
                        </>)}

                        {side === 'costs' && (<>
                            <h3>Data</h3>
                            <div className="row">
                                <div><label>History for backtests (days)</label><input type="number" inputMode="numeric" value={data.days} onChange={e => setData({ ...data, days: +e.target.value })} /></div>
                                <div><label>Exchange</label><input value={data.exchange} disabled={futures} onChange={e => setData({ ...data, exchange: e.target.value })} /></div>
                            </div>
                            <div className="note">{futures ? 'Futures are USDT-margined perpetuals (symbol like BTCUSDT).' : 'coinswitchx = INR pairs, c2c1 / c2c2 = USDT pairs.'} Changing the timeframe, symbol or history reloads the chart automatically.</div>
                            <h3 style={{ marginTop: 16 }}>Costs (percent per side)</h3>
                            <div className="row">
                                <div><label>Taker fee %</label><input type="number" inputMode="decimal" step="any" value={costs.fee} onChange={num('fee')} /></div>
                                <div><label>GST on fee %</label><input type="number" inputMode="decimal" step="any" value={costs.gst} onChange={num('gst')} /></div>
                                <div><label>Slippage %</label><input type="number" inputMode="decimal" step="any" value={costs.slippage} onChange={num('slippage')} /></div>
                                <div><label>TDS % (on sells)</label><input type="number" inputMode="decimal" step="any" value={costs.tds} onChange={num('tds')} /></div>
                                {futures && <div><label>Funding % / 8h</label><input type="number" inputMode="decimal" step="any" value={costs.funding} onChange={num('funding')} /></div>}
                                {futures && <div><label>Max leverage</label><input type="number" inputMode="decimal" step="any" value={costs.leverage} onChange={num('leverage')} /></div>}
                                <div><label>Start capital ({quote})</label><input type="number" inputMode="decimal" value={startCash} onChange={e => setStartCash(+e.target.value)} /></div>
                            </div>
                            <div style={{ display: 'flex', gap: 6, marginTop: 10, flexWrap: 'wrap' }}>
                                {!futures && <button className="ghost" onClick={loadMyFee}>Load my fee from CoinSwitch</button>}
                                {futures && live?.fundingRate != null && <button className="ghost" onClick={() => setCosts(c => ({ ...c, funding: live.fundingRate! * 100 }))}>Use live funding ({(live.fundingRate * 100).toFixed(4)}%)</button>}
                            </div>
                            <div className="note">{futures ? 'Futures TDS defaults to 0: confirm how CoinSwitch applies it to derivatives, then enter it here.' : 'Default fee is the conservative worst case. Spot TDS is a refundable credit but locks cash on each sell.'}</div>
                        </>)}

                        {side === 'options' && <OptionsLab baseHint={optBase} hv={hv} loadPrice={base => api.live({ market: 'futures', exchange: 'EXCHANGE_2', symbol: base + 'USDT', intervalMin: 60, days: 1 }).then(t => t.price)} />}

                        {side === 'wallet' && <WalletPanel wallet={wallet} error={walletErr} loading={walletLoading} market={data.market} symbol={data.symbol} hasKeys={keysOk} onRefresh={() => loadWallet(true)} />}

                        {side === 'trade' && <TraderPanel data={data} strategy={strategyIn} costs={costs} liveEnabled={!!health?.liveEnabled} hasKeys={keysOk} quote={quote}
                            st={trader} session={session} onRefresh={refreshTrader} />}

                        {/*{side === 'research' && <ResearchPanel data={data} strategy={strategyIn} costs={costs} defs={optDefs} current={params}*/}
                        {/*    strategyName={pickName ?? 'strategy'} tfLabel={tfLabel} onApply={p => setParams(cur => ({ ...cur, ...p }))} />}*/}
                    </div>
                    {side !== 'trade' &&
                        side !== 'wallet' &&
                        side !== 'options' &&
                        side !== 'research' && (
                            <div className="sidefoot">
                                <div style={{ display: 'flex', gap: 8 }}>
                                    <button
                                        className="primary"
                                        style={{ flex: 1, width: 'auto', marginTop: 0 }}
                                        disabled={busy}
                                        onClick={() => run(false)}
                                    >
                                        {busy ? 'Working...' : 'Run backtest'}
                                    </button>

                                    <button
                                        className="ghost"
                                        title="Download the latest candles first, then run"
                                        disabled={busy}
                                        onClick={() => run(true)}
                                    >
                                        Refresh data
                                    </button>
                                </div>

                                {info && <div className="note">{info}</div>}
                                {err && <div className="err">{err}</div>}
                            </div>
                        )}
                    {side === 'research' && (
                        <ResearchPanel
                            data={data}
                            strategy={strategyIn}
                            costs={costs}
                            defs={optDefs}
                            current={params}
                            strategyName={def?.name ?? sid}
                            tfLabel={tfLabel}
                            onApply={setParams}
                        />
                    )}
                </aside>
            </div>

            <BottomNav active={view === 'chart' ? 'chart' : side} onNav={nav} tradeActive={tradeActive} />
        </div>
    )
}

export default function App() {
    const [auth, setAuth] = useState<{ required: boolean; authed: boolean } | null>(null)
    const check = useCallback(() => { api.authStatus().then(setAuth).catch(() => setAuth({ required: false, authed: true })) }, [])
    useEffect(() => {
        check(); window.addEventListener('csbot-login-required', check)
        return () => window.removeEventListener('csbot-login-required', check)
    }, [check])
    if (!auth) return null
    if (auth.required && !auth.authed) return <LoginScreen onDone={check} />
    return <Workspace onLogout={auth.required ? async () => { await api.logout().catch(() => { }); check() } : undefined} />
}
