# QuantScanner Release Notes - Institutional Upgrade (v2.0)

## Overview
This update transforms the QuantScanner from a basic momentum screener into an elite, "Prop Desk" grade institutional scanner. It introduces sophisticated quantitative metrics, advanced options data integration via FYERS MCP, and a high-performance in-memory DuckDB backend designed to scan thousands of tickers with minimal latency.

## Key Features & Upgrades

### 1. High-Performance Backend Architecture (DuckDB + Dapper)
- **Database Migration:** Migrated from Entity Framework Core to Dapper with an embedded DuckDB engine.
- **Extreme Speed:** Reduced scan times dramatically by leveraging DuckDB's fast columnar storage and analytical capabilities.
- **Concurrency Safeguards:** Implemented optimized parallel processing with centralized single-connection writes to adhere to DuckDB's strict concurrency limits while retaining multi-threaded processing speeds.

### 2. Institutional Flow & Options Data (FYERS MCP)
- **MCP Integration:** Integrated seamless, on-demand FYERS MCP server querying to pull live options data.
- **Institutional Scoring:** The scanner now detects options squeeze setups and scores candidates based on Institutional Options Flow.
- **Smart Fetching:** Implemented lazy-loading for options chains—data is fetched precisely when a ticker is selected, preventing server throttling and ensuring a smooth user experience.

### 3. "Prop Desk" Quantitative Indicators
We added an entire suite of advanced indicators used by seasoned quants and professional proprietary trading desks:
- **Point of Control (POC):** Calculates high-volume nodes across trading periods.
- **Year-To-Date VWAP (YTD VWAP):** Tracks institutional baseline averages over the calendar year.
- **Chandelier Exits (ATR Trailing Stops):** Implements dynamic trailing stops based on Average True Range to help lock in profits and limit downside risk.
- **MACD (Moving Average Convergence Divergence):** Fully integrated into backend analytics and frontend charting.
- **Other Metrics:** Enhanced RSI divergence detection, ADX trend strength, Bollinger Bands, Keltner Channels, and Z-Score statistics.

### 4. UI/UX Enhancements & Charting
- **Premium Aesthetics:** Upgraded the dashboard to feature dynamic, sleek, and highly responsive components (e.g., "Institutional Flow" and "Quant Metrics" cards).
- **TradingView Integration:** Embedded `lightweight-charts` for seamless charting. Added an interactive MACD pane (Line, Signal, and dynamic-colored Histogram) below the main candlesticks.
- **Watchlist Enhancements:** Polished watchlist interactions with real-time P&L tracking based on entry prices.

### 5. Bug Fixes & Stability Improvements
- **JSON Serialization (NaN Fix):** Resolved a critical crash (`SyntaxError: JSON.parse: unexpected end of data`) that occurred when dividing by zero on low-volume stocks. Implemented a `SafeRound` safety net across the scanner and charting APIs to perfectly sanitize output data.
- **Chart Rendering Fix:** Corrected a `lightweight-charts` initialization issue to ensure the MACD scales perfectly apply without crashing the renderer on load.

---
*Elevate your positional bets and conquer the market with QuantScanner v2.0!*
