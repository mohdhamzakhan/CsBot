import type { BacktestResp, StrategyDef } from './api'
import EquityChart from './EquityChart'

const n = (x: number | null, d = 1) => (x == null ? '-' : x.toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d }))
const cls = (x: number | null) => (x == null ? '' : x >= 0 ? 'pos' : 'neg')

type Note = { tone: 'good' | 'warn' | 'bad'; text: string }

/** Plain-English reasons for the result, built only from numbers the backtest already produced. */
function diagnose(r: BacktestResp, gross: number, feePct: number, slipPct: number, fundPct: number): Note[] {
  const m = r.metrics, b = r.benchmark, out: Note[] = []
  const net = m.returnPct ?? 0, f = (x: number) => `${x >= 0 ? '+' : ''}${x.toFixed(1)}%`
  const wfPos = r.walkForward.filter(x => x != null && x > 0).length
  if (m.trades < 6) out.push({ tone: 'warn', text: `Only ${m.trades} trades: too few to judge anything. Use more history or a faster timeframe.` })
  if (gross <= 0)
    out.push({ tone: 'bad', text: `Even BEFORE costs it lost (${f(gross)}). Costs are not the problem here: this idea has no edge on this market and period. Try a different strategy, timeframe or symbol.` })
  else if (net <= 0)
    out.push({ tone: 'bad', text: `Before costs it made ${f(gross)}, but fees and GST (${feePct.toFixed(1)}%), slippage (${slipPct.toFixed(1)}%)${fundPct > 0.05 ? ` and funding (${fundPct.toFixed(1)}%)` : ''} took it to ${f(net)}. The idea may work, but it trades too often for these costs. Use a longer timeframe, slower settings, or check your real fee with "Load my fee" in Costs.` })
  else if (b.returnPct != null && net < b.returnPct)
    out.push({ tone: 'warn', text: `It made ${f(net)} but simply buying and holding made ${f(b.returnPct)} in the same period. Trend strategies often lag in a steady rise; their value is a smaller drawdown (${(m.maxDrawdownPct ?? 0).toFixed(0)}% vs ${(b.maxDrawdownPct ?? 0).toFixed(0)}%).` })
  else out.push({ tone: 'good', text: `It made ${f(net)} after all costs${b.returnPct != null ? ` versus ${f(b.returnPct)} for buy & hold` : ''}.` })
  if (r.walkForward.length >= 5 && wfPos < 3) out.push({ tone: 'warn', text: `It was profitable in only ${wfPos} of ${r.walkForward.length} separate periods, so a good headline number may just be luck.` })
  if ((m.turnover ?? 0) > 60 && gross > 0 && net <= 0) out.push({ tone: 'warn', text: `It traded ${Math.round(m.turnover ?? 0)}x your capital. That level of activity is what costs punish most.` })
  return out
}

