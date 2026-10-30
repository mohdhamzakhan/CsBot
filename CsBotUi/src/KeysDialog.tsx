import { useEffect, useState } from 'react'
import { api } from './api'
import type { KeysStatus } from './api'

type Props = { onClose: () => void; onSaved: () => void }

export default function KeysDialog({ onClose, onSaved }: Props) {
  const [st, setSt] = useState<KeysStatus | null>(null)
  const [apiKey, setApiKey] = useState('')
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState('')
  const [ok, setOk] = useState('')
  const refresh = () => api.keysStatus().then(setSt).catch(e => setErr(e.message))
  useEffect(() => { refresh() }, [])

  const save = async () => {
    setBusy(true); setErr(''); setOk('')
    try {
      const r = await api.keysSave(apiKey, secret)
      setOk(r.warning ?? `Verified with CoinSwitch and saved to ${r.path}. You will not need to enter them again.`)
      setApiKey(''); setSecret(''); refresh(); onSaved()
    } catch (e: any) { setErr(e.message) } finally { setBusy(false) }
  }
  const remove = async () => {
    if (!window.confirm('Delete the saved keys from this computer? You will need to enter them again.')) return
    setErr(''); setOk('')
    try { await api.keysRemove(); setOk('Saved keys deleted.'); refresh(); onSaved() } catch (e: any) { setErr(e.message) }
  }

  return (
    <div className="modal" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="dialog">
        <div style={{ display: 'flex', alignItems: 'center' }}>
          <h2 style={{ margin: 0, fontSize: 18 }}>Connect your CoinSwitch keys</h2>
          <div style={{ flex: 1 }} />
          <button className="ghost" style={{ padding: '2px 10px' }} onClick={onClose}>&times;</button>
        </div>

        {st && (st.hasKeys
          ? <div className="gate pass" style={{ marginTop: 12 }}><div className="ico">&#10003;</div>
              <div><b>Keys are set</b> ({st.apiKeyPreview})<div className="note" style={{ margin: 0 }}>
                {st.source === 'environment' ? 'They come from your environment variables (CS_API_KEY / CS_SECRET_KEY).' : `Loaded automatically from ${st.source}.`}</div></div></div>
          : <div className="warnbox">No keys yet. Paste them below once; the app remembers them.</div>)}

        {st && !st.canEditKeys ? (
          <div className="warnbox">Keys cannot be changed from here because this is a remote connection (a safety rule). On the server, put them in <code>/etc/csbot.env</code> and restart the service; see the deployment guide.</div>
        ) : (<>
        <label>API key</label>
        <input value={apiKey} onChange={e => setApiKey(e.target.value)} placeholder="64 characters" autoComplete="off" spellCheck={false} style={{ fontFamily: 'var(--mono)' }} />
        <label>Secret key</label>
        <input type="password" value={secret} onChange={e => setSecret(e.target.value)} placeholder="64 characters" autoComplete="off" spellCheck={false} style={{ fontFamily: 'var(--mono)' }} />
        {err && <div className="err">{err}</div>}
        {ok && <div className="note pos">{ok}</div>}
        <button className="primary" style={{ marginTop: 14 }} disabled={busy || !apiKey.trim() || !secret.trim()} onClick={save}>{busy ? 'Verifying with CoinSwitch...' : 'Verify and save'}</button>
        </>)}

        <div className="note" style={{ marginTop: 12 }}>
          <b>Where do I get them?</b> CoinSwitch PRO &rarr; profile &rarr; API trading &rarr; create a key. If you only want charts and backtests, give the key no trading permission.<br />
          <b>Where are they stored?</b> In a plain text file on this computer: <code>{st?.savePath ?? '~/.csbot/keys.env'}</code> (your user folder, outside the project, so it is not included if you zip or share the project).
          Anyone who can open your Windows account can read it, so never share or upload that file. The keys are only ever sent to CoinSwitch.
        </div>
        <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
          {st?.canRemove && st.canEditKeys && <button className="ghost" onClick={remove}>Delete saved keys</button>}
          <div style={{ flex: 1 }} /><button className="ghost" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  )
}
