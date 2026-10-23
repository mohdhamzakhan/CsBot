import { useEffect, useRef } from 'react'
import { createChart, IChartApi, ISeriesApi, UTCTimestamp } from 'lightweight-charts'
import type { Pt } from './api'
import { chartBase } from './ChartPanel'

export default function EquityChart({ equity, bench }: { equity: Pt[]; bench: Pt[] }) {
  const el = useRef<HTMLDivElement>(null)
  const chart = useRef<IChartApi | null>(null), a = useRef<ISeriesApi<'Area'> | null>(null), b = useRef<ISeriesApi<'Line'> | null>(null)
  useEffect(() => {
    const c = createChart(el.current!, chartBase)
    a.current = c.addAreaSeries({ lineColor: '#3b82f6', topColor: 'rgba(59,130,246,.35)', bottomColor: 'rgba(59,130,246,.02)', lineWidth: 2, title: 'Strategy' })
    b.current = c.addLineSeries({ color: '#7d8aa3', lineWidth: 1, title: 'Buy & hold' })
    chart.current = c
    return () => { c.remove(); chart.current = null }
  }, [])
  useEffect(() => {
    a.current?.setData(equity.map(p => ({ time: p.t as UTCTimestamp, value: p.v })))
    b.current?.setData(bench.map(p => ({ time: p.t as UTCTimestamp, value: p.v })))
    chart.current?.timeScale().fitContent()
  }, [equity, bench])
  return <div ref={el} style={{ height: 240 }} />
}
