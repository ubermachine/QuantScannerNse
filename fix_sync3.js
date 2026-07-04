const fs = require('fs');
let c = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

c = c.replace(/using \(var scope = _scopeFactory\.CreateScope\(\)\)\r?\n\s*\{\r?\n\s*var localContext = scope\.ServiceProvider\.GetRequiredService<AppDbContext>\(\);\r?\n\r?\n\s*if \(daily\.Count > 0\)\r?\n\s*\{\r?\n\s*await SaveDailyBarsInternalAsync\(localContext, stock\.Ticker, daily\);\r?\n\s*\}\r?\n\s*if \(weekly\.Count > 0\)\r?\n\s*\{\r?\n\s*await SaveWeeklyBarsInternalAsync\(localContext, stock\.Ticker, weekly\);\r?\n\s*\}\r?\n\s*\}/, `using var localContext = new DuckDBConnection(_connectionString);
                        localContext.Open();

                        if (daily.Count > 0)
                        {
                            await SaveDailyBarsInternalAsync(localContext, stock.Ticker, daily);
                        }
                        if (weekly.Count > 0)
                        {
                            await SaveWeeklyBarsInternalAsync(localContext, stock.Ticker, weekly);
                        }`);

c = c.replace(/private async Task SaveDailyBarsInternalAsync\(AppDbContext context, string ticker, List<DailyBar> freshBars\)[\s\S]*?\}\s*\}/, `private async Task SaveDailyBarsInternalAsync(DuckDBConnection context, string ticker, List<DailyBar> freshBars)
        {
            var existingDates = context.Query<DateTime>("SELECT Date FROM DailyBars WHERE Ticker = @Ticker", new { Ticker = ticker }).ToHashSet();
            var newBars = freshBars.Where(b => !existingDates.Contains(b.Date)).ToList();
            foreach(var b in newBars) {
                context.Execute("INSERT INTO DailyBars (Ticker, Date, Open, High, Low, Close, Volume) VALUES (@Ticker, @Date, @Open, @High, @Low, @Close, @Volume)", b);
            }
        }`);

c = c.replace(/private async Task SaveWeeklyBarsInternalAsync\(AppDbContext context, string ticker, List<WeeklyBar> freshBars\)[\s\S]*?\}\s*\}/, `private async Task SaveWeeklyBarsInternalAsync(DuckDBConnection context, string ticker, List<WeeklyBar> freshBars)
        {
            var existingDates = context.Query<DateTime>("SELECT Date FROM WeeklyBars WHERE Ticker = @Ticker", new { Ticker = ticker }).ToHashSet();
            var newBars = freshBars.Where(b => !existingDates.Contains(b.Date)).ToList();
            foreach(var b in newBars) {
                context.Execute("INSERT INTO WeeklyBars (Ticker, Date, Open, High, Low, Close, Volume) VALUES (@Ticker, @Date, @Open, @High, @Low, @Close, @Volume)", b);
            }
        }`);

c = c.replace(/private async Task SaveDailyBarsAsync\(string ticker, List<DailyBar> freshBars\)\r?\n\s*\{\r?\n\s*await SaveDailyBarsInternalAsync\(_context, ticker, freshBars\);\r?\n\s*\}/, `private async Task SaveDailyBarsAsync(string ticker, List<DailyBar> freshBars)
        {
            using var conn = new DuckDBConnection(_connectionString); conn.Open();
            await SaveDailyBarsInternalAsync(conn, ticker, freshBars);
        }`);

c = c.replace(/private async Task SaveWeeklyBarsAsync\(string ticker, List<WeeklyBar> freshBars\)\r?\n\s*\{\r?\n\s*await SaveWeeklyBarsInternalAsync\(_context, ticker, freshBars\);\r?\n\s*\}/, `private async Task SaveWeeklyBarsAsync(string ticker, List<WeeklyBar> freshBars)
        {
            using var conn = new DuckDBConnection(_connectionString); conn.Open();
            await SaveWeeklyBarsInternalAsync(conn, ticker, freshBars);
        }`);

fs.writeFileSync('backend/Services/ScannerService.cs', c);
