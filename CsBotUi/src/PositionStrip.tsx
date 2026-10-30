import type { SessionInfo, TraderStatus } from './api'

const f = (x: unknown, d = 2) => Number(x).toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d })

/** A one-glance line under the chart: what you hold right now and how it is doing. */
export default function PositionStrip({ trader, session, quote, onManage }: { trader: TraderStatus | null; session: SessionInfo | null; quote: string; onManage: () => void }) {
  const acct = trader?.account as Record<string, any> | undefined
  if (trader && trader.state !== 'none' && acct) {
    const side = String(acct.side)
    return (
      <div className="posstrip" onClick={onManage} style={{ cursor: 'pointer' }}>
        <span className={`side-pill ${side}`}>{trader.mode} &middot; {side}</span>
        {trader.strategyName && <span className="note" style={{ margin: 0, color: 'var(--text)' }}>{trader.strategyName}</span>}
        <div className="grow mono">
          {side === 'flat'
            ? <span style={{ color: 'var(--muted)' }}>No open position &middot; {trader.state}</span>
            : <>{Number(acct.units).toFixed(6)} @ {f(acct.entryPrice)}</>}
        </div>
        {side !== 'flat' && <b className={`mono ${Number(acct.unrealizedPnl) >= 0 ? 'pos' : 'neg'}`}>{Number(acct.unrealizedPnl) >= 0 ? '+' : ''}{f(acct.unrealizedPnl)} {quote} ({Number(acct.unrealizedPct) >= 0 ? '+' : ''}{f(acct.unrealizedPct)}%)</b>}
        <span style={{ color: 'var(--accent)', fontSize: 12 }}>Manage &rsaquo;</span>
      </div>
    )
  }
  if (session?.exists && session.hasPosition) {
    return (
      <div className="posstrip" onClick={onManage} style={{ cursor: 'pointer', borderColor: '#f5b83d88' }}>
        <span className={`side-pill ${session.side}`}>saved &middot; {session.side}</span>
        {session.strategyName && <span className="note" style={{ margin: 0, color: 'var(--text)' }}>{session.strategyName}</span>}
        <div className="grow mono">{Number(session.units).toFixed(6)} @ {f(session.entryPrice)} <span style={{ color: 'var(--amber)' }}>(not being managed)</span></div>
        <span style={{ color: 'var(--accent)', fontSize: 12 }}>Resume or discard &rsaquo;</span>
      </div>
    )
  }
  return null
}
