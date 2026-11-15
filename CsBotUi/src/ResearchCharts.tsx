import { useMemo } from 'react'

export type Series = { values: number[]; color: string; width?: number; opacity?: number; dashed?: boolean }
type Props = {
  series: Series[]; band?: { lo: number[]; hi: number[]; color: string }; height?: number; baseline?: number
  vlines?: number[]; xFormat?: (i: number) => string; yFormat?: (v: number) => string
}

const nice = (x: number, round: boolean) => {
  const e = Math.floor(Math.log10(x)), f = x / Math.pow(10, e)
  const n = round ? (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) : (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10)
  return n * Math.pow(10, e)
}
const ticks = (lo: number, hi: number, n = 5) => {
  if (hi - lo < 1e-9) return [lo]
  const step = nice((hi - lo) / n, true), out: number[] = []
  for (let v = Math.ceil(lo / step) * step; v <= hi + step * 1e-6; v += step) out.push(Math.round(v / step) * step)
  return out
}
const short = (v: number) => { const a = Math.abs(v); return a >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : a >= 1e4 ? (v / 1e3).toFixed(0) + 'k' : a >= 1e3 ? (v / 1e3).toFixed(1) + 'k' : a >= 100 ? v.toFixed(0) : a >= 10 ? v.toFixed(1) : v.toFixed(2) }

/** Several lines (and an optional shaded band) on one chart. All series share the same x index. */
export function LineChart({ series, band, height = 260, baseline, vlines, xFormat, yFormat }: Props) {
  const W = 640, H = height, L = 52, R = 12, T = 10, B = 26
  const g = useMemo(() => {
    let n = 2
    for (const s of series) n = Math.max(n, s.values.length)
    if (band) n = Math.max(n, band.lo.length)
    let lo = Infinity, hi = -Infinity
    const see = (v: number) => { if (Number.isFinite(v)) { if (v < lo) lo = v; if (v > hi) hi = v } }
    series.forEach(s => s.values.forEach(see))
    if (band) { band.lo.forEach(see); band.hi.forEach(see) }
    if (baseline != null) see(baseline)
    if (!Number.isFinite(lo)) { lo = 0; hi = 1 }
    const pad = (hi - lo || 1) * 0.08; lo -= pad; hi += pad
    const X = (i: number) => L + (i / (n - 1)) * (W - L - R)
    const Y = (v: number) => T + (hi - v) / (hi - lo) * (H - T - B)
    const indices = (len: number) => {
      const st = Math.max(1, Math.ceil(len / 500)), out: number[] = []
      for (let i = 0; i < len; i += st) out.push(i)
      if (len > 0 && out[out.length - 1] !== len - 1) out.push(len - 1)
      return out
    }
    const path = (v: number[]) => indices(v.length).map((i, k) => `${k ? 'L' : 'M'}${X(i).toFixed(1)},${Y(v[i]).toFixed(1)}`).join('')
    let bandPath = ''
    if (band) {
      const idx = indices(band.hi.length)
      bandPath = idx.map((i, k) => `${k ? 'L' : 'M'}${X(i).toFixed(1)},${Y(band.hi[i]).toFixed(1)}`).join('') +
        [...idx].reverse().map(i => `L${X(i).toFixed(1)},${Y(band.lo[i]).toFixed(1)}`).join('') + 'Z'
    }
    const xt = Array.from({ length: 5 }, (_, k) => Math.round((k / 4) * (n - 1)))
    return { X, Y, path, bandPath, yt: ticks(lo, hi, 5), xt }
  }, [series, band, baseline, H])

  const yf = yFormat ?? short, xf = xFormat ?? ((i: number) => String(i))
  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: '100%', height: 'auto', display: 'block' }} role="img" aria-label="Chart">
      {g.yt.map(v => <g key={'y' + v}><line x1={L} x2={W - R} y1={g.Y(v)} y2={g.Y(v)} stroke="#1b2334" /><text x={L - 6} y={g.Y(v) + 4} textAnchor="end" fontSize="11" fill="#8793ab">{yf(v)}</text></g>)}
      {g.xt.map((i, k) => <text key={'x' + k} x={g.X(i)} y={H - 8} textAnchor={k === 0 ? 'start' : k === g.xt.length - 1 ? 'end' : 'middle'} fontSize="11" fill="#8793ab">{xf(i)}</text>)}
      {vlines?.map(i => <line key={'v' + i} x1={g.X(i)} x2={g.X(i)} y1={T} y2={H - B} stroke="#2c3750" strokeDasharray="3 4" />)}
      {baseline != null && <line x1={L} x2={W - R} y1={g.Y(baseline)} y2={g.Y(baseline)} stroke="#56627a" strokeDasharray="5 4" />}
      {band && <path d={g.bandPath} fill={band.color} />}
      {series.map((s, k) => <path key={k} d={g.path(s.values)} fill="none" stroke={s.color} strokeWidth={s.width ?? 1.6} opacity={s.opacity ?? 1} strokeDasharray={s.dashed ? '6 4' : undefined} />)}
    </svg>
  )
}
