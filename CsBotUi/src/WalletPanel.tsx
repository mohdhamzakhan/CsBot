import type { Market, Wallet } from './api'

const n = (x: number, d = 2) => x.toLocaleString('en-IN', { maximumFractionDigits: d, minimumFractionDigits: d })
const crypto = (x: number) => x.toLocaleString('en-IN', { maximumFractionDigits: 8 })
const cls = (x: number | null | undefined) => (x == null ? '' : x >= 0 ? 'pos' : 'neg')
const age = (t: number) => { const s = Math.max(0, Math.round(Date.now() / 1000 - t)); return s < 60 ? `${s}s ago` : `${Math.round(s / 60)} min ago` }

type Props = { wallet: Wallet | null; error: string; loading: boolean; market: Market; symbol: string; onRefresh: () => void; hasKeys: boolean }

/** Your real CoinSwitch balances and holdings (separate from the paper-trading account). */
export default function WalletPanel({ wallet, error, loading, market, symbol, onRefresh, hasKeys }: Props) {
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
        <h3 style={{ margin: 0, flex: 1 }}>{market === 'spot' ? 'Spot wallet' : 'Futures wallet'}</h3>
        {wallet && <span className="note" style={{ margin: 0 }}>updated {age(wallet.asOf)}</span>}
        <button className="ghost" style={{ padding: '4px 12px', minHeight: 34 }} disabled={loading} onClick={onRefresh}>{loading ? '...' : 'Refresh'}</button>
      </div>
      {!hasKeys && <div className="warnbox">Connect your CoinSwitch API keys (menu, top right) to see your balances. The key needs permission to read your account.</div>}
      {error && <div className="err">{error}</div>}

      {wallet && wallet.market === 'spot' && (<>
        <div className="kpis">
          <div className="kpi"><span>INR balance</span><b>&#8377;{n(wallet.inr.free)}</b><small>{wallet.inr.blocked > 0 ? `+ \u20B9${n(wallet.inr.blocked)} in open orders` : 'available'}</small></div>
          <div className="kpi"><span>USDT balance</span><b>{n(wallet.usdt.free)}</b><small>{wallet.usdt.blocked > 0 ? `+ ${n(wallet.usdt.blocked)} in open orders` : 'available'}</small></div>
          <div className="kpi"><span>Portfolio value</span><b>&#8377;{n(wallet.totalValueInr, 0)}</b><small>cash + holdings, in INR</small></div>
          <div className="kpi"><span>Profit / loss</span><b className={cls(wallet.pnlInr)}>{wallet.pnlInr >= 0 ? '+' : ''}&#8377;{n(wallet.pnlInr, 0)}</b><small>on &#8377;{n(wallet.investedInr, 0)} invested</small></div>
        </div>
        <div className="card">
          <h3>Holdings</h3>
          {wallet.holdings.length === 0 ? <div className="note">No crypto holdings.</div> : (
            <div className="tablewrap"><table>
              <thead><tr><th>Asset</th><th>Amount</th><th>Value (&#8377;)</th><th className="hide-sm">Avg buy</th><th>P&amp;L</th></tr></thead>
              <tbody>{wallet.holdings.map(h => (
                <tr key={h.currency}>
                  <td><b>{h.currency}</b>{h.blocked > 0 && <span className="note" style={{ margin: 0 }}> (in orders)</span>}</td>
                  <td>{crypto(h.total)}</td><td>{n(h.value)}</td><td className="hide-sm">{h.avgBuy > 0 ? n(h.avgBuy) : '-'}</td>
                  <td className={cls(h.pnl)}>{h.pnl == null ? '-' : `${h.pnl >= 0 ? '+' : ''}${n(h.pnl)} (${n(h.pnlPct ?? 0, 1)}%)`}</td>
                </tr>))}</tbody>
            </table></div>)}
          <div className="note">Values are CoinSwitch's own INR estimates. USDT is shown at its INR value in the portfolio total.</div>
        </div>
      </>)}

      {wallet && wallet.market === 'futures' && (<>
        <div className="kpis">
          <div className="kpi"><span>USDT balance</span><b>{n(wallet.usdt.total)}</b><small>total wallet</small></div>
          <div className="kpi"><span>Available</span><b>{n(wallet.usdt.available)}</b><small>for new positions</small></div>
          <div className="kpi"><span>In use</span><b>{n(wallet.usdt.blocked)}</b><small>margin {n(wallet.usdt.positionMargin)} &middot; orders {n(wallet.usdt.openOrderMargin)}</small></div>
          <div className="kpi"><span>Unrealised P&amp;L</span><b className={cls(wallet.unrealisedPnl)}>{wallet.unrealisedPnl >= 0 ? '+' : ''}{n(wallet.unrealisedPnl)}</b><small>{symbol}</small></div>
        </div>
        <div className="card">
          <h3>Open position &middot; {symbol}</h3>
          {wallet.positionsError && <div className="err">Could not read positions: {wallet.positionsError}</div>}
          {wallet.positions.length === 0 && !wallet.positionsError && <div className="note" style={{ margin: 0 }}>No open position on {symbol}. (CoinSwitch lists positions one symbol at a time; change the symbol at the top to check another.)</div>}
          {wallet.positions.map(p => (
            <div key={p.symbol + p.side} style={{ marginBottom: 10 }}>
              <div style={{ display: 'flex', gap: 10, alignItems: 'baseline', flexWrap: 'wrap' }}>
                <span className={`side-pill ${p.side}`}>{p.side}</span><b>{p.symbol}</b>
                <span style={{ fontFamily: 'var(--mono)' }}>{crypto(p.size)} @ {n(p.entry, 4)}</span>
                <span className="note" style={{ margin: 0 }}>{p.leverage}x {p.marginType.toLowerCase()}</span>
              </div>
              <div className="kpis" style={{ margin: '8px 0 0' }}>
                <div className="kpi"><span>P&amp;L</span><b className={cls(p.pnl)}>{p.pnl >= 0 ? '+' : ''}{n(p.pnl)}</b><small>mark {n(p.mark, 4)}</small></div>
                <div className="kpi"><span>Liquidation</span><b>{n(p.liquidation, 4)}</b><small>margin {n(p.margin)} USDT</small></div>
              </div>
            </div>))}
        </div>
      </>)}

      <div className="note">This is your real CoinSwitch account. The paper trader uses its own simulated money and never changes these balances.</div>
    </div>
  )
}
