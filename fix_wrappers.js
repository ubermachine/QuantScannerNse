const fs = require('fs');
let code = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

code = code.replace(/private async Task SaveDailyBarsAsync\(string ticker, List<DailyBar> freshBars\)\r?\n\s*\{\r?\n\s*await SaveDailyBarsInternalAsync\(_context, ticker, freshBars\);\r?\n\s*\}/, `private async Task SaveDailyBarsAsync(string ticker, List<DailyBar> freshBars)
{
    using var conn = new DuckDBConnection(_connectionString); conn.Open();
    await SaveDailyBarsInternalAsync(conn, ticker, freshBars);
}`);

code = code.replace(/private async Task SaveWeeklyBarsAsync\(string ticker, List<WeeklyBar> freshBars\)\r?\n\s*\{\r?\n\s*await SaveWeeklyBarsInternalAsync\(_context, ticker, freshBars\);\r?\n\s*\}/, `private async Task SaveWeeklyBarsAsync(string ticker, List<WeeklyBar> freshBars)
{
    using var conn = new DuckDBConnection(_connectionString); conn.Open();
    await SaveWeeklyBarsInternalAsync(conn, ticker, freshBars);
}`);

fs.writeFileSync('backend/Services/ScannerService.cs', code);
