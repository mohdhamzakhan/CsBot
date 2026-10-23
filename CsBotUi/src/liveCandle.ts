import type { LiveBar } from './api'

export type Forming = { t: number; o: number; h: number; l: number; c: number }

/**
 * Works out the still-forming candle from the latest traded price.
 * last = newest CLOSED candle we hold; prev = the forming candle computed on the previous tick (if any);
 * bar = the exchange's own partial bar, when it returned one.
 */
export function nextForming(last: { t: number; c: number }, prev: Forming | null, price: number, bar: LiveBar | null, intervalSec: number, nowSec: number): Forming {
  const k = Math.floor((nowSec - last.t) / intervalSec)
  let bucket = last.t + Math.max(1, k) * intervalSec
  if (bar && bar.t > last.t) bucket = bar.t                      // trust the exchange's bar boundaries when available
  if (bar && bar.t === bucket) return { t: bucket, o: bar.o, h: Math.max(bar.h, price), l: Math.min(bar.l, price), c: price }
  if (prev && prev.t === bucket) return { ...prev, h: Math.max(prev.h, price), l: Math.min(prev.l, price), c: price }
  const o = prev && prev.t < bucket ? prev.c : last.c           // a new bar opens where the previous one ended
  return { t: bucket, o, h: Math.max(o, price), l: Math.min(o, price), c: price }
}
