import { useMemo } from 'react'

type Props = { spot: number; xs: number[]; expiry: number[]; scenario?: number[] | null; scenarioLabel?: string; breakevens: number[] }

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
const short = (v: number) => { const a = Math.abs(v); return a >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : a >= 1e4 ? (v / 1e3).toFixed(0) + 'k' : a >= 1e3 ? (v / 1e3).toFixed(1) + 'k' : a >= 100 ? v.toFixed(0) : v.toFixed(2) }

/** Profit/loss against the underlying's price: at expiry (solid) and for a what-if scenario (dashed). */
const halo = { stroke: '#0f141d', strokeWidth: 4, paintOrder: 'stroke', strokeLinejoin: 'round' } as const

export default function PayoffChart({ spot, xs, expiry, scenario, scenarioLabel, breakevens }: Props) {
  const W = 640, H = 310, L = 54, R = 12, T = 24, B = 28
  const g = useMemo(() => {
    const x0 = xs[0], x1 = xs[xs.length - 1]
    const all = [...expiry, ...(scenario ?? []), 0]
    let lo = Math.min(...all), hi = Math.max(...all); const pad = (hi - lo || 1) * 0.08; lo -= pad; hi += pad
    const X = (v: number) => L + (v - x0) / (x1 - x0) * (W - L - R), Y = (v: number) => T + (hi - v) / (hi - lo) * (H - T - B)
    const path = (ys: number[]) => ys.map((y, i) => `${i ? 'L' : 'M'}${X(xs[i]).toFixed(1)},${Y(y).toFixed(1)}`).join('')
    const area = `${path(expiry)}L${X(x1).toFixed(1)},${Y(0).toFixed(1)}L${X(x0).toFixed(1)},${Y(0).toFixed(1)}Z`
    return { X, Y, path, area, xt: ticks(x0, x1, 5), yt: ticks(lo, hi, 5), y0: Y(0) }
  }, [xs, expiry, scenario])
  return (
    <div>
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: '100%', height: 'auto', display: 'block' }} role="img" aria-label="Profit and loss chart">
      <defs>
        <clipPath id="above"><rect x={L} y={T} width={W - L - R} height={Math.max(0, g.y0 - T)} /></clipPath>
        <clipPath id="below"><rect x={L} y={g.y0} width={W - L - R} height={Math.max(0, H - B - g.y0)} /></clipPath>
      </defs>
      {g.yt.map(v => <g key={'y' + v}><line x1={L} x2={W - R} y1={g.Y(v)} y2={g.Y(v)} stroke="#1b2334" /><text x={L - 6} y={g.Y(v) + 4} textAnchor="end" fontSize="11" fill="#8793ab">{short(v)}</text></g>)}
      {g.xt.map(v => <g key={'x' + v}><line x1={g.X(v)} x2={g.X(v)} y1={T} y2={H - B} stroke="#161d2c" /><text x={g.X(v)} y={H - 8} textAnchor="middle" fontSize="11" fill="#8793ab">{short(v)}</text></g>)}
      <path d={g.area} fill="rgba(38,166,154,0.22)" clipPath="url(#above)" />
      <path d={g.area} fill="rgba(239,83,80,0.22)" clipPath="url(#below)" />
      <line x1={L} x2={W - R} y1={g.y0} y2={g.y0} stroke="#56627a" />
      {spot >= xs[0] && spot <= xs[xs.length - 1] && <><line x1={g.X(spot)} x2={g.X(spot)} y1={T} y2={H - B} stroke="#f5b83d" strokeDasharray="4 4" /><text x={g.X(spot)} y={T - 8} textAnchor="middle" fontSize="11" fill="#f5b83d" {...halo}>now {short(spot)}</text></>}
      {scenario && <path d={g.path(scenario)} fill="none" stroke="#f5b83d" strokeWidth="2" strokeDasharray="6 4" />}
      <path d={g.path(expiry)} fill="none" stroke="#e7ebf3" strokeWidth="2.2" />
      {breakevens.filter(b => b >= xs[0] && b <= xs[xs.length - 1]).map(b => <g key={b}><circle cx={g.X(b)} cy={g.y0} r="4.5" fill="#0f141d" stroke="#e7ebf3" strokeWidth="2" /><text x={g.X(b)} y={g.y0 + 18} textAnchor="middle" fontSize="11" fill="#e7ebf3" {...halo}>{short(b)}</text></g>)}
    </svg>
    <div style={{ display: 'flex', gap: '4px 16px', flexWrap: 'wrap', fontSize: 12, color: 'var(--muted)', marginTop: 4 }}>
      <span><span style={{ display: 'inline-block', width: 18, borderTop: '2px solid #e7ebf3', verticalAlign: 'middle', marginRight: 6 }} />at expiry</span>
      {scenario && <span><span style={{ display: 'inline-block', width: 18, borderTop: '2px dashed #f5b83d', verticalAlign: 'middle', marginRight: 6 }} />{scenarioLabel ?? 'scenario'}</span>}
      <span><span style={{ display: 'inline-block', width: 9, height: 9, borderRadius: '50%', border: '2px solid #e7ebf3', verticalAlign: 'middle', marginRight: 6 }} />break-even</span>
    </div>
    </div>
  )
}
