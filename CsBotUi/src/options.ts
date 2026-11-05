/** Option maths (Black-Scholes, constant volatility). Everything is theoretical: real exchange prices include spread and skew. */

export type LegKind = 'call' | 'put' | 'stock'
export type Leg = { id: number; kind: LegKind; side: 1 | -1; strike: number; qty: number; premium: number; auto: boolean }   // stock: strike unused, premium = entry price

const erf = (x: number) => {                                  // Abramowitz-Stegun 7.1.26, error < 1.5e-7
  const s = Math.sign(x); x = Math.abs(x)
  const t = 1 / (1 + 0.3275911 * x)
  const y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.exp(-x * x)
  return s * y
}
export const cdf = (x: number) => 0.5 * (1 + erf(x / Math.SQRT2))
const pdf = (x: number) => Math.exp(-0.5 * x * x) / Math.sqrt(2 * Math.PI)

export type Greeks = { price: number; delta: number; gamma: number; vega: number; theta: number }
/** S spot, K strike, T years, r rate, sigma volatility (0.6 = 60%). vega per 1.00 of vol, theta per year. */
export function bs(S: number, K: number, T: number, r: number, sigma: number, call: boolean): Greeks {
  if (T <= 1e-9 || sigma <= 1e-9) {
    const df = Math.exp(-r * Math.max(T, 0)), intrinsic = call ? Math.max(S - K * df, 0) : Math.max(K * df - S, 0)
    return { price: intrinsic, delta: call ? (S > K ? 1 : 0) : (S < K ? -1 : 0), gamma: 0, vega: 0, theta: 0 }
  }
  const sq = sigma * Math.sqrt(T), d1 = (Math.log(S / K) + (r + 0.5 * sigma * sigma) * T) / sq, d2 = d1 - sq, df = Math.exp(-r * T)
  const price = call ? S * cdf(d1) - K * df * cdf(d2) : K * df * cdf(-d2) - S * cdf(-d1)
  const delta = call ? cdf(d1) : cdf(d1) - 1
  const theta = -(S * pdf(d1) * sigma) / (2 * Math.sqrt(T)) + (call ? -r * K * df * cdf(d2) : r * K * df * cdf(-d2))
  return { price, delta, gamma: pdf(d1) / (S * sq), vega: S * pdf(d1) * Math.sqrt(T), theta }
}

const unitValue = (l: Leg, S: number, T: number, r: number, sigma: number) => (l.kind === 'stock' ? S : bs(S, l.strike, T, r, sigma, l.kind === 'call').price)

/** Profit or loss of the whole position if the underlying is S with T years left. */
export const pnl = (legs: Leg[], S: number, T: number, r: number, sigma: number) =>
  legs.reduce((a, l) => a + l.side * l.qty * (unitValue(l, S, T, r, sigma) - l.premium), 0)

export function greeks(legs: Leg[], S: number, T: number, r: number, sigma: number) {
  let delta = 0, gamma = 0, vega = 0, theta = 0
  for (const l of legs) {
    if (l.kind === 'stock') { delta += l.side * l.qty; continue }
    const g = bs(S, l.strike, T, r, sigma, l.kind === 'call'), k = l.side * l.qty
    delta += k * g.delta; gamma += k * g.gamma; vega += k * g.vega / 100; theta += k * g.theta / 365
  }
  return { delta, gamma, vega, theta }     // vega per 1 vol point, theta per day
}

/** Net money paid (positive = you pay, negative = you receive) when the position is opened. */
export const netCost = (legs: Leg[]) => legs.reduce((a, l) => a + l.side * l.qty * l.premium, 0)

