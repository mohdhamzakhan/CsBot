import { useEffect, useRef } from 'react'
import { createChart, IChartApi, UTCTimestamp, LineStyle } from 'lightweight-charts'
import type { Curves } from './api'
import { chartBase } from './ChartPanel'

export const CURVE_COLORS = ['#3b82f6', '#22d3ee', '#f5b83d', '#bd10e0', '#e84393', '#b8e986', '#ff8a65', '#9ccc65']

/** Equity curves of several settings (and buy & hold) on one chart, with the unseen test period shaded. */
export default function CurveChart({ curves, splitTime, height = 260 }: { curves: Curves; splitTime?: number | null; height?: number }) {
  const el = useRef<HTMLDivElement>(null)
  const chart = useRef<IChartApi | null>(null)
  useEffect(() => {
    const c = createChart(el.current!, chartBase); chart.current = c
    return () => { c.remove(); chart.current = null }
  }, [])
  useEffect(() => {
    const c = chart.current; if (!c) return
    const t = curves.t as UTCTimestamp[]
    const shade = c.addAreaSeries({ priceScaleId: 'split', topColor: 'rgba(245,184,61,0.10)', bottomColor: 'rgba(245,184,61,0.10)', lineColor: 'rgba(0,0,0,0)', lineWidth: 1, crosshairMarkerVisible: false, lastValueVisible: false, priceLineVisible: false, autoscaleInfoProvider: () => ({ priceRange: { minValue: 0, maxValue: 1 } }) })
    c.priceScale('split').applyOptions({ scaleMargins: { top: 0, bottom: 0 }, visible: false })
    if (splitTime) shade.setData(t.map((x, i) => ({ x, i })).filter(p => p.x >= splitTime).map(p => ({ time: p.x, value: 1 })))
    const bench = c.addLineSeries({ color: '#8793ab', lineWidth: 1, lineStyle: LineStyle.Dashed, title: 'Buy & hold', priceLineVisible: false })
    bench.setData(curves.bench.map((v, i) => ({ time: t[i], value: v })))
    const lines = curves.curves.map((cv, k) => {
      const s = c.addLineSeries({ color: CURVE_COLORS[k % CURVE_COLORS.length], lineWidth: 2, priceLineVisible: false, lastValueVisible: true })
      s.setData(cv.equity.map((v, i) => ({ time: t[i], value: v }))); return s
    })
    const firstTest = splitTime ? t.find(x => x >= splitTime) : undefined
    if (firstTest && lines[0]) lines[0].setMarkers([{ time: firstTest, position: 'aboveBar', color: '#f5b83d', shape: 'circle', text: 'Unseen test starts' }])
    c.timeScale().fitContent()
    return () => { try { [shade, bench, ...lines].forEach(s => c.removeSeries(s)) } catch { /* chart already removed */ } }
  }, [curves, splitTime])
  return <div ref={el} style={{ height }} />
}
