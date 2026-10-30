import { useState } from 'react'
import type { ParamRange } from './api'

export type OptDef = { key: string; label: string; def: number; min: number; max: number; step: number }
type Sel = { on: boolean; from: number; to: number; step: number }
const r4 = (x: number) => Math.round(x * 10000) / 10000
const MAX = 400

/** Pick which settings to vary and over what range; shows how many combinations that makes. */
export default function OptimizePanel({ defs, current, busy, error, onRun }: {
  defs: OptDef[]; current: Record<string, number>; busy: boolean; error: string; onRun: (ranges: ParamRange[], trainPct: number) => void
}) {
  const [open, setOpen] = useState(false)
  const [sel, setSel] = useState<Record<string, Sel>>({})
  const [train, setTrain] = useState(0.7)

  const initial = (d: OptDef): Sel => {
    const c = current[d.key] ?? d.def
    const from = Math.max(d.min, Math.min(c * 0.5, c - d.step)), to = Math.min(d.max, Math.max(c * 1.5, c + d.step))
    const step = Math.max(d.step, Math.round(((to - from) / 6) / d.step) * d.step)
    return { on: true, from: r4(from), to: r4(to), step: r4(step) }
  }
  const toggle = (d: OptDef) => setSel(s => ({ ...s, [d.key]: s[d.key] ? { ...s[d.key], on: !s[d.key].on } : initial(d) }))
  const upd = (k: string, f: keyof Sel, v: number) => setSel(s => ({ ...s, [k]: { ...s[k], [f]: v } }))

  const active = defs.filter(d => sel[d.key]?.on)
  const count = (d: OptDef) => { const s = sel[d.key]; return s.step > 0 && s.to >= s.from ? Math.floor((s.to - s.from) / s.step + 1e-6) + 1 : 0 }
  const combos = active.reduce((a, d) => a * Math.max(1, count(d)), 1)
  const tooMany = combos > MAX, tooManyDims = active.length > 3
  const run = () => onRun(active.map(d => ({ key: d.key, from: sel[d.key].from, to: sel[d.key].to, step: sel[d.key].step })), train)

  if (defs.length === 0) return null
  return (
    <div className="card" style={{ marginTop: 12 }}>
      <button className="ghost" style={{ width: '100%', textAlign: 'left', display: 'flex', alignItems: 'center' }} onClick={() => setOpen(!open)}>
        <span style={{ flex: 1 }}>Find better settings</span><span>{open ? '\u25B4' : '\u25BE'}</span>
      </button>
      {open && (<>
        <div className="note">Tick the settings to vary (up to 3). The app tests every combination with your costs, ranks them on the <b>early part</b> of the history, then shows how each did on the <b>later part it never saw</b>. Trust a setting only if it works on both.</div>
        {defs.map(d => {
          const s = sel[d.key]
          return (
            <div key={d.key} style={{ borderTop: '1px solid var(--line)', paddingTop: 8, marginTop: 8 }}>
              <label style={{ display: 'flex', gap: 8, alignItems: 'center', margin: 0, color: 'var(--text)' }}>
                <input type="checkbox" checked={!!s?.on} onChange={() => toggle(d)} /> {d.label} <span style={{ color: 'var(--muted)' }}>(now {current[d.key] ?? d.def})</span>
              </label>
              {s?.on && (
                <div className="row" style={{ gridTemplateColumns: '1fr 1fr 1fr', marginTop: 6 }}>
                  <div><label>From</label><input type="number" inputMode="decimal" step="any" value={s.from} onChange={e => upd(d.key, 'from', +e.target.value)} /></div>
                  <div><label>To</label><input type="number" inputMode="decimal" step="any" value={s.to} onChange={e => upd(d.key, 'to', +e.target.value)} /></div>
                  <div><label>Step</label><input type="number" inputMode="decimal" step="any" value={s.step} onChange={e => upd(d.key, 'step', +e.target.value)} /></div>
                </div>)}
            </div>)
        })}
        <label>History used for ranking (the rest is the unseen test)</label>
        <select value={train} onChange={e => setTrain(+e.target.value)}><option value={0.6}>First 60%</option><option value={0.7}>First 70%</option><option value={0.8}>First 80%</option></select>
        <div className="note" style={{ color: tooMany || tooManyDims ? 'var(--down)' : undefined }}>
          {active.length === 0 ? 'Tick at least one setting.' : tooManyDims ? 'Vary at most 3 settings at once.' : tooMany ? `${combos} combinations is too many (limit ${MAX}). Use bigger steps or narrower ranges.` : `${combos} combinations will be tested.`}
        </div>
        {error && <div className="err">{error}</div>}
        <button className="primary" disabled={busy || active.length === 0 || tooMany || tooManyDims} onClick={run}>{busy ? 'Searching...' : 'Search settings'}</button>
      </>)}
    </div>
  )
}
