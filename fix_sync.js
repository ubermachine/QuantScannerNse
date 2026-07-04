const fs = require('fs');
let code = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

const target = `var stocks = connection.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            int total = stocks.Count;
            int counter = 0;

            // Use SemaphoreSlim to run up to 10 concurrent download tasks from Yahoo Finance
            var semaphore = new SemaphoreSlim(10);
            var tasks = stocks.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 1. Download Price Bars (daily and weekly)
                    var (daily, weekly) = await _yahooFinanceService.FetchHistoricalDataAsync(stock.Ticker);

                    // 2. Commit to database within a thread-safe scoped DbContext
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var localContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                        if (daily.Count > 0)
                        {
                            await SaveDailyBarsInternalAsync(localContext, stock.Ticker, daily);
                        }
                        if (weekly.Count > 0)
                        {
                            await SaveWeeklyBarsInternalAsync(localContext, stock.Ticker, weekly);
                        }
                    }`;

const replacement = `using var syncConn = new DuckDBConnection(_connectionString); syncConn.Open(); var stocks = syncConn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            int total = stocks.Count;
            int counter = 0;

            var semaphore = new SemaphoreSlim(10);
            var tasks = stocks.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var (daily, weekly) = await _yahooFinanceService.FetchHistoricalDataAsync(stock.Ticker);

                    using var localContext = new DuckDBConnection(_connectionString);
                    localContext.Open();

                    if (daily.Count > 0)
                    {
                        await SaveDailyBarsInternalAsync(localContext, stock.Ticker, daily);
                    }
                    if (weekly.Count > 0)
                    {
                        await SaveWeeklyBarsInternalAsync(localContext, stock.Ticker, weekly);
                    }`;

code = code.replace(target, replacement);

fs.writeFileSync('backend/Services/ScannerService.cs', code);
console.log("fix_sync done");
