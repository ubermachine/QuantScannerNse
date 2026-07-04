const fs = require('fs');
let code = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

code = code.replace(/using Microsoft\.EntityFrameworkCore;\r?\n/, 'using DuckDB.NET.Data;\r\nusing Dapper;\r\n');
code = code.replace(/using backend\.Data;\r?\n/, '');

code = code.replace(/private readonly AppDbContext _context;/, 'private readonly string _connectionString = "Data Source=quantscanner.duckdb";');
code = code.replace(/AppDbContext context,\r?\n\s*/, '');
code = code.replace(/_context = context;\r?\n\s*/, '');

code = code.replace(/var connection = _context\.Database\.GetDbConnection\(\);\r?\n\s*bool wasOpen = connection\.State == ConnectionState\.Open;\r?\n\s*if \(\!wasOpen\) await connection\.OpenAsync\(\);/g, 'var connection = new DuckDBConnection(_connectionString);\r\n            connection.Open();');
code = code.replace(/finally\r?\n\s*\{\r?\n\s*if \(\!wasOpen\) await connection\.CloseAsync\(\);\r?\n\s*\}/g, 'finally { connection.Dispose(); }');

code = code.replace(/var stocks = await _context\.StockMetadatas\.AsNoTracking\(\)\.ToListAsync\(\);/g, 'var stocks = connection.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();');
code = code.replace(/var stocks = await _context\.StockMetadatas\.ToListAsync\(\);/g, 'var stocks = connection.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();');

// Watchlist
code = code.replace(/return await _context\.WatchlistItems\.AsNoTracking\(\)\.ToListAsync\(\);/, 'using var conn = new DuckDBConnection(_connectionString); conn.Open(); return conn.Query<WatchlistItem>("SELECT * FROM WatchlistItems").ToList();');

code = code.replace(/var existing = await _context\.WatchlistItems\.FirstOrDefaultAsync\(x => x\.Ticker == ticker\);\r?\n\s*if \(existing != null\)\r?\n\s*\{\r?\n\s*existing\.EntryPrice = entryPrice;\r?\n\s*\}\r?\n\s*else\r?\n\s*\{\r?\n\s*_context\.WatchlistItems\.Add\(new WatchlistItem \{ Ticker = ticker, EntryPrice = entryPrice \}\);\r?\n\s*\}\r?\n\s*await _context\.SaveChangesAsync\(\);/, 'using var conn = new DuckDBConnection(_connectionString); conn.Open(); conn.Execute("INSERT INTO WatchlistItems (Ticker, EntryPrice) VALUES (@Ticker, @EntryPrice) ON CONFLICT (Ticker) DO UPDATE SET EntryPrice = excluded.EntryPrice", new { Ticker = ticker, EntryPrice = entryPrice });');

code = code.replace(/var existing = await _context\.WatchlistItems\.FirstOrDefaultAsync\(x => x\.Ticker == ticker\);\r?\n\s*if \(existing != null\)\r?\n\s*\{\r?\n\s*_context\.WatchlistItems\.Remove\(existing\);\r?\n\s*await _context\.SaveChangesAsync\(\);\r?\n\s*\}/, 'using var conn = new DuckDBConnection(_connectionString); conn.Open(); conn.Execute("DELETE FROM WatchlistItems WHERE Ticker = @Ticker", new { Ticker = ticker });');

// SaveDailyBarsInternalAsync
code = code.replace(/private async Task SaveDailyBarsInternalAsync\(AppDbContext ctx, string ticker, List<DailyBar> bars\)/, 'private async Task SaveDailyBarsInternalAsync(DuckDBConnection ctx, string ticker, List<DailyBar> bars)');
code = code.replace(/=> await SaveDailyBarsInternalAsync\(_context, ticker, freshBars\);/g, '=> await SaveDailyBarsInternalAsync(connection, ticker, freshBars);');

code = code.replace(/ctx\.DailyBars\.AddRange\(newBars\);\r?\n\s*await ctx\.SaveChangesAsync\(\);/, 'foreach(var b in newBars) { ctx.Execute("INSERT INTO DailyBars (Ticker, Date, Open, High, Low, Close, Volume) VALUES (@Ticker, @Date, @Open, @High, @Low, @Close, @Volume)", b); }');
code = code.replace(/ctx\.WeeklyBars\.AddRange\(newWeeklyBars\);\r?\n\s*await ctx\.SaveChangesAsync\(\);/, 'foreach(var b in newWeeklyBars) { ctx.Execute("INSERT INTO WeeklyBars (Ticker, Date, Open, High, Low, Close, Volume) VALUES (@Ticker, @Date, @Open, @High, @Low, @Close, @Volume)", b); }');

fs.writeFileSync('backend/Services/ScannerService.cs', code, 'utf8');
console.log("ScannerService rewritten cleanly.");
