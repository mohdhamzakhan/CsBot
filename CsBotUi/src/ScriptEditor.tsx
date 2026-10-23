import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import type { SavedScript, ScriptCheck, ScriptTemplate } from './api'

type Props = { code: string; setCode: (c: string) => void; params: Record<string, number>; setParams: (p: Record<string, number>) => void }

const HELP = `SERIES      open high low close volume hl2 hlc3 ohlc4 tr bar_index time
CLOCK (UTC) minute_of_day  hour  day_index  tf_minutes (candle length)
            session_min(startMin)  minutes since a session starting at startMin (minutes after 00:00 UTC)
            new_session(startMin)  1 on the first candle of each session, else 0
            vwap(startMin)         volume-weighted average price, restarting at startMin each day
POSITION    position (1 long, -1 short, 0 flat)   entry_price   bars_since_exit
VARIABLES   x = expr            recalculated on every bar
            var n = 0           remembered between bars; update with:  n := n + 1
            x[1]                value of x one bar ago (also close[2], ema(close,20)[1] ...)
INPUTS      len = input(20, "EMA length", 2, 200, 1)      -> becomes a setting box
INDICATORS  sma ema rma wma stdev highest lowest sum change roc rsi atr
            macd(src,fast,slow)  macd_signal(src,fast,slow,sig)  macd_hist(...)
            bb_upper(src,len,mult) bb_lower(...)  stoch_k(len)  adx(len) plus_di(len) minus_di(len)
            cci(src,len)  supertrend(len,mult)  supertrend_dir(len,mult)  vol(len)  crossover(a,b)  crossunder(a,b)  barssince(cond)
MATH        abs max min sqrt log exp pow round floor ceil sign nz(x,0) na(x)   (na alone = empty value, e.g. var x = na)
OPERATORS   + - * / % ^   == != < > <= >=   and or not   cond ? a : b
ACTIONS     long(size)   short(size)   flat()       size = fraction of capital, 1 = 100%
            stop_loss(pct)  take_profit(pct)  trail_stop(pct)     percent of entry price, checked on the candle close
            stop_at(price)  target_at(price)   exact price levels; call them AFTER long()/short().
               Tested against each LATER candle's high/low. If both are inside one candle the
               stop is assumed to hit first; gaps fill at the open. Example for a 1.5R target:
               risk = close - stopLevel ...  long(1); stop_at(stopLevel); target_at(close + 1.5 * risk)
               LIVE: the bot checks once per closed candle (no resting stop orders; CoinSwitch spot is limit-only).
            On SPOT, short() just means "be flat".
SHOW        plot(expr, "name", "#f5a623")   on the price chart
            plot_sub(expr, "name")          in the indicator pane
CONTROL     if cond then long(1)            one line, or several actions:  if cond then a(); b()
            if cond                         multi-line block
               long(1)
            elif cond2
               flat()
            else
               ...
            end
RULES       Indicator functions (ema, rsi, ...) must be written at the top level, assigned to a variable,
            BEFORE you use them inside an if. Comments start with //`

