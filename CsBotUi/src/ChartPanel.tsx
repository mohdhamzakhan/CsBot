import { useCallback, useEffect, useRef, useState } from 'react'
import { createChart, ColorType, CrosshairMode, IChartApi, ISeriesApi, UTCTimestamp, LineStyle } from 'lightweight-charts'
import type { Candle, FillPt, LiveTick, Overlay } from './api'
import type { IPriceLine } from 'lightweight-charts'
import { nextForming } from './liveCandle'
import type { Forming } from './liveCandle'

type Props = {
  candles: Candle[]; overlays: Overlay[]; fills: FillPt[]; intervalSec: number; live: LiveTick | null; liveError: string
  loading: boolean; error: string; title: string; subtitle: string; viewKey: string; onNewBar: () => void; onReload: () => void
  height: number; indHeight: number; splitTime?: number | null; levels?: { price: number; color: string; title: string }[]
}

export const chartBase = {
  autoSize: true,
  localization: { locale: 'en-GB' },   // explicit, so a device with an odd default language cannot break the chart's date formatting
  layout: { attributionLogo: false, background: { type: ColorType.Solid, color: '#0f141d' }, textColor: '#7d8aa3', fontFamily: 'Inter, -apple-system, Segoe UI, sans-serif', fontSize: 12 },
  grid: { vertLines: { color: '#171e2d' }, horzLines: { color: '#171e2d' } },
  rightPriceScale: { borderColor: '#222b3d', scaleMargins: { top: 0.08, bottom: 0.08 } },
  timeScale: { borderColor: '#222b3d', timeVisible: true, secondsVisible: false, rightOffset: 6, minBarSpacing: 0.05 },   // allow zooming right out to the whole history
  crosshair: { mode: CrosshairMode.Normal, vertLine: { color: '#4a5878', style: LineStyle.Dashed, labelBackgroundColor: '#2b3a5c' }, horzLine: { color: '#4a5878', style: LineStyle.Dashed, labelBackgroundColor: '#2b3a5c' } },
}
const precisionFor = (p: number) => (p >= 100000 ? 0 : p >= 1000 ? 2 : p >= 1 ? 4 : 6)
const fmt = (x: number, p: number) => x.toLocaleString(undefined, { minimumFractionDigits: p, maximumFractionDigits: p })

