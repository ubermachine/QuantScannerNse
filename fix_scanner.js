const fs = require('fs');
let c = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

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

fs.writeFileSync('backend/Services/ScannerService.cs', c);
console.log("ScannerService fixed.");
