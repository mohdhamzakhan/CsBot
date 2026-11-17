import { useEffect, useRef } from 'react'
import { createChart, ColorType, CrosshairMode, LineStyle } from 'lightweight-charts'
import type { IChartApi, ISeriesApi, SeriesMarker, Time, UTCTimestamp } from 'lightweight-charts'
import { api } from './api'
import type { Candle, LiveTick, MarketRef } from './api'
import { nextForming } from './liveCandle'
import type { Forming } from './liveCandle'
import { DAYS_FOR } from './TopBar'

export type Mark = { t: number; side: string; name: string; color: string }
type Props = { data: MarketRef; marks: Mark[]; height: number; onLive?: (t: LiveTick | null) => void }

const precisionFor = (p: number) => (p >= 100000 ? 0 : p >= 1000 ? 2 : p >= 1 ? 4 : 6)

/** Candles for one agent's market, the live forming candle, and an arrow for every trade the agents made on it. */
export default function AgentChart({ data, marks, height, onLive }: Props) {
    const el = useRef<HTMLDivElement>(null)
    const chart = useRef<IChartApi | null>(null)
    const series = useRef<ISeriesApi<'Candlestick'> | null>(null)
    const candles = useRef<Candle[]>([])
    const forming = useRef<Forming | null>(null)
    const req = useRef(0)
    const live = useRef<LiveTick | null>(null)
    const marksRef = useRef(marks)
    marksRef.current = marks
    const key = `${data.market}|${data.exchange}|${data.symbol}|${data.intervalMin}`
    const onLiveRef = useRef(onLive); onLiveRef.current = onLive

    const applyMarks = () => {
        const s = series.current, cs = candles.current
        if (!s) return
        const iv = data.intervalMin * 60, first = cs.length ? cs[0].t : 0
        const m: SeriesMarker<Time>[] = marksRef.current
            .filter(x => x.t >= first)
            .map(x => ({
                time: (Math.floor(x.t / iv) * iv) as UTCTimestamp, position: x.side === 'buy' ? 'belowBar' as const : 'aboveBar' as const,
                color: x.color, shape: x.side === 'buy' ? 'arrowUp' as const : 'arrowDown' as const, text: x.name.slice(0, 4),
            }))
            .sort((a, b) => (a.time as number) - (b.time as number))
        s.setMarkers(m)
    }

    const loadCandles = async (resetView: boolean) => {
        const id = ++req.current
        try {
            const r = await api.candles({ ...data, days: Math.min(DAYS_FOR[data.intervalMin] ?? 60, 120) })
            if (id !== req.current || !series.current) return
            candles.current = r.candles
            const last = r.candles[r.candles.length - 1]
            const prec = last ? precisionFor(last.c) : 2
            series.current.applyOptions({ priceFormat: { type: 'price', precision: prec, minMove: 1 / 10 ** prec } })
            series.current.setData(r.candles.map(c => ({ time: c.t as UTCTimestamp, open: c.o, high: c.h, low: c.l, close: c.c })))
            forming.current = null
            if (resetView && chart.current) { const n = r.candles.length; chart.current.timeScale().setVisibleLogicalRange({ from: Math.max(0, n - 110), to: n + 6 }) }
            applyMarks()
        } catch { /* the page shows its own errors; the chart just stays as it was */ }
    }

    useEffect(() => {
        const c = createChart(el.current!, {
            autoSize: true, localization: { locale: 'en-GB' },
            layout: { attributionLogo: false, background: { type: ColorType.Solid, color: '#ffffff' }, textColor: '#6b7488', fontFamily: 'ui-monospace, "SF Mono", Menlo, Consolas, monospace', fontSize: 11 },
            grid: { vertLines: { color: '#eef1f7' }, horzLines: { color: '#eef1f7' } },
            rightPriceScale: { borderColor: '#dfe4ef', scaleMargins: { top: 0.08, bottom: 0.08 } },
            timeScale: { borderColor: '#dfe4ef', timeVisible: true, secondsVisible: false, rightOffset: 5, minBarSpacing: 0.05 },
            crosshair: { mode: CrosshairMode.Normal, vertLine: { color: '#9aa5bd', style: LineStyle.Dashed, labelBackgroundColor: '#2d6bff' }, horzLine: { color: '#9aa5bd', style: LineStyle.Dashed, labelBackgroundColor: '#2d6bff' } },
        })
        chart.current = c
        series.current = c.addCandlestickSeries({ upColor: '#1b9a7f', downColor: '#d6455d', borderVisible: false, wickUpColor: '#1b9a7f', wickDownColor: '#d6455d' })
        return () => { c.remove(); chart.current = null; series.current = null }
    }, [])

    // history: reload whenever the market or timeframe changes
    useEffect(() => { candles.current = []; forming.current = null; live.current = null; onLiveRef.current?.(null); loadCandles(true) /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [key])

    // live price every 3 seconds (paused while the tab is hidden); the newest candle follows it
    useEffect(() => {
        let alive = true
        const tick = async () => {
            if (document.hidden) return
            try {
                const lv = await api.live(data)
                if (!alive) return
                live.current = lv; onLiveRef.current?.(lv)
                const cs = candles.current, s = series.current
                if (!s || cs.length === 0) return
                const prev = forming.current
                const f = nextForming(cs[cs.length - 1], prev, lv.price, lv.bar, data.intervalMin * 60, Date.now() / 1000)
                forming.current = f
                try { s.update({ time: f.t as UTCTimestamp, open: f.o, high: f.h, low: f.l, close: f.c }) } catch { /* older than the newest bar */ }
                if (prev && prev.t !== f.t) loadCandles(false)            // a bar just closed: fetch its final values
            } catch { if (alive) onLiveRef.current?.(null) }
        }
        tick(); const id = setInterval(tick, 3000)
        return () => { alive = false; clearInterval(id) }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [key])

    // arrows for agent trades
    useEffect(() => { applyMarks() /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [marks])

    return <div ref={el} style={{ height }} />
}