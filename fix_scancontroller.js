const fs = require('fs');
let code = fs.readFileSync('backend/Controllers/ScanController.cs', 'utf8');

code = code.replace(/using Microsoft\.EntityFrameworkCore;\r?\n/, 'using DuckDB.NET.Data;\r\nusing Dapper;\r\n');
code = code.replace(/using backend\.Data;\r?\n/, '');

code = code.replace(/private readonly AppDbContext _context;/, 'private readonly string _conn = "Data Source=quantscanner.duckdb";');
code = code.replace(/AppDbContext context,/, '');
code = code.replace(/_context = context;/, '');

code = code.replace(/var bars = await _context\.DailyBars\s*\.Where\(b => b\.Ticker == ticker\)\s*\.OrderBy\(b => b\.Date\)\s*\.ToListAsync\(\);/, 'using var conn = new DuckDBConnection(_conn); conn.Open(); var bars = conn.Query<DailyBar>("SELECT * FROM DailyBars WHERE Ticker = @Ticker ORDER BY Date", new { Ticker = ticker }).ToList();');

code = code.replace(/var list = await _context\.WatchlistItems\.OrderByDescending\(w => w\.AddedAt\)\.ToListAsync\(\);/, 'var list = await _scannerService.GetWatchlistAsync();');
code = code.replace(/var exists = await _context\.WatchlistItems\.AnyAsync\(w => w\.Ticker == item\.Ticker\);/, 'var list = await _scannerService.GetWatchlistAsync(); var exists = list.Any(w => w.Ticker == item.Ticker);');
code = code.replace(/_context\.WatchlistItems\.Add\(item\);\s*await _context\.SaveChangesAsync\(\);/, 'await _scannerService.AddToWatchlistAsync(item.Ticker, item.EntryPrice);');
code = code.replace(/var item = await _context\.WatchlistItems\.FirstOrDefaultAsync\(w => w\.Ticker == ticker\);\s*if \(item != null\)\s*\{\s*_context\.WatchlistItems\.Remove\(item\);\s*await _context\.SaveChangesAsync\(\);\s*\}/, 'await _scannerService.RemoveFromWatchlistAsync(ticker);');

code = code.replace(/var dailyCounts = await _context\.DailyBars\.GroupBy\(b => b\.Ticker\)\.Select\(g => new \{ Ticker = g\.Key, Count = g\.Count\(\) \}\)\.ToListAsync\(\);/, 'using var conn = new DuckDBConnection(_conn); conn.Open(); var dailyCounts = conn.Query("SELECT Ticker, COUNT(*) as Count FROM DailyBars GROUP BY Ticker").ToList();');
code = code.replace(/var weeklyCounts = await _context\.WeeklyBars\.GroupBy\(b => b\.Ticker\)\.Select\(g => new \{ Ticker = g\.Key, Count = g\.Count\(\) \}\)\.ToListAsync\(\);/, 'var weeklyCounts = conn.Query("SELECT Ticker, COUNT(*) as Count FROM WeeklyBars GROUP BY Ticker").ToList();');
code = code.replace(/var metaCount = await _context\.StockMetadatas\.CountAsync\(\);/, 'var metaCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM StockMetadatas");');

fs.writeFileSync('backend/Controllers/ScanController.cs', code);
console.log('ScanController rewritten.');
