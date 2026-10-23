import { useState } from 'react'
import { api } from './api'

export default function LoginScreen({ onDone }: { onDone: () => void }) {
  const [pw, setPw] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState('')
  const go = async () => {
    setBusy(true); setErr('')
    try { await api.login(pw); onDone() } catch (e: any) { setErr(e.message) } finally { setBusy(false) }
  }
  return (
    <div className="modal" style={{ position: 'static', minHeight: '100vh' }}>
      <div className="dialog" style={{ width: 'min(400px,100%)' }}>
        <div className="brand" style={{ marginBottom: 14 }}>
          <div className="logo"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round"><path d="M6 4v16M6 7v6M12 9v10M12 12v5M18 3v14M18 6v7" /></svg></div>
          <div><b>CsBot</b><small>CoinSwitch PRO</small></div>
        </div>
        <label>Password</label>
        <input type="password" autoFocus value={pw} onChange={e => setPw(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && pw) go() }} />
        {err && <div className="err">{err}</div>}
        <button className="primary" disabled={busy || !pw} onClick={go}>{busy ? 'Checking...' : 'Log in'}</button>
        <div className="note">This site can use your CoinSwitch API keys, so it is password protected.</div>
      </div>
    </div>
  )
}
