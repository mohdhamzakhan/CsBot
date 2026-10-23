import type { BacktestResp, StrategyDef } from './api'
import EquityChart from './EquityChart'

const n = (x: number | null, d = 1) => (x == null ? '-' : x.toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d }))
const cls = (x: number | null) => (x == null ? '' : x >= 0 ? 'pos' : 'neg')

export default function Results({ r, def, quote }: { r: BacktestResp; def?: StrategyDef; quote: string }) {
  const m = r.metrics, b = r.benchmark
  const edge = m.postTaxReturnPct != null && b.postTaxReturnPct != null ? m.postTaxReturnPct - b.postTaxReturnPct : null
  const rows: [string, string, string][] = [
    ['Pre-tax return', `${n(m.returnPct)}%`, `${n(b.returnPct)}%`],
    ['CAGR', `${n(m.cagrPct)}%`, `${n(b.cagrPct)}%`],
    ['Fees + GST', `${n(m.fees, 0)} ${quote}`, `${n(b.fees, 0)} ${quote}`],
    ['Slippage cost', `${n(m.slippage, 0)} ${quote}`, `${n(b.slippage, 0)} ${quote}`],
    ['TDS withheld (refundable)', `${n(m.tdsWithheld, 0)} ${quote}`, `${n(b.tdsWithheld, 0)} ${quote}`],
    ['Funding paid', `${n(m.funding, 0)} ${quote}`, '-'],
    ['Turnover (x capital)', `${n(m.turnover)}x`, `${n(b.turnover)}x`],
    ['Fee drag (% of gross profit)', m.feeDragPct == null ? 'no gross profit' : `${n(m.feeDragPct, 0)}%`, '-'],
    ['Estimated tax (30%)', `${n(m.estTax, 0)} ${quote}`, `${n(b.estTax, 0)} ${quote}`],
  ]
  return (
    <>
      <div className={`gate ${r.gate.pass ? 'pass' : 'block'}`}>
        <div className="ico">{r.gate.pass ? '\u2713' : '!'}</div>
        <div><b>Live gate: {r.gate.pass ? 'PASSED' : 'BLOCKED'}</b> &mdash; {r.gate.reason}
          <div className="note" style={{ margin: '2px 0 0' }}>Needs a positive post-tax return, a Sharpe ratio above buy &amp; hold, and at least 60% of walk-forward periods profitable. Passing is a lead, not a guarantee.</div></div>
      </div>
      <div className="kpis">
        <div className="kpi"><span>Post-tax return</span><b className={cls(m.postTaxReturnPct)}>{n(m.postTaxReturnPct)}%</b><small>buy &amp; hold {n(b.postTaxReturnPct)}% {edge != null && <span className={cls(edge)}>({edge >= 0 ? '+' : ''}{n(edge)})</span>}</small></div>
        <div className="kpi"><span>Sharpe ratio</span><b>{n(m.sharpe, 2)}</b><small>buy &amp; hold {n(b.sharpe, 2)}</small></div>
        <div className="kpi"><span>Max drawdown</span><b className="neg">{n(m.maxDrawdownPct)}%</b><small>buy &amp; hold {n(b.maxDrawdownPct)}%</small></div>
        <div className="kpi"><span>Trades</span><b>{m.trades}</b><small>fee drag {m.feeDragPct == null ? 'n/a' : `${n(m.feeDragPct, 0)}%`}</small></div>
      </div>
      <div className="card">
        <h3>Equity curve &mdash; strategy vs buy &amp; hold (after all costs, TDS refund counted)</h3>
        <EquityChart equity={r.equity} bench={r.benchEquity} />
      </div>
      <div className="card">
        <h3>Walk-forward: post-tax return in 5 consecutive periods (fixed settings, nothing re-optimised)</h3>
        <div className="wf">{r.walkForward.map((x, i) => (
          <div key={i} style={{ background: x == null ? '#1a2030' : x >= 0 ? '#26a69a22' : '#ef535022', color: x == null ? undefined : x >= 0 ? 'var(--up)' : 'var(--down)' }}>{x == null ? '-' : `${n(x)}%`}</div>))}</div>
        <div className="note">If only one period carries the whole result, the edge is probably luck.</div>
      </div>
      <div className="card">
        <h3>Details</h3>
        <table><thead><tr><th></th><th>{m.name}</th><th>Buy &amp; hold</th></tr></thead>
          <tbody>{rows.map(([k, a, c]) => <tr key={k}><td>{k}</td><td>{a}</td><td>{c}</td></tr>)}</tbody></table>
        {def && <div className="note" style={{ marginTop: 10 }}><b>Evidence:</b> {def.evidence}</div>}
        {m.busted && <div className="err">The account was wiped out in this simulation.</div>}
      </div>
    </>
  )
}
