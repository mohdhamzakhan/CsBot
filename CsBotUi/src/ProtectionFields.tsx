export const PROTECT_KEYS = ['stopPct', 'takePct', 'trailPct', 'guardPct', 'exchStops'] as const
export type Protect = Partial<Record<(typeof PROTECT_KEYS)[number], number>>

/** Keeps only the protection settings that are switched on (so an empty box means "off" and is not sent). */
export const activeProtect = (p: Protect): Record<string, number> => {
    const out: Record<string, number> = {}
    for (const k of PROTECT_KEYS) if ((p[k] ?? 0) > 0) out[k] = p[k] as number
    return out
}

type Props = {
    values: Protect; onChange: (next: Protect) => void
    lev: number | null                    // the leverage you chose (size of a fully-invested position as a multiple of capital); null = not chosen yet
    variant: 'astra' | 'bench'
    showExchange?: boolean                // live trading only: rest the stop and take profit on CoinSwitch
}

/** All limits are in % OF YOUR CAPITAL, because with leverage that is what you actually lose or gain. The box shows what each one means as a price move. */
export default function ProtectionFields({ values, onChange, lev, variant, showExchange }: Props) {
    const two = variant === 'astra' ? 'a-two' : 'row', note = variant === 'astra' ? 'a-note' : 'note'
    const L = lev && lev > 0 ? lev : 1
    const set = (k: keyof Protect, v: string) => onChange({ ...values, [k]: v === '' ? 0 : Math.max(0, +v) })
    const shown = (k: keyof Protect) => (values[k] ? values[k] : '') as number | ''
    const px = (pct?: number) => (pct && pct > 0 ? `${(pct / L).toFixed(2)}%` : '')
    const field = (k: keyof Protect, label: string, hint: string) => (
        <div>
            <label>{label}</label>
            <input type="number" inputMode="decimal" min={0} step="any" placeholder="off" value={shown(k)} onChange={e => set(k, e.target.value)} />
            {values[k] ? <div className={note} style={{ margin: '2px 0 0' }}>{hint}</div> : null}
        </div>)
    const noStop = L >= 3 && !(values.stopPct ?? 0) && !(values.guardPct ?? 0)

    return (
        <div style={{ gridColumn: '1 / -1' }}>
            <label style={{ marginTop: 14 }}>Protection &mdash; stop loss and take profit (in % of your capital)</label>
            <div className={two}>
                {field('stopPct', 'Stop loss (capital lost)', `Exits when the trade has lost ${values.stopPct}% of capital = a ${px(values.stopPct)} price move at ${L}x.`)}
                {field('takePct', 'Take profit (capital gained)', `Exits when the trade has gained ${values.takePct}% of capital = a ${px(values.takePct)} price move at ${L}x.`)}
                {field('trailPct', 'Trailing stop (from the best)', `Exits when it falls ${values.trailPct}% of capital from its best point = ${px(values.trailPct)} of price.`)}
                {field('guardPct', 'Liquidation guard (% of the way)', `Exits when price has covered ${values.guardPct}% of the way to liquidation (about ${(100 / L).toFixed(1)}% away at ${L}x), i.e. at ${px(values.guardPct)}.`)}
            </div>
            {noStop && (
                <div className={note} style={{ color: 'var(--down)' }}>
                    No stop loss or liquidation guard is set. At {L}x a {(100 / L).toFixed(1)}% move against the position wipes out the capital.
                </div>)}
            {showExchange && (
                <label style={{ display: 'flex', gap: 8, alignItems: 'center', textTransform: 'none', letterSpacing: 0, margin: '10px 0 0', color: 'inherit' }}>
                    <input type="checkbox" style={{ width: 'auto', minHeight: 0 }} checked={(values.exchStops ?? 0) > 0} onChange={e => onChange({ ...values, exchStops: e.target.checked ? 1 : 0 })} />
                    Place the stop loss and take profit as real orders on CoinSwitch (futures, live)
                </label>)}
            <div className={note}>
                {showExchange
                    ? 'Without the tick above, stops are only checked when a candle closes, so in a fast move the exit can be far past the level. With it, the orders rest on the exchange and act at once; if one cannot be placed the bot closes the position instead of leaving it unprotected. This part has not been validated against the live exchange: test with the smallest size and watch the orders in the CoinSwitch app. The trailing stop is always candle-based.'
                    : 'In backtests and paper trading the limits are tested against each candle\u2019s high and low and fill at the level. In live trading they act on candle closes unless you tick the exchange-orders option in the Paper / Live settings. Leave a box empty to switch that limit off.'}
            </div>
        </div>
    )
}