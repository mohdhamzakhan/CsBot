type Props = {
    onClose: () => void; onKeys: () => void; onLogout?: () => void; onAgents?: () => void
    health: { hasKeys: boolean; keyMatchesSecret: boolean; liveEnabled: boolean } | null
}

export default function MenuSheet({ onClose, onKeys, onLogout, onAgents, health }: Props) {
    const keysOk = !!health?.hasKeys && !!health?.keyMatchesSecret
    return (
        <div className="modal" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
            <div className="dialog">
                <div style={{ display: 'flex', alignItems: 'center', marginBottom: 12 }}>
                    <h2 style={{ margin: 0, fontSize: 18 }}>Menu</h2><div style={{ flex: 1 }} />
                    <button className="ghost" style={{ padding: '2px 12px' }} onClick={onClose} aria-label="Close">&times;</button>
                </div>

                {onAgents && <button className="menuitem" onClick={() => { onClose(); onAgents() }}>
                    <span className="badge ok">new</span>
                    <span className="grow"><b>Agents terminal</b><small>Run several strategies at once on paper money.</small></span>
                    <span>&rsaquo;</span>
                </button>}

                <button className="menuitem" onClick={() => { onClose(); onKeys() }}>
                    <span className={`badge ${keysOk ? 'ok' : 'bad'}`}>{keysOk ? 'connected' : !health?.hasKeys ? 'missing' : 'mismatch'}</span>
                    <span className="grow"><b>CoinSwitch API keys</b><small>{keysOk ? 'Keys are saved. Tap to change or delete.' : 'Tap to connect your keys (needed for charts and trading).'}</small></span>
                    <span>&rsaquo;</span>
                </button>

                <div className="menuitem" style={{ cursor: 'default' }}>
                    <span className={`badge ${health?.liveEnabled ? 'warn' : ''}`}>{health?.liveEnabled ? 'ON' : 'off'}</span>
                    <span className="grow"><b>Real-money orders</b><small>{health?.liveEnabled ? 'Enabled on this server. Live trading can place real orders.' : 'Switched off. Paper trading only. (Enabled on the server with CS_LIVE_ENABLED=1.)'}</small></span>
                </div>

                {onLogout && <button className="menuitem" onClick={() => { onClose(); onLogout() }}><span className="grow"><b>Log out</b><small>End this session on this device.</small></span></button>}

                <h3 style={{ margin: '14px 0 6px', fontSize: 11, textTransform: 'uppercase', letterSpacing: 1, color: 'var(--muted)' }}>About</h3>
                <div className="note" style={{ margin: 0 }}>
                    CsBot &middot; a personal strategy workbench for CoinSwitch PRO. Nothing here is financial advice and no strategy is guaranteed to make money.
                    <br /><br />
                    Charts powered by <a href="https://www.tradingview.com/lightweight-charts/" target="_blank" rel="noreferrer" style={{ color: 'var(--accent)' }}>TradingView Lightweight Charts&trade;</a>,
                    &copy; TradingView, Inc. (<a href="https://www.tradingview.com/" target="_blank" rel="noreferrer" style={{ color: 'var(--accent)' }}>tradingview.com</a>), used under the Apache 2.0 licence.
                </div>
            </div>
        </div>
    )
}
