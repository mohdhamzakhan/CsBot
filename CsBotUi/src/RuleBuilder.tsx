import type { Cond, Operand, RuleSet } from './api'

const KINDS = ['close', 'open', 'high', 'low', 'volume', 'value', 'ema', 'sma', 'rsi', 'roc', 'highest', 'lowest', 'atr']
const PERIODIC = new Set(['ema', 'sma', 'rsi', 'roc', 'highest', 'lowest', 'atr'])
const OPS: [string, string][] = [['gt', '>'], ['lt', '<'], ['crossAbove', 'crosses above'], ['crossBelow', 'crosses below']]

export const defaultRules = (): RuleSet => ({
  entryLong: [
    { left: { kind: 'close' }, op: 'crossAbove', right: { kind: 'ema', period: 50 } },
    { left: { kind: 'rsi', period: 14 }, op: 'gt', right: { kind: 'value', value: 50 } },
  ],
  exitLong: [{ left: { kind: 'close' }, op: 'crossBelow', right: { kind: 'ema', period: 50 } }],
  entryShort: [], exitShort: [], stopLossPct: 8, takeProfitPct: 0, size: 1,
})

function OperandEditor({ o, onChange }: { o: Operand; onChange: (o: Operand) => void }) {
  return (<>
    <select value={o.kind} onChange={e => onChange({ kind: e.target.value, period: PERIODIC.has(e.target.value) ? (o.period ?? 14) : undefined, value: e.target.value === 'value' ? (o.value ?? 0) : undefined })}>
      {KINDS.map(k => <option key={k} value={k}>{k}</option>)}
    </select>
    {PERIODIC.has(o.kind) ? <input type="number" title="period" value={o.period ?? 14} onChange={e => onChange({ ...o, period: +e.target.value })} />
      : o.kind === 'value' ? <input type="number" title="value" step="any" value={o.value ?? 0} onChange={e => onChange({ ...o, value: +e.target.value })} />
      : <span />}
  </>)
}

function Section({ title, hint, conds, onChange }: { title: string; hint: string; conds: Cond[]; onChange: (c: Cond[]) => void }) {
  const upd = (i: number, c: Cond) => onChange(conds.map((x, j) => (j === i ? c : x)))
  return (
    <div style={{ marginTop: 10 }}>
      <label>{title} <span style={{ opacity: .7 }}>({hint})</span></label>
      {conds.map((c, i) => (
        <div className="cond" key={i}>
          <OperandEditor o={c.left} onChange={o => upd(i, { ...c, left: o })} />
          <select value={c.op} onChange={e => upd(i, { ...c, op: e.target.value })}>{OPS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select>
          <OperandEditor o={c.right} onChange={o => upd(i, { ...c, right: o })} />
          <button className="ghost" onClick={() => onChange(conds.filter((_, j) => j !== i))}>&times;</button>
        </div>
      ))}
      <button className="ghost" onClick={() => onChange([...conds, { left: { kind: 'close' }, op: 'gt', right: { kind: 'ema', period: 50 } }])}>+ condition</button>
    </div>
  )
}

export default function RuleBuilder({ rules, onChange, futures }: { rules: RuleSet; onChange: (r: RuleSet) => void; futures: boolean }) {
  const set = (p: Partial<RuleSet>) => onChange({ ...rules, ...p })
  return (
    <div>
      <Section title="Enter long when" hint="ALL must be true" conds={rules.entryLong} onChange={c => set({ entryLong: c })} />
      <Section title="Exit long when" hint="ANY is true" conds={rules.exitLong} onChange={c => set({ exitLong: c })} />
      {futures && <>
        <Section title="Enter short when" hint="ALL must be true" conds={rules.entryShort} onChange={c => set({ entryShort: c })} />
        <Section title="Exit short when" hint="ANY is true" conds={rules.exitShort} onChange={c => set({ exitShort: c })} />
      </>}
      <div className="row">
        <div><label>Stop-loss % (0 = off)</label><input type="number" step="any" value={rules.stopLossPct} onChange={e => set({ stopLossPct: +e.target.value })} /></div>
        <div><label>Take-profit % (0 = off)</label><input type="number" step="any" value={rules.takeProfitPct} onChange={e => set({ takeProfitPct: +e.target.value })} /></div>
      </div>
      <label>Position size (fraction of equity{futures ? ', up to your leverage cap' : ', max 1'})</label>
      <input type="number" step="0.05" value={rules.size} onChange={e => set({ size: +e.target.value })} />
      <div className="note">highest/lowest use the previous N bars (excluding the current one). Tip: every rule you tweak to look better in a backtest raises overfitting risk, so trust the walk-forward row more than the headline return.</div>
    </div>
  )
}