/** Exact analysis of the value at expiry (it is piecewise linear, so extremes and break-evens sit at the strikes). */
export function analyze(legs: Leg[], S0: number, T: number, r: number, sigma: number) {
  const strikes = [...new Set(legs.filter(l => l.kind !== 'stock').map(l => l.strike))].sort((a, b) => a - b)
  const big = Math.max(S0, ...strikes, 1) * 1000
  const at = (S: number) => pnl(legs, S, 0, 0, 0)
  const pts = [0, ...strikes, big]
  const vals = pts.map(at)
  const slopeUp = (at(big * 2) - at(big)) / big                  // profit per unit of price as the price rises forever (relative, so rounding noise is ignored)
  const maxProfit = slopeUp > 1e-9 ? Infinity : Math.max(...vals)
  const maxLoss = slopeUp < -1e-9 ? -Infinity : Math.min(...vals)
  const bes: number[] = []
  for (let i = 0; i + 1 < pts.length; i++) {
    const a = pts[i], b = pts[i + 1], fa = vals[i], fb = vals[i + 1]
    if (fa === 0 && i > 0) { bes.push(a); continue }
    if ((fa < 0 && fb > 0) || (fa > 0 && fb < 0)) bes.push(a + (b - a) * (0 - fa) / (fb - fa))
  }
  if (vals[vals.length - 1] === 0) bes.push(big)
  const breakevens = [...new Set(bes.map(x => Math.round(x * 100) / 100))].filter(x => x < big * 0.5).sort((a, b) => a - b)

  // probability that the position finishes in profit, if the price is log-normal with this volatility
  let pop: number | null = null
  if (T > 1e-9 && sigma > 1e-9) {
    const sq = sigma * Math.sqrt(T), mu = (r - 0.5 * sigma * sigma) * T
    const P = (x: number) => (x <= 0 ? 0 : cdf((Math.log(x / S0) - mu) / sq))
    const cuts = [0, ...new Set([...strikes, ...breakevens])].sort((a, b) => a - b)
    cuts.push(Infinity); pop = 0
    for (let i = 0; i + 1 < cuts.length; i++) {
      const a = cuts[i], b = cuts[i + 1], mid = Number.isFinite(b) ? (a + b) / 2 : a * 2 + 1
      if (at(mid) > 1e-9) pop += (Number.isFinite(b) ? P(b) : 1) - P(a)
    }
  }
  return { maxProfit, maxLoss, breakevens, pop }
}

/** A readable strike step for the underlying's price level (1, 2 or 5 times a power of ten, at least 1% of the price). */
export function niceStep(S: number) {
  const target = Math.max(S * 0.01, 1e-6), p = Math.pow(10, Math.floor(Math.log10(target)))
  return ([1, 2, 5, 10].map(m => m * p).find(v => v >= target) ?? 10 * p)
}

let seq = 1
const mk = (kind: LegKind, side: 1 | -1, strike: number, qty = 1): Leg => ({ id: seq++, kind, side, strike, qty, premium: 0, auto: true })

export const PRESETS: { id: string; name: string; build: (atm: number, s: number) => Leg[] }[] = [
  { id: 'long_call', name: 'Long call', build: (a) => [mk('call', 1, a)] },
  { id: 'long_put', name: 'Long put', build: (a) => [mk('put', 1, a)] },
  { id: 'bull_call', name: 'Bull call spread', build: (a, s) => [mk('call', 1, a), mk('call', -1, a + 2 * s)] },
  { id: 'bear_put', name: 'Bear put spread', build: (a, s) => [mk('put', 1, a), mk('put', -1, a - 2 * s)] },
  { id: 'long_straddle', name: 'Long straddle', build: (a) => [mk('call', 1, a), mk('put', 1, a)] },
  { id: 'short_straddle', name: 'Short straddle', build: (a) => [mk('call', -1, a), mk('put', -1, a)] },
  { id: 'long_strangle', name: 'Long strangle', build: (a, s) => [mk('call', 1, a + 2 * s), mk('put', 1, a - 2 * s)] },
  { id: 'short_strangle', name: 'Short strangle', build: (a, s) => [mk('call', -1, a + 2 * s), mk('put', -1, a - 2 * s)] },
  { id: 'iron_condor', name: 'Iron condor', build: (a, s) => [mk('put', 1, a - 4 * s), mk('put', -1, a - 2 * s), mk('call', -1, a + 2 * s), mk('call', 1, a + 4 * s)] },
  { id: 'iron_butterfly', name: 'Iron butterfly', build: (a, s) => [mk('put', 1, a - 3 * s), mk('put', -1, a), mk('call', -1, a), mk('call', 1, a + 3 * s)] },
  { id: 'covered_call', name: 'Covered call', build: (a, s) => [mk('stock', 1, 0), mk('call', -1, a + 2 * s)] },
  { id: 'protective_put', name: 'Protective put', build: (a, s) => [mk('stock', 1, 0), mk('put', 1, a - 2 * s)] },
]
export const newLeg = (kind: LegKind, side: 1 | -1, strike: number): Leg => mk(kind, side, strike)

/** Fill every "auto" premium with the model price. */
export const priceLegs = (legs: Leg[], S: number, T: number, r: number, sigma: number): Leg[] =>
  legs.map(l => (l.auto ? { ...l, premium: Math.round((l.kind === 'stock' ? S : bs(S, l.strike, T, r, sigma, l.kind === 'call').price) * 100) / 100 } : l))
