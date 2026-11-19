import type { SavedScript, ScriptTemplate, StrategyDef } from './api'

type Props = {
    members: string[]; onChange: (m: string[]) => void
    templates: ScriptTemplate[]; builtins: StrategyDef[]; saved: SavedScript[]; futures: boolean
}

/** Pick the strategies that will share one capital. The risk settings below it are the normal numeric settings of the "portfolio" strategy. */
export default function PortfolioBuilder({ members, onChange, templates, builtins, saved, futures }: Props) {
    const toggle = (k: string) => onChange(members.includes(k) ? members.filter(x => x !== k) : [...members, k])
    const chip = (k: string, label: string, title?: string) => (
        <button key={k} title={title} onClick={() => toggle(k)}
            style={{
                padding: '6px 10px', minHeight: 32, borderRadius: 8, cursor: 'pointer', fontSize: 12, fontWeight: 600,
                border: `1px solid ${members.includes(k) ? 'var(--accent)' : 'var(--line2)'}`, background: members.includes(k) ? '#1f2f55' : '#141b2b', color: members.includes(k) ? '#fff' : 'var(--muted)'
            }}>
            {label}
        </button>)
    const tooMany = members.length > 8

    return (
        <div className="card" style={{ marginTop: 10 }}>
            <h3>Strategies sharing the capital ({members.length} selected)</h3>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                {templates.filter(t => t.id !== 'blank').map(t => chip('t:' + t.id, t.name, t.description))}
                {builtins.map(b => chip('b:' + b.id, b.name, b.description))}
                {saved.map(s => chip('s:' + s.name, s.name))}
            </div>
            <div className="note" style={{ color: tooMany || members.length === 0 ? 'var(--down)' : undefined }}>
                {members.length === 0 ? 'Pick at least one strategy.' : tooMany ? 'At most 8 strategies.' : `Each gets an equal ${(100 / members.length).toFixed(0)}% share of the position budget.`}
            </div>
            <div className="note">
                <b style={{ color: 'var(--text)' }}>How the risk controls work.</b> The strategies are averaged into <b>one</b> position, so opposite signals cancel instead of fighting. The
                position can never exceed {futures ? 'your leverage cap' : '100% of capital'}. <b>Risk scale</b> multiplies the position (1 = if all agree it is fully invested; above 1 needs futures leverage).
                A strategy whose own simulated equity drops by the <b>bench</b> limit is switched off for the cooldown. If the portfolio's simulated drawdown reaches the <b>soft</b> limit the position is
                halved; at the <b>hard</b> limit it goes flat for the cooldown. All members use their default settings.
            </div>
            <div className="note">Run the backtest and check Max drawdown against buy &amp; hold, then run Monte Carlo in the Research tab before going live. Your account limits (max order, max position, daily loss, kill switch) still apply on top of these.</div>
        </div>
    )
}