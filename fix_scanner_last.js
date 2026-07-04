const fs = require('fs');
let code = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

// Fix _context in ExecuteScanAsync (line 53)
code = code.replace(/var indexBars = await _context\.DailyBars\r?\n\s*\.Where\(b => b\.Ticker == "\^NSEI"\)\r?\n\s*\.OrderBy\(b => b\.Date\)\r?\n\s*\.ToListAsync\(\);/, 'using var earlyConn = new DuckDBConnection(_connectionString); earlyConn.Open(); var indexBars = earlyConn.Query<DailyBar>("SELECT * FROM DailyBars WHERE Ticker = \'^NSEI\' ORDER BY Date").ToList();');

// Fix line 72: connection used before declaration
code = code.replace(/var stocks = connection\.Query<StockMetadata>\("SELECT \* FROM StockMetadatas"\)\.ToList\(\);/, 'var stocks = earlyConn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();');

// Fix GetWatchlistAsync
code = code.replace(/using var conn = new DuckDBConnection\(_connectionString\); conn\.Open\(\); return conn\.Query<WatchlistItem>\("SELECT \* FROM WatchlistItems"\)\.ToList\(\);/, 'using var conn = new DuckDBConnection(_connectionString); conn.Open(); return await Task.FromResult(conn.Query<WatchlistItem>("SELECT * FROM WatchlistItems").ToList());');

// AddToWatchlistAsync signature is Task
code = code.replace(/public async Task AddToWatchlistAsync\(string ticker, double entryPrice\)\r?\n\s*\{\r?\n\s*using var conn = new DuckDBConnection\(_connectionString\); conn\.Open\(\); conn\.Execute\("INSERT INTO WatchlistItems \(Ticker, EntryPrice\) VALUES \(@Ticker, @EntryPrice\) ON CONFLICT \(Ticker\) DO UPDATE SET EntryPrice = excluded\.EntryPrice", new \{ Ticker = ticker, EntryPrice = entryPrice \}\);\r?\n\s*\}/, 'public async Task AddToWatchlistAsync(string ticker, double entryPrice) { using var conn = new DuckDBConnection(_connectionString); conn.Open(); conn.Execute("INSERT INTO WatchlistItems (Ticker, EntryPrice) VALUES (@Ticker, @EntryPrice) ON CONFLICT (Ticker) DO UPDATE SET EntryPrice = excluded.EntryPrice", new { Ticker = ticker, EntryPrice = entryPrice }); await Task.CompletedTask; }');

// RemoveFromWatchlistAsync signature is Task
code = code.replace(/public async Task RemoveFromWatchlistAsync\(string ticker\)\r?\n\s*\{\r?\n\s*using var conn = new DuckDBConnection\(_connectionString\); conn\.Open\(\); conn\.Execute\("DELETE FROM WatchlistItems WHERE Ticker = @Ticker", new \{ Ticker = ticker \}\);\r?\n\s*\}/, 'public async Task RemoveFromWatchlistAsync(string ticker) { using var conn = new DuckDBConnection(_connectionString); conn.Open(); conn.Execute("DELETE FROM WatchlistItems WHERE Ticker = @Ticker", new { Ticker = ticker }); await Task.CompletedTask; }');

// SyncDataAsync uses connection without declaring it in line 382/399?
// Wait, I will use grep to see where `connection` or `_context` are used in the rest of the file.
fs.writeFileSync('backend/Services/ScannerService.cs', code);