export default function Results({ r, def, quote }: { r: BacktestResp; def?: StrategyDef; quote: string }) {
  const m = r.metrics, b = r.benchmark
  const start = m.startEquity, fees = m.fees ?? 0, slip = m.slippage ?? 0, fund = m.funding ?? 0
  const gross = ((m.finalEquity ?? start) - start + fees + slip + fund) / start * 100
  const feePct = fees / start * 100, slipPct = slip / start * 100, fundPct = fund / start * 100, taxPct = (m.estTax ?? 0) / start * 100
  const notes = diagnose(r, gross, feePct, slipPct, fundPct)
  const sgn = (x: number) => (Math.abs(x) < 0.005 ? '0.00%' : `${x >= 0 ? '+' : '\u2212'}${Math.abs(x).toFixed(2)}%`)
  const bridge: [string, number, number, string][] = [
    ['Result before any costs', gross, gross / 100 * start, ''],
    ['Fees + GST', -feePct, -fees, 'neg'],
    ['Slippage', -slipPct, -slip, 'neg'],
    ...(fundPct > 0.005 ? [['Funding', -fundPct, -fund, 'neg'] as [string, number, number, string]] : []),
    ['After costs (before tax)', m.returnPct ?? 0, (m.finalEquity ?? start) - start, 'tot'],
    ['Estimated tax (30% of gains)', -taxPct, -(m.estTax ?? 0), 'neg'],
    ['After tax', m.postTaxReturnPct ?? 0, (m.postTaxReturnPct ?? 0) / 100 * start, 'tot'],
  ]
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
      <div className="card">
        <h3>Why this result</h3>
        {notes.map((n, i) => (
          <div key={i} style={{ display: 'flex', gap: 10, margin: '6px 0', alignItems: 'flex-start' }}>
            <span style={{ width: 9, height: 9, borderRadius: '50%', flex: 'none', marginTop: 6, background: n.tone === 'good' ? 'var(--up)' : n.tone === 'bad' ? 'var(--down)' : 'var(--amber)' }} />
            <span style={{ fontSize: 13.5 }}>{n.text}</span>
          </div>))}
        <h3 style={{ marginTop: 14 }}>Where the money went</h3>
        <div className="tablewrap"><table><tbody>{bridge.map(([k, pct, amt, kind]) => (
          <tr key={k} style={kind === 'tot' ? { fontWeight: 700 } : undefined}>
            <td>{k}</td><td className={kind === 'neg' ? 'neg' : pct >= 0 ? 'pos' : 'neg'}>{sgn(pct)}</td>
            <td style={{ color: 'var(--muted)' }}>{amt >= 0 ? '+' : '\u2212'}{Math.abs(amt).toLocaleString('en-IN', { maximumFractionDigits: 0 })} {quote}</td></tr>))}</tbody></table></div>
        <div className="note">Percentages are of your starting capital. "Before any costs" is what the idea earned on price moves alone; the rest is the price of trading it.</div>
      </div>
      <div className="kpis">
        <div className="kpi"><span>Post-tax return</span><b className={cls(m.postTaxReturnPct)}>{n(m.postTaxReturnPct)}%</b><small>buy &amp; hold {n(b.postTaxReturnPct)}% {edge != null && <span className={cls(edge)}>({edge >= 0 ? '+' : ''}{n(edge)})</span>}</small></div>
        <div className="kpi"><span>Sharpe ratio</span><b>{n(m.sharpe, 2)}</b><small>buy &amp; hold {n(b.sharpe, 2)}</small></div>
        <div className="kpi"><span>Max drawdown</span><b className="neg">{n(m.maxDrawdownPct)}%</b><small>buy &amp; hold {n(b.maxDrawdownPct)}%</small></div>
        <div className="kpi"><span>Trades</span><b>{m.trades}</b><small>fee drag {m.feeDragPct == null ? 'n/a' : `${n(m.feeDragPct, 0)}%`}</small></div>
      </div>
      <div className="card">
        <h3>Equity: strategy vs buy &amp; hold</h3>
        <EquityChart equity={r.equity} bench={r.benchEquity} />
      </div>
      <div className="card">
        <h3>Walk-forward check (5 periods, post-tax)</h3>
        <div className="wf">{r.walkForward.map((x, i) => (
          <div key={i} style={{ background: x == null ? '#1a2030' : x >= 0 ? '#26a69a22' : '#ef535022', color: x == null ? undefined : x >= 0 ? 'var(--up)' : 'var(--down)' }}>{x == null ? '-' : `${n(x)}%`}</div>))}</div>
        <div className="note">Same fixed settings in 5 consecutive periods, nothing re-optimised. If only one period carries the whole result, the edge is probably luck. Equity includes all costs, with the TDS refund counted.</div>
      </div>
      <div className="card">
        <h3>Details</h3>
        <div className="tablewrap"><table><thead><tr><th></th><th>{m.name}</th><th>Buy &amp; hold</th></tr></thead>
          <tbody>{rows.map(([k, a, c]) => <tr key={k}><td>{k}</td><td>{a}</td><td>{c}</td></tr>)}</tbody></table></div>
        {def && <div className="note" style={{ marginTop: 10 }}><b>Evidence:</b> {def.evidence}</div>}
        {m.busted && <div className="err">The account was wiped out in this simulation.</div>}
      </div>
    </>
  )
}
