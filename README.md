# CsBot: CoinSwitch PRO trading workbench (spot + futures)

```
CsBot/     .NET 8 backend (web API + strategy engine + backtester + paper/live trader). Serves the UI from wwwroot/.
CsBotUi/   React + TypeScript source of the UI (only needed if you want to change the UI).
```

## Run (Windows PowerShell)
```powershell
cd <folder>\CsBot
dotnet run
```
Open http://localhost:5080. The first time, a **Connect your CoinSwitch keys** window appears: paste your API key and secret key once.
The app checks them with CoinSwitch and saves them to `%USERPROFILE%\.csbot\keys.env` (outside the project folder), and picks them up
automatically every time after that. Click the "keys OK" badge at the top any time to change or delete them.

Other ways to supply keys (checked in this order): environment variables `CS_API_KEY` / `CS_SECRET_KEY`, a `keys.env` file next to where you run
the app, or the user-folder file above. Format of `keys.env`:
```
CS_API_KEY=your_api_key
CS_SECRET_KEY=your_secret_key
```
Real-money trading stays off unless you also set the environment variable `CS_LIVE_ENABLED=1` (a keys file cannot switch it on).
Never share or upload `keys.env`.

## Running it on a server (Oracle Cloud free tier)
See `DEPLOY-ORACLE.md`. The app has an optional login (`CS_APP_PASSWORD`) and refuses to listen on a public address without one.

## UI development (optional)
```powershell
cd <folder>\CsBotUi
npm install
npm run dev      # http://localhost:5173 (proxies /api to the .NET app)
npm run build    # writes into ..\CsBot\wwwroot
```

## Command line (still available)
```powershell
dotnet run -- fetch --market spot --exchange coinswitchx --symbol BTC/INR --interval 360 --days 730 --out btc_6h.csv
dotnet run -- fetch --market futures --symbol BTCUSDT --interval 360 --days 730 --out btcusdt_6h.csv
dotnet run -- fees --exchange coinswitchx
dotnet run -- cli --csv btc_6h.csv --market spot --compare --fee 0.04
```

## Chart and live prices
The chart loads by itself whenever you change market, symbol, timeframe or history (no button). The last price refreshes every 3 seconds
from CoinSwitch's ticker, and the current candle moves up and down with it; when a bar closes the final candle is fetched automatically.
The top bar shows last price, 24h change, high/low, volume, bid/ask (and funding for futures).

## Writing your own strategies (like TradingView)
Choose **Script editor** in the Strategy box. Pick one of 17 templates (Opening Range Breakout, Trend pullback EMA+VWAP, EMA trend, MACD, Supertrend, ADX trend, Donchian, ATR channel,
momentum, volatility-targeted trend, RSI pullback, Connors RSI(2), Bollinger reversion/breakout, z-score reversion, stochastic pullback, blank)
Intraday helpers: `session_min`, `new_session`, `vwap`, `minute_of_day`, and exact `stop_at(price)` / `target_at(price)` levels (R-multiples).
or write your own. `input(...)` lines become setting boxes, `plot(...)` draws lines on the chart, `plot_sub(...)` draws an indicator pane.
The editor checks the script as you type and shows the line of any error. Save scripts by name (stored in `CsBot/data/scripts`).
It is a small Pine-inspired language run by a safe built-in interpreter (no arbitrary code runs), not TradingView Pine itself.
A "Language reference" box in the editor lists everything. There is also a point-and-click **Custom rules** builder.

## What is where
- Costs: `CostConfig.cs`, `SimPortfolio.cs` (fee + GST + slippage + 1% TDS + funding, shared by backtest and paper trading)
- Script language: `Scripting/` (lexer, parser, interpreter, indicator library, templates)
- Strategies: `TrendFollowingStrategy.cs`, `MoreStrategies.cs` (Donchian, time-series momentum), `RuleStrategy.cs` (your own rules), `StrategyRegistry.cs`
- Live gate: `Backtester.cs` (`LiveGate`)
- Exchange: `CoinSwitchClient.cs` (signing, candles, orders)
- Trading engine and risk limits: `Trader.cs`
- HTTP API: `Api.cs`

## Safety design for live trading
Off by default (needs CS_LIVE_ENABLED=1), typed confirmation phrase, mandatory max order / max position / daily loss / leverage limits,
requires the exact strategy settings to have PASSED the backtest gate in the last 24h (override is explicit), kill switch that stops and closes the tracked position.
