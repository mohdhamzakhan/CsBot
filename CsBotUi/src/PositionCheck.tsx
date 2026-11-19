import { useEffect, useRef, useState } from 'react'
import type { TraderStatus, Wallet } from './api'

type Diff = { bot: number; real: number; symbol: string }

/**
 * Compares the position the LIVE bot believes it holds with what CoinSwitch's wallet says.
 *   null      = they agree (or a spot wallet simply holds more than the bot, which is fine)
 *   undefined = cannot compare right now (paper mode, different market, futures symbol not on the chart, wallet error)
 */
function compare(trader: TraderStatus | null, wallet: Wallet | null, chartSymbol: string): Diff | null | undefined {
    const acct = trader?.account as Record<string, any> | undefined
    if (!trader || trader.mode !== 'live' || !acct || !wallet || trader.market !== wallet.market) return undefined
    const symbol = String(trader.symbol ?? '')
    const bot = Number(acct.units)                                  // signed: + long, - short
    if (!Number.isFinite(bot)) return undefined
    let real: number
    if (wallet.market === 'futures') {
        if (symbol.toUpperCase() !== chartSymbol.toUpperCase() || wallet.positionsError) return undefined   // futures positions are loaded for the chart's symbol only
        real = wallet.positions
            .filter(p => p.symbol.toUpperCase() === symbol.toUpperCase())
            .reduce((s, p) => s + (/short|sell/i.test(p.side) ? -p.size : p.size), 0)
    } else {
        const base = symbol.split('/')[0]
        real = wallet.holdings.find(h => h.currency === base)?.total ?? 0
        if (real >= bot * 0.98) return null                           // holding extra from before is fine; holding LESS than the bot thinks is not
    }
    return Math.abs(real - bot) <= Math.max(1e-9, Math.abs(bot) * 0.02) ? null : { bot, real, symbol }
}

/** Shows a red banner only when the difference persists for two consecutive wallet refreshes (about 40 s), so a fill in progress does not flash it. */
export default function PositionCheck({ trader, wallet, chartSymbol }: { trader: TraderStatus | null; wallet: Wallet | null; chartSymbol: string }) {
    const streak = useRef({ asOf: 0, n: 0 })
    const [diff, setDiff] = useState<Diff | null>(null)

    useEffect(() => {
        const d = compare(trader, wallet, chartSymbol)
        if (d === undefined || !wallet) { streak.current = { asOf: 0, n: 0 }; setDiff(null); return }
        if (wallet.asOf !== streak.current.asOf) streak.current = { asOf: wallet.asOf, n: d ? streak.current.n + 1 : 0 }
        setDiff(d && streak.current.n >= 2 ? d : null)
    }, [trader, wallet, chartSymbol])

    if (!diff) return null
    return (
        <div className="warnbox">
            <b>Position mismatch.</b> The bot tracks <b>{diff.bot.toFixed(6)}</b> {diff.symbol}, but CoinSwitch shows <b>{diff.real.toFixed(6)}</b>.
            Open the CoinSwitch app and check your orders and positions. If you closed it yourself, press &ldquo;I already closed it on CoinSwitch&rdquo; in the Paper / Live tab.
        </div>
    )
}