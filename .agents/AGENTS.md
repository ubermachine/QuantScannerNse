# Antigravity Rules for QuantScanner Workspace

## FYERS MCP Integration Best Practices

1. **API Response Structure (`get_option_chain`)**:
   - The Fyers MCP server returns the options data array inside the `optionsChain` property of the `data` object, NOT as the `data` object itself.
   - Example path: `dataNode?["data"]?["optionsChain"]`.

2. **On-Demand Fetching Only**:
   - NEVER fetch options data eagerly (e.g., in loops during full market scans).
   - Only fetch options data on-demand when the user explicitly interacts with a symbol (e.g., clicks a "Populate Options Data" button). Eager fetching overwhelms the MCP server and triggers unnecessary authentication flows.

3. **Symbol Formatting**:
   - Tickers coming from the scanner (e.g., `DRREDDY.NS`, `ASIANPAINT.NS`) must be transformed by stripping `.NS` and wrapping them in the FYERS format: `NSE:<SYMBOL>-EQ` before querying the MCP server.

## DuckDB and Dapper Integration

1. **Parameter Syntax**: When using Dapper with `DuckDBConnection`, **ALWAYS** use the `$` prefix for parameterized queries instead of `@`. 
   - *Incorrect*: `conn.Query("SELECT * FROM Table WHERE Ticker = @Ticker", new { Ticker = t })`
   - *Correct*: `conn.Query("SELECT * FROM Table WHERE Ticker = $Ticker", new { Ticker = t })`

2. **DuckDB Concurrency Limits**: DuckDB is an embedded file-based database. Opening multiple concurrent connections (e.g., inside a `Parallel.ForEach` or multiple `Task.Run` calls) triggers expensive OS file-locking protocols that destroy parallel performance. 
   - **Fix**: To insert or update data efficiently across multiple concurrent threads, instantiate a **single, shared `DuckDBConnection`** outside the parallel loop, pass it into the tasks, and wrap all database writes inside a strict `SemaphoreSlim(1)` block.

3. **Dapper Dynamic Typing and DuckDB Case Sensitivity**: Avoid using `connection.Query("SELECT ...")` to fetch `dynamic` records (i.e. `DapperRow`). DuckDB preserves the exact case of columns in the `SELECT` clause, but C#'s dynamic runtime often returns `null` if the properties are referenced with mismatched casing (e.g., `row.ticker` instead of `row.Ticker`), leading to fatal `ArgumentNullException`s.
   - **Fix**: **Always use strongly-typed models** (e.g., `connection.Query<DailyBar>(...)`). This forces Dapper to use reflection to map columns robustly and case-insensitively.

## C# Code Refactoring Guardrails

1. **Refactoring Tool Choice**: Do NOT use arbitrary Javascript/Bash regex scripts for multi-line C# refactoring or brace management. Always use the built-in `multi_replace_file_content` or `replace_file_content` tools to prevent unbalanced braces.
2. **Dependency Purges**: When migrating dependencies (e.g., EF Core to Dapper), you must comprehensively check and remove all lingering `using` statements and injected services (e.g., `AppDbContext`) across the entire file before compiling.
