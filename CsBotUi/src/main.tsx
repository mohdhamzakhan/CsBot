import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App'
import './styles.css'

// Some devices report an invalid default language, which makes number/date formatting throw. Fall back to a fixed locale.
try { new Intl.NumberFormat(undefined).format(1); new Date().toLocaleString() } catch {
  const num = Number.prototype.toLocaleString, dt = Date.prototype.toLocaleString
  Number.prototype.toLocaleString = function (_l?: any, o?: any) { return num.call(this, 'en-US', o) }
  Date.prototype.toLocaleString = function (_l?: any, o?: any) { return dt.call(this, 'en-GB', o) }
}

ReactDOM.createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)
