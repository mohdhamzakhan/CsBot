import { useEffect, useState } from 'react'
import { agentsApi } from './agentsApi'
import type { AgentsSnap } from './agentsApi'

const fmt = (x: number, d = 0) => x.toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d })

/** A single strip under the position strip: what the paper agents are doing right now. Hidden when there are none. */
export default function AgentsSummary({ onOpen }: { onOpen: () => void }) {
    const [snap, setSnap] = useState<AgentsSnap | null>(null)

    useEffect(() => {
        let alive = true
        const load = () => { if (!document.hidden) agentsApi.list().then(s => { if (alive) setSnap(s) }).catch(() => { }) }
        load(); const id = setInterval(load, 5000)
        return () => { alive = false; clearInterval(id) }
    }, [])

    if (!snap || snap.agents.length === 0) return null
    const run = snap.agents.filter(a => a.running)
    const n = (side: string) => run.filter(a => String(a.account?.side ?? 'flat') === side).length
    const c = snap.combined
    const money = (v: number) => (snap.quote === 'INR' ? `\u20B9${fmt(Math.abs(v))}` : `${fmt(Math.abs(v), 2)} ${snap.quote ?? 'USDT'}`)

    return (
        <div className="posstrip" onClick={onOpen} style={{ cursor: 'pointer' }}>
            <span className="side-pill flat">paper agents</span>
            <div className="grow mono">
                {run.length}/{snap.agents.length} running &middot; <span className="pos">{n('long')} long</span> &middot; <span className="neg">{n('short')} short</span> &middot; {n('flat')} flat
            </div>
            <b className={`mono ${c.pnl >= 0 ? 'pos' : 'neg'}`}>{c.pnl >= 0 ? '+' : '\u2212'}{money(c.pnl)}</b>
            <span style={{ color: 'var(--accent)', fontSize: 12 }}>Open terminal &rsaquo;</span>
        </div>
    )
}