export default function ChartPanel(props: Props) {
  const { candles, overlays, fills, intervalSec, live, loading, error, title, subtitle, viewKey, splitTime, levels } = props
  const priceEl = useRef<HTMLDivElement>(null), indEl = useRef<HTMLDivElement>(null)
  const priceChart = useRef<IChartApi | null>(null), indChart = useRef<IChartApi | null>(null)
  const splitS = useRef<ISeriesApi<'Area'> | null>(null)
  const lastFills = useRef<unknown>(null)
  const lineRefs = useRef<IPriceLine[]>([])
  const candleS = useRef<ISeriesApi<'Candlestick'> | null>(null), volS = useRef<ISeriesApi<'Histogram'> | null>(null)
  const lineS = useRef<ISeriesApi<'Line'>[]>([]), indS = useRef<ISeriesApi<'Line'>[]>([])
  const forming = useRef<Forming | null>(null)
  const liveRef = useRef(live), candlesRef = useRef(candles), onNewBar = useRef(props.onNewBar), lastView = useRef('')
  liveRef.current = live; candlesRef.current = candles; onNewBar.current = props.onNewBar
  const [legend, setLegend] = useState<{ o: number; h: number; l: number; c: number } | null>(null)
  const hasInd = overlays.some(o => o.pane === 1)
  const prec = candles.length ? precisionFor(candles[candles.length - 1].c) : 2

  // ---- create charts once ----
  useEffect(() => {
    const pc = createChart(priceEl.current!, chartBase), ic = createChart(indEl.current!, chartBase)
    // translucent band over the period the settings search did NOT use for ranking (created first so it sits behind the candles)
    splitS.current = pc.addAreaSeries({ priceScaleId: 'split', topColor: 'rgba(245,184,61,0.10)', bottomColor: 'rgba(245,184,61,0.10)', lineColor: 'rgba(0,0,0,0)', lineWidth: 1, crosshairMarkerVisible: false, lastValueVisible: false, priceLineVisible: false, autoscaleInfoProvider: () => ({ priceRange: { minValue: 0, maxValue: 1 } }) })
    pc.priceScale('split').applyOptions({ scaleMargins: { top: 0, bottom: 0 }, visible: false })
    candleS.current = pc.addCandlestickSeries({ upColor: '#26a69a', downColor: '#ef5350', borderVisible: false, wickUpColor: '#26a69a', wickDownColor: '#ef5350' })
    volS.current = pc.addHistogramSeries({ priceFormat: { type: 'volume' }, priceScaleId: 'vol', lastValueVisible: false, priceLineVisible: false })
    pc.priceScale('vol').applyOptions({ scaleMargins: { top: 0.86, bottom: 0 } })
    priceChart.current = pc; indChart.current = ic
    if ((window as any).__csDebug) (window as any).__pc = pc    // test hook: lets automated checks read the chart's zoom

    pc.subscribeCrosshairMove(p => {
      const d = p.seriesData.get(candleS.current!) as { open: number; high: number; low: number; close: number } | undefined
      setLegend(d ? { o: d.open, h: d.high, l: d.low, c: d.close } : null)
    })
    let busy = false
    const sync = (src: IChartApi, dst: IChartApi) => src.timeScale().subscribeVisibleTimeRangeChange(r => {
      if (busy || !r) return
      busy = true; try { dst.timeScale().setVisibleRange(r) } catch { /* outside data */ } busy = false
    })
    sync(pc, ic); sync(ic, pc)
    return () => { pc.remove(); ic.remove(); priceChart.current = indChart.current = null }
  }, [])

  // ---- animate the forming candle from the live price ----
  const applyLive = useCallback(() => {
    const cs = candleS.current, lv = liveRef.current, cds = candlesRef.current
    if (!cs || !lv || cds.length === 0 || !intervalSec) return
    const last = cds[cds.length - 1], prev = forming.current
    const f = nextForming(last, prev, lv.price, lv.bar, intervalSec, Date.now() / 1000)
    forming.current = f
    try { cs.update({ time: f.t as UTCTimestamp, open: f.o, high: f.h, low: f.l, close: f.c }) } catch { /* older than series end */ }
    if (prev && prev.t !== f.t) onNewBar.current()                    // a new bar started: fetch the final values of the one that closed
  }, [intervalSec])

  useEffect(() => { applyLive() }, [live, applyLive])

  // ---- data ----
  useEffect(() => {
    const pc = priceChart.current, ic = indChart.current, cs = candleS.current, vs = volS.current
    if (!pc || !ic || !cs || !vs) return
    cs.applyOptions({ priceFormat: { type: 'price', precision: prec, minMove: 1 / 10 ** prec } })
    cs.setData(candles.map(c => ({ time: c.t as UTCTimestamp, open: c.o, high: c.h, low: c.l, close: c.c })))
    vs.setData(candles.map(c => ({ time: c.t as UTCTimestamp, value: c.v, color: c.c >= c.o ? 'rgba(38,166,154,.30)' : 'rgba(239,83,80,.30)' })))
    forming.current = null
    lineS.current.forEach(s => pc.removeSeries(s)); lineS.current = []
    indS.current.forEach(s => ic.removeSeries(s)); indS.current = []
    overlays.forEach(o => {
      const onInd = o.pane === 1
      const s = (onInd ? ic : pc).addLineSeries({ color: o.color, lineWidth: 2, title: o.name, priceLineVisible: false, lastValueVisible: true })
      s.setData(o.points.map(p => ({ time: p.t as UTCTimestamp, value: p.v })));
      (onInd ? indS : lineS).current.push(s)
    })
    splitS.current?.setData(splitTime ? candles.filter(c => c.t >= splitTime).map(c => ({ time: c.t as UTCTimestamp, value: 1 })) : [])
    const marks = fills.map(f => ({
      time: f.t as UTCTimestamp, position: f.side === 'buy' ? 'belowBar' as const : 'aboveBar' as const,
      color: f.side === 'buy' ? '#26a69a' : '#ef5350', shape: f.side === 'buy' ? 'arrowUp' as const : 'arrowDown' as const, text: '',
    }))
    const firstTest = splitTime ? candles.find(c => c.t >= splitTime) : undefined
    if (firstTest) marks.push({ time: firstTest.t as UTCTimestamp, position: 'aboveBar' as const, color: '#f5b83d', shape: 'circle' as any, text: 'Unseen test starts' })
    cs.setMarkers(marks.sort((a, b) => (a.time as number) - (b.time as number)))
    if (lastView.current !== viewKey && candles.length) {            // only re-zoom when the market/timeframe changed, not on silent refreshes
      lastView.current = viewKey
      const n = candles.length
      pc.timeScale().setVisibleLogicalRange({ from: Math.max(0, n - 140), to: n + 6 })
      ic.timeScale().fitContent()
    }
    if (splitTime && fills.length && lastFills.current !== fills) { lastFills.current = fills; pc.timeScale().fitContent() }   // a setting was just drawn: show the whole history so the test boundary is visible
    applyLive()
  }, [candles, overlays, fills, viewKey, prec, applyLive, splitTime])

  // dashed horizontal lines at the prices where the strategy will buy / exit / short
  useEffect(() => {
    const cs = candleS.current; if (!cs) return
    lineRefs.current.forEach(l => { try { cs.removePriceLine(l) } catch { /* already gone */ } }); lineRefs.current = []
    for (const l of levels ?? []) lineRefs.current.push(cs.createPriceLine({ price: l.price, color: l.color, lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: true, title: l.title }))
  }, [levels, candles.length > 0])

  const shown = legend ?? (candles.length ? (() => { const c = candles[candles.length - 1]; return { o: c.o, h: c.h, l: c.l, c: c.c } })() : null)
  const up = shown ? shown.c >= shown.o : true

  return (
    <div className="chartcard">
      <div className="charthead">
        <div className="title">{title}<small>{subtitle}</small></div>
        {shown && (
          <div className="legend">
            <span>O <b className={up ? 'pos' : 'neg'}>{fmt(shown.o, prec)}</b></span><span>H <b className={up ? 'pos' : 'neg'}>{fmt(shown.h, prec)}</b></span>
            <span>L <b className={up ? 'pos' : 'neg'}>{fmt(shown.l, prec)}</b></span><span>C <b className={up ? 'pos' : 'neg'}>{fmt(shown.c, prec)}</b></span>
          </div>)}
        <div style={{ flex: 1 }} />
        {splitTime ? <span className="badge warn" title="The shaded area is the later period the settings search did not use for ranking">shaded = unseen test</span> : null}
        <span className="badge" title={props.liveError || 'Price refreshes every 3 seconds'}>
          <span className={`dot ${live && !props.liveError ? '' : 'off'}`} /><span className="btxt">{live && !props.liveError ? 'LIVE' : 'offline'}</span>
        </span>
        <button className="ghost" style={{ padding: '4px 10px', minHeight: 32 }} onClick={props.onReload} aria-label="Reload chart">&#8635;</button>
      </div>
      <div className="chartbody">
        <div ref={priceEl} style={{ height: props.height }} />
        {loading && <div className="overlay"><div><div className="spinner" /><div>Loading candles...</div><div className="note">The first load of a long history can take a minute.</div></div></div>}
        {!loading && error && <div className="overlay"><div><div style={{ fontWeight: 700, marginBottom: 6 }}>Could not load the chart</div><div className="err">{error}</div><button className="ghost" onClick={props.onReload}>Try again</button></div></div>}
      </div>
      <div style={{ display: hasInd ? 'block' : 'none' }}><div className="panehead">Indicators</div></div>
      <div ref={indEl} style={{ height: hasInd ? props.indHeight : 2, overflow: 'hidden' }} />
    </div>
  )
}
