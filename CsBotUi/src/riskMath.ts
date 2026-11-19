// Pure functions behind the Risk desk panel (no React), so they can be tested on their own.
// An agent's curve is a list of [unixSeconds, equity]; its P&L is equity minus its starting capital.

export type CurveAgent = { curve: number[][]; capital: number }

/** P&L of every agent on one shared time axis. A value is carried forward from the agent's latest earlier sample, and is 0 before its first sample. */
export function alignPnl(agents: CurveAgent[]): { ts: number[]; v: number[][] } {
    const ts = [...new Set(agents.flatMap(a => a.curve.map(p => p[0])))].sort((x, y) => x - y)
    const v = agents.map(a => {
        const out = new Array<number>(ts.length).fill(0)
        let j = -1
        for (let k = 0; k < ts.length; k++) {
            while (j + 1 < a.curve.length && a.curve[j + 1][0] <= ts[k]) j++
            out[k] = j >= 0 ? a.curve[j][1] - a.capital : 0
        }
        return out
    })
    return { ts, v }
}

/** Pearson correlation of the step-to-step changes of two series. null when either is flat or there are too few points. */
export function corr(x: number[], y: number[]): number | null {
    const n = Math.min(x.length, y.length)
    if (n < 4) return null
    const dx: number[] = [], dy: number[] = []
    for (let k = 1; k < n; k++) { dx.push(x[k] - x[k - 1]); dy.push(y[k] - y[k - 1]) }
    const mx = dx.reduce((a, b) => a + b, 0) / dx.length, my = dy.reduce((a, b) => a + b, 0) / dy.length
    let sxy = 0, sxx = 0, syy = 0
    for (let k = 0; k < dx.length; k++) { sxy += (dx[k] - mx) * (dy[k] - my); sxx += (dx[k] - mx) ** 2; syy += (dy[k] - my) ** 2 }
    if (sxx < 1e-12 || syy < 1e-12) return null
    return sxy / Math.sqrt(sxx * syy)
}

/** Largest fall from a peak, in percent, of an equity series. */
export function maxDrawdown(equity: number[]): number {
    let peak = -Infinity, dd = 0
    for (const e of equity) { peak = Math.max(peak, e); if (peak > 0) dd = Math.max(dd, ((peak - e) / peak) * 100) }
    return dd
}

/** Combined equity of the chosen agents: their summed capital plus their summed P&L at every time step. */
export function combinedEquity(v: number[][], caps: number[], include: boolean[]): number[] {
    const n = v.length ? v[0].length : 0
    const cap = caps.reduce((s, c, i) => s + (include[i] ? c : 0), 0)
    return Array.from({ length: n }, (_, k) => cap + v.reduce((s, row, i) => s + (include[i] ? row[k] : 0), 0))
}

/** If the running agents were averaged into one position: net and gross size, and how many hold the most common side. */
export function exposure(weights: number[]): { net: number; gross: number; sameSide: number; side: 'long' | 'short' | 'flat' } {
    const n = weights.length
    if (n === 0) return { net: 0, gross: 0, sameSide: 0, side: 'flat' }
    const longs = weights.filter(w => w > 0.01).length, shorts = weights.filter(w => w < -0.01).length, flats = n - longs - shorts
    const sameSide = Math.max(longs, shorts, flats)
    return {
        net: weights.reduce((a, b) => a + b, 0) / n, gross: weights.reduce((a, b) => a + Math.abs(b), 0) / n, sameSide,
        side: sameSide === longs && longs >= shorts && longs >= flats ? 'long' : sameSide === shorts && shorts >= flats ? 'short' : 'flat',
    }
}

/** Roughly how many independent bets n strategies amount to when they share an average pairwise correlation (equal weights). */
export function effectiveBets(avgCorr: number, n: number): number {
    const rho = Math.min(1, Math.max(0, avgCorr))
    return n / (1 + (n - 1) * rho)
}