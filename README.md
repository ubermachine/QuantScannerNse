# QuantScanner 🚀

QuantScanner is a high-performance quantitative stock scanning and charting platform designed for retail-executable factor scanning of Indian Equities (NSE). It combines a robust **C# .NET 8 Web API** backend, a high-speed concurrent SQLite data sync engine, and a sleek **Angular 18** financial dashboard powered by **TradingView's Lightweight Charts**.

---

## 🖥️ System Architecture & Data Flow

QuantScanner is designed to scan 800+ stocks in **under 800ms** by bypassing traditional ORM bottlenecks, implementing concurrency-friendly SQLite Write-Ahead Logging (WAL), and parallelizing math calculations across CPU cores.

```
                  ┌──────────────────────┐
                  │ Yahoo Finance API    │ (Daily & Weekly historical series)
                  └──────────┬───────────┘
                             │ Parallel Download (10 threads)
                             ▼
                  ┌──────────────────────┐
                  │ SQLite cache with    │ (quantscanner.db)
                  │ journal_mode=WAL     │ (Allows concurrent read/writes)
                  └──────────┬───────────┘
                             │ Bulk raw SqliteDataReader
                             ▼
                  ┌──────────────────────┐
                  │ Static RAM Cache     │ (Refreshed every sync)
                  └──────────┬───────────┘
                             │ Parallel CPU Loop (Parallel.ForEach)
                             ▼
                  ┌──────────────────────┐
                  │ .NET 8 Web API       │ (Scan Results & Watchlist REST API)
                  └──────────┬───────────┘
                             │ JSON over HTTP
                             ▼
                  ┌──────────────────────┐
                  │ Angular 18 Dashboard │ (lightweight-charts rendering)
                  └──────────────────────┘
```

---

## 📈 Trading Strategies Supported

### 1. HCT (High Conviction Trade)
*   **Concept:** A trend-following pullback strategy designed to enter high-momentum stocks right as they retrace to their dynamic Golden Ratio (61.8%) Fibonacci support level.
*   **Rules:**
    *   `Price > EMA 200` (Macro uptrend)
    *   `EMA 8 > EMA 21` (Short-term momentum)
    *   `Close > EMA 10` (Immediate strength)
    *   `Close > JNSAR` (JustNifty Stop-and-Reverse trailing support)
    *   `Price` is within **2%** of the dynamically calculated **61.8% Fibonacci Swing Pullback** level (Swing High is the maximum high of the last 120 days, Swing Low is the minimum low following that high).

### 2. LRHR (Low Risk High Return)
*   **Concept:** A weekly support rebound strategy designed to catch cyclical market leaders bottoming out on long-term weekly support.
*   **Rules:**
    *   `ProximityTo52WHigh >= 30%` (Significant discount from 52-week high)
    *   `Weekly MACD Line > Weekly MACD Signal` (Weekly bullish momentum crossover)
    *   `Price` is within **5%** of either the **Weekly EMA 144** or **Weekly EMA 233** support lines.

---

## ⚡ Performance Optimizations

To handle the expanded 867-stock universe efficiently, the following enterprise optimizations have been engineered:
1.  **N+1 Query Elimination:** All daily and weekly historical bars are loaded in bulk at the beginning of a scan and grouped in RAM, reducing SQLite queries from $1,734$ down to just **2**.
2.  **Raw ADO.NET Data Reader:** Bypasses EF Core tracking and entity mapping, streaming raw doubles directly from the SQLite page buffer into memory in under **40ms**.
3.  **SQLite WAL (Write-Ahead Logging):** Enables concurrent readers to access cached data while the parallel downloader is actively writing synced candles, preventing lock contention.
4.  **CPU Parallelization:** Employs `Parallel.ForEach` to split the math workload across all available CPU cores, calculating 3,800+ indicator series (EMAs, JNSAR, Swing Fibs, ATRs) in **20ms**.
5.  **Indefinite Server RAM Cache:** Caches compiled datasets on server RAM. Subsequent scans bypass the database completely and return results in **under 800ms**. Cache invalidates automatically when a sync completes.
6.  **Fast Memory Quicksort:** Replaced disk-ordered SQL seeks with chronological quicksorts in C# memory.

---

## 🛠️ Technology Stack

*   **Backend:** C# .NET 8, ASP.NET Core Web API, Entity Framework Core 8, Microsoft.Data.Sqlite.
*   **Frontend:** Angular 18 (Standalone components, reactive RxJS services).
*   **Charting:** TradingView Lightweight Charts v5.0 (Canvas/GPU accelerated).
*   **Styling:** Tailwind CSS, Outfit financial typography.
*   **Database:** SQLite.

---

## 🚀 Getting Started

### Prerequisites
*   [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
*   [Node.js (v18+)](https://nodejs.org/) & npm

### Setup & Execution

#### 1. Start the Backend Server
```bash
cd backend
dotnet run
```
*The server will initialize a fresh SQLite database, automatically seed the 867 NSE Stock metadata, and bind to `http://localhost:5150`.*

#### 2. Start the Frontend client
```bash
cd frontend
npm install
npm start
```
*The app will compile and launch the development dashboard on `http://localhost:4200/`.*

#### 3. Seed/Sync Market Data
*   Open `http://localhost:4200/`
*   Click **Sync Cache** to begin downloading historical daily and weekly price bars. The synchronizer utilizes a high-speed concurrent pool with Semaphore throttles and short-circuits delisted symbols to prevent delays.
*   Click **Scan Universe** to rank the stocks and filter active trades!

---

## 📂 Code Directory Sitemap

*   **Backend Source Code:**
    *   [Program.cs](backend/Program.cs) — Server startup, dependency injection, and initial database seeder.
    *   [AppDbContext.cs](backend/Data/AppDbContext.cs) — DB Schema and WAL mode configuration.
    *   [StockEntities.cs](backend/Models/StockEntities.cs) — Data Transfer Objects (DTOs) and Entities.
    *   [YahooFinanceService.cs](backend/Services/YahooFinanceService.cs) — Public API parser with timeout/retry adjustments.
    *   [IndicatorService.cs](backend/Services/IndicatorService.cs) — Technical indicator math (EMA, MACD, JNSAR, Swing Fibs, ATR).
    *   [ScannerService.cs](backend/Services/ScannerService.cs) — Core 7-factor evaluation engine with Parallel CPU loops and static memory cache.
    *   [ScanController.cs](backend/Controllers/ScanController.cs) — Web API controllers (Sync, Scan, Watchlist, Charts).
*   **Frontend Source Code:**
    *   [tickers.ts](frontend/src/app/constants/tickers.ts) — Constant array containing the 867 NSE ticker symbols.
    *   [scanner.model.ts](frontend/src/app/models/scanner.model.ts) — TypeScript interfaces for REST mapping.
    *   [scanner.service.ts](frontend/src/app/services/scanner.service.ts) — API client wrapper.
    *   [tradingview-chart.component.ts](frontend/src/app/components/tradingview-chart/tradingview-chart.component.ts) — GPU canvas wrapper for TradingView chart overlays.
    *   [dashboard.component.ts](frontend/src/app/components/dashboard/dashboard.component.ts) — State and filter logic.
    *   [dashboard.component.html](frontend/src/app/components/dashboard/dashboard.component.html) — Outfitted sidebar console and grid panel layout.
