export type NavKey = 'chart' | 'strategy' | 'costs' | 'trade' | 'wallet' | 'options'

const I = {
  chart: <path d="M4 19V5M4 19h16M8 15l3-4 3 3 5-7" />,
  strategy: <path d="M9 4h6l1 3H8zM6 7h12v13H6zM9 11h6M9 15h4" />,
  costs: <><circle cx="12" cy="12" r="3" /><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1" /></>,
  trade: <path d="M5 12l4 4L19 6M3 20h18" />,
  options: <path d="M3 18c4 0 5-12 9-12s5 12 9 12" />,
  wallet: <path d="M3 7h15a3 3 0 0 1 3 3v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM3 7V6a2 2 0 0 1 2-2h11M16 14h3" />,
}

/** Phone-only bottom tab bar: one tap to reach any section, nothing to scroll for. */
export default function BottomNav({ active, onNav, tradeActive }: { active: NavKey; onNav: (k: NavKey) => void; tradeActive: boolean }) {
  const items: [NavKey, string][] = [['chart', 'Chart'], ['strategy', 'Strategy'], ['costs', 'Settings'], ['trade', 'Trade'], ['wallet', 'Wallet'], ['options', 'Options']]
  return (
    <nav className="bnav">
      {items.map(([k, label]) => (
        <button key={k} className={active === k ? 'on' : ''} onClick={() => onNav(k)}>
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{I[k]}</svg>
          {label}
          {k === 'trade' && tradeActive && <span className="dotbadge" />}
        </button>))}
    </nav>
  )
}
