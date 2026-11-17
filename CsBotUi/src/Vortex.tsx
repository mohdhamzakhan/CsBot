import { useEffect, useRef } from 'react'

/**
 * Decorative particle vortex. It does not measure anything by itself: the page drives its speed
 * from how convinced the running agents are (average target position size).
 */
export default function Vortex({ intensity = 1, count = 460 }: { intensity?: number; count?: number }) {
    const ref = useRef<HTMLCanvasElement>(null)
    const speed = useRef(intensity)
    speed.current = intensity

    useEffect(() => {
        const cv = ref.current!, ctx = cv.getContext('2d')!
        const calm = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
        const P = Array.from({ length: count }, () => ({ u: Math.random(), a: Math.random() * Math.PI * 2, r: Math.random() }))
        let raf = 0, t = 0, w = 0, h = 0

        const fit = () => {
            const d = window.devicePixelRatio || 1
            w = cv.clientWidth; h = cv.clientHeight
            cv.width = Math.max(1, w * d); cv.height = Math.max(1, h * d)
            ctx.setTransform(d, 0, 0, d, 0, 0)
        }
        fit()
        const ro = new ResizeObserver(fit); ro.observe(cv)

        const draw = () => {
            ctx.clearRect(0, 0, w, h)
            const k = calm ? 0.15 : speed.current
            t += 0.011 * k
            const cx = w / 2, top = h * 0.06, span = h * 0.88, maxR = Math.min(w * 0.34, h * 0.36)
            ctx.lineCap = 'round'
            for (const p of P) {
                p.u = (p.u + 0.0013 * k) % 1                                   // drift along the axis
                const y = p.u
                const shape = Math.pow(Math.sin(Math.PI * y), 0.75)             // narrow at both ends, wide in the middle
                const rad = shape * (0.25 + 0.75 * p.r) * maxR
                const ang = p.a + t * (1 + (1 - y) * 1.8) + y * 5               // twists faster near the top
                const a0 = ang - 0.16
                const x1 = cx + rad * Math.cos(ang), x0 = cx + rad * Math.cos(a0)
                const y1 = top + y * span + rad * 0.18 * Math.sin(ang), y0 = top + y * span + rad * 0.18 * Math.sin(a0)
                const depth = (Math.sin(ang) + 1) / 2                           // front particles are darker
                ctx.strokeStyle = `hsla(${212 + p.r * 14}, 82%, ${38 + (1 - depth) * 22}%, ${0.16 + depth * 0.55})`
                ctx.lineWidth = 0.7 + depth * 0.9
                ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(x1, y1); ctx.stroke()
            }
            raf = requestAnimationFrame(draw)
        }
        raf = requestAnimationFrame(draw)
        return () => { cancelAnimationFrame(raf); ro.disconnect() }
    }, [count])

    return <canvas ref={ref} className="a-vortex-canvas" aria-hidden />
}