export default function ScriptEditor({ code, setCode, params, setParams }: Props) {
  const [templates, setTemplates] = useState<ScriptTemplate[]>([])
  const [saved, setSaved] = useState<SavedScript[]>([])
  const [check, setCheck] = useState<ScriptCheck | null>(null)
  const [name, setName] = useState('')
  const [msg, setMsg] = useState('')
  const gutter = useRef<HTMLPreElement>(null)

  const loadSaved = () => api.scriptSaved().then(setSaved).catch(() => {})
  useEffect(() => { api.scriptTemplates().then(t => { setTemplates(t); if (!code && t.length) setCode(t[0].code) }).catch(e => setMsg(e.message)); loadSaved() }, [])

  useEffect(() => {
    if (!code.trim()) { setCheck(null); return }
    const id = setTimeout(() => api.scriptCheck(code).then(setCheck).catch(e => setCheck({ ok: false, error: e.message })), 500)
    return () => clearTimeout(id)
  }, [code])

  const known = (c: string) => !c.trim() || templates.some(t => t.code === c) || saved.some(s => s.code === c)
  const replace = (c: string) => { if (known(code) || window.confirm('Replace the script in the editor? Unsaved changes will be lost.')) setCode(c) }
  const lines = code.split('\n').length
  const errLine = check && !check.ok ? check.line : undefined

  const save = async () => {
    setMsg('')
    try { const r = await api.scriptSave(name, code); setMsg(`Saved as "${r.saved}"`); setName(''); loadSaved() } catch (e: any) { setMsg(e.message) }
  }
  const tpl = templates.find(t => t.code === code)

  return (
    <div>
      <label>Start from a template</label>
      <select value="" onChange={e => { const t = templates.find(x => x.id === e.target.value); if (t) replace(t.code) }}>
        <option value="">Load a template...</option>
        {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select>
      {saved.length > 0 && <>
        <label>My saved scripts</label>
        <div style={{ display: 'flex', gap: 6 }}>
          <select value="" onChange={e => { const s = saved.find(x => x.name === e.target.value); if (s) { replace(s.code); setName(s.name) } }}>
            <option value="">Load saved...</option>{saved.map(s => <option key={s.name} value={s.name}>{s.name}</option>)}
          </select>
          <button className="ghost" title="Delete the saved script named in the box below" onClick={async () => { if (name && window.confirm(`Delete saved script "${name}"?`)) { await api.scriptDelete(name); setMsg('Deleted'); loadSaved() } }}>Del</button>
        </div></>}
      {tpl && <div className="note"><b>{tpl.name}:</b> {tpl.description}<br /><i>{tpl.evidence}</i></div>}

      <label>Script</label>
      <div style={{ display: 'flex', border: '1px solid var(--line)', borderRadius: 6, background: '#0b0f14', overflow: 'hidden' }}>
        <pre ref={gutter} aria-hidden style={{ margin: 0, padding: '6px 6px', textAlign: 'right', color: '#5b6573', font: '12px/1.5 ui-monospace, Consolas, monospace', overflow: 'hidden', minWidth: 34, userSelect: 'none' }}>
          {Array.from({ length: lines }, (_, i) => <div key={i} style={errLine === i + 1 ? { background: '#5a1d1d', color: '#fff' } : undefined}>{i + 1}</div>)}
        </pre>
        <textarea value={code} spellCheck={false} wrap="off" rows={18}
          onChange={e => setCode(e.target.value)}
          onScroll={e => { if (gutter.current) gutter.current.scrollTop = e.currentTarget.scrollTop }}
          onKeyDown={e => {
            if (e.key === 'Tab') {
              e.preventDefault(); const t = e.currentTarget, s = t.selectionStart
              setCode(code.slice(0, s) + '  ' + code.slice(t.selectionEnd)); requestAnimationFrame(() => { t.selectionStart = t.selectionEnd = s + 2 })
            }
          }}
          style={{ border: 0, borderRadius: 0, background: 'transparent', font: '12px/1.5 ui-monospace, Consolas, monospace', resize: 'vertical', whiteSpace: 'pre', overflow: 'auto', padding: '6px 8px', flex: 1, minHeight: 260 }} />
      </div>
      {check && (check.ok
        ? <div className="note pos">Script OK &middot; needs about {check.warmupBars} bars of history</div>
        : <div className="err">{check.error}</div>)}

      {check?.ok && check.inputs && check.inputs.length > 0 && (<>
        <label style={{ marginTop: 12 }}>Script settings</label>
        <div className="row">
          {check.inputs.map(i => (
            <div key={i.name}><label>{i.label}</label>
              <input type="number" step={i.step} min={i.min ?? undefined} max={i.max ?? undefined} value={params[i.name] ?? i.default}
                onChange={e => setParams({ ...params, [i.name]: +e.target.value })} /></div>))}
        </div></>)}
      <label>Rebalance band (ignore size changes smaller than this)</label>
      <input type="number" step="0.05" min="0.05" max="0.5" value={params.band ?? 0.15} onChange={e => setParams({ ...params, band: +e.target.value })} />

      <div style={{ display: 'flex', gap: 6, marginTop: 10 }}>
        <input placeholder="Save as..." value={name} onChange={e => setName(e.target.value)} />
        <button className="ghost" onClick={save} disabled={!name.trim()}>Save</button>
      </div>
      {msg && <div className="note">{msg}</div>}
      <details style={{ marginTop: 10 }}>
        <summary style={{ cursor: 'pointer', color: 'var(--muted)' }}>Language reference</summary>
        <pre className="log" style={{ height: 'auto', maxHeight: 360 }}>{HELP}</pre>
      </details>
      <div className="note">Scripts run in a safe built-in interpreter (a small Pine-inspired language), not arbitrary code. It is not TradingView Pine itself, so Pine scripts must be rewritten. Decisions use each closed candle and execute at the next open.</div>
    </div>
  )
}
