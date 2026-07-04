using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backend.Models;

namespace backend.Services
{
    public class YahooFinanceService
    {
        private readonly HttpClient _httpClient;

        public YahooFinanceService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.Timeout = TimeSpan.FromSeconds(5); // Prevent hanging downloads from stalling slots
            // Set browser user-agent to bypass Yahoo's bot blocker
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<(List<DailyBar> Daily, List<WeeklyBar> Weekly)> FetchHistoricalDataAsync(string ticker, int daysToFetch = 2500)
        {
            var dailyBars = new List<DailyBar>();
            var weeklyBars = new List<WeeklyBar>();

            try
            {
                long endUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long startUnix = DateTimeOffset.UtcNow.AddDays(-daysToFetch).ToUnixTimeSeconds();

                // 1. Fetch Daily Bars
                string dailyUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{ticker}?period1={startUnix}&period2={endUnix}&interval=1d";
                var dailyJson = await FetchStringWithRetryAsync(dailyUrl);
                if (!string.IsNullOrEmpty(dailyJson))
                {
                    dailyBars = ParseChartJsonToDaily(ticker, dailyJson);
                }

                // 2. Fetch Weekly Bars (we fetch more history for weekly to compute 144w and 233w EMAs, e.g. 5 years)
                long startWeeklyUnix = DateTimeOffset.UtcNow.AddYears(-10).ToUnixTimeSeconds();
                string weeklyUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{ticker}?period1={startWeeklyUnix}&period2={endUnix}&interval=1wk";
                var weeklyJson = await FetchStringWithRetryAsync(weeklyUrl);
                if (!string.IsNullOrEmpty(weeklyJson))
                {
                    weeklyBars = ParseChartJsonToWeekly(ticker, weeklyJson);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching data for {ticker}: {ex.Message}");
            }

            return (dailyBars, weeklyBars);
        }

        public async Task<(double EpsGrowth, double DebtToEquity)> FetchFundamentalsAsync(string ticker)
        {
            double epsGrowth = 0;
            double debtToEquity = 0;

            try
            {
                string url = $"https://query1.finance.yahoo.com/v11/finance/quoteSummary/{ticker}?modules=defaultKeyStatistics,financialData";
                var json = await FetchStringWithRetryAsync(url);
                if (string.IsNullOrEmpty(json)) return (0, 0);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("quoteSummary", out var quoteSummary) &&
                    quoteSummary.TryGetProperty("result", out var resultList) &&
                    resultList.GetArrayLength() > 0)
                {
                    var firstResult = resultList[0];

                    // 1. Parse EPS Growth (earningsQuarterlyGrowth from defaultKeyStatistics or earningsGrowth from financialData)
                    if (firstResult.TryGetProperty("financialData", out var financialData))
                    {
                        if (financialData.TryGetProperty("earningsGrowth", out var eGrowthProp) &&
                            eGrowthProp.TryGetProperty("raw", out var eGrowthRaw))
                        {
                            epsGrowth = eGrowthRaw.GetDouble() * 100.0; // convert to % (e.g. 0.15 -> 15.0%)
                        }

                        if (financialData.TryGetProperty("debtToEquity", out var debtProp) &&
                            debtProp.TryGetProperty("raw", out var debtRaw))
                        {
                            // Yahoo finance returns debtToEquity as a raw value (e.g., 65.4 means 65.4% or a ratio of 0.654 depending on stock).
                            // Usually, if it's > 10, it's expressed as a percentage value. We will normalize it to a ratio.
                            double rawValue = debtRaw.GetDouble();
                            debtToEquity = rawValue > 5.0 ? rawValue / 100.0 : rawValue;
                        }
                    }

                    if (epsGrowth == 0 && firstResult.TryGetProperty("defaultKeyStatistics", out var keyStats))
                    {
                        if (keyStats.TryGetProperty("earningsQuarterlyGrowth", out var qGrowthProp) &&
                            qGrowthProp.TryGetProperty("raw", out var qGrowthRaw))
                        {
                            epsGrowth = qGrowthRaw.GetDouble() * 100.0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching fundamentals for {ticker}: {ex.Message}");
            }

            return (epsGrowth, debtToEquity);
        }

        public async Task<(double Price, double Ema200)> FetchIndexDataAsync()
        {
            double price = 0;
            double ema200 = 0;

            try
            {
                // Fetch Nifty 50 (^NSEI) - last 1 year to calculate 200 DMA
                long endUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long startUnix = DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeSeconds();
                string url = $"https://query1.finance.yahoo.com/v8/finance/chart/^NSEI?period1={startUnix}&period2={endUnix}&interval=1d";

                var json = await FetchStringWithRetryAsync(url);
                if (!string.IsNullOrEmpty(json))
                {
                    var bars = ParseChartJsonToDaily("^NSEI", json);
                    if (bars.Count > 0)
                    {
                        price = bars[^1].Close;
                        
                        // Compute 200 EMA
                        double[] closes = new double[bars.Count];
                        for (int i = 0; i < bars.Count; i++) closes[i] = bars[i].Close;
                        
                        var ema200Array = CalculateEma(closes, 200);
                        if (ema200Array.Length > 0)
                        {
                            ema200 = ema200Array[^1];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching index data: {ex.Message}");
            }

            return (price, ema200);
        }

        private async Task<string> FetchStringWithRetryAsync(string url, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync();
                    }
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return string.Empty; // Delisted or invalid symbol, no use retrying
                    }
                    if ((int)response.StatusCode == 429)
                    {
                        // Throttled: Wait a bit and retry
                        await Task.Delay(1000 * (i + 1));
                    }
                }
                catch (Exception)
                {
                    if (i == maxRetries - 1) throw;
                    await Task.Delay(500);
                }
            }
            return string.Empty;
        }

        private List<DailyBar> ParseChartJsonToDaily(string ticker, string json)
        {
            var bars = new List<DailyBar>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("chart", out var chart) &&
                chart.TryGetProperty("result", out var resultList) &&
                resultList.GetArrayLength() > 0)
            {
                var result = resultList[0];
                if (result.TryGetProperty("timestamp", out var timestamps) &&
                    result.TryGetProperty("indicators", out var indicators) &&
                    indicators.TryGetProperty("quote", out var quoteList) &&
                    quoteList.GetArrayLength() > 0)
                {
                    var quote = quoteList[0];
                    var opens = quote.TryGetProperty("open", out var o) ? o : default;
                    var highs = quote.TryGetProperty("high", out var h) ? h : default;
                    var lows = quote.TryGetProperty("low", out var l) ? l : default;
                    var closes = quote.TryGetProperty("close", out var c) ? c : default;
                    var volumes = quote.TryGetProperty("volume", out var v) ? v : default;

                    int len = timestamps.GetArrayLength();
                    for (int i = 0; i < len; i++)
                    {
                        // Skip if essential price data is missing/null
                        if (closes.ValueKind == JsonValueKind.Array && closes[i].ValueKind == JsonValueKind.Null) continue;
                        if (opens.ValueKind == JsonValueKind.Array && opens[i].ValueKind == JsonValueKind.Null) continue;

                        long timestamp = timestamps[i].GetInt64();
                        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;

                        bars.Add(new DailyBar
                        {
                            Ticker = ticker,
                            Date = date,
                            Open = opens[i].GetDouble(),
                            High = highs[i].GetDouble(),
                            Low = lows[i].GetDouble(),
                            Close = closes[i].GetDouble(),
                            Volume = volumes[i].ValueKind == JsonValueKind.Null ? 0 : volumes[i].GetInt64()
                        });
                    }
                }
            }

            return bars;
        }

        private List<WeeklyBar> ParseChartJsonToWeekly(string ticker, string json)
        {
            var bars = new List<WeeklyBar>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("chart", out var chart) &&
                chart.TryGetProperty("result", out var resultList) &&
                resultList.GetArrayLength() > 0)
            {
                var result = resultList[0];
                if (result.TryGetProperty("timestamp", out var timestamps) &&
                    result.TryGetProperty("indicators", out var indicators) &&
                    indicators.TryGetProperty("quote", out var quoteList) &&
                    quoteList.GetArrayLength() > 0)
                {
                    var quote = quoteList[0];
                    var opens = quote.TryGetProperty("open", out var o) ? o : default;
                    var highs = quote.TryGetProperty("high", out var h) ? h : default;
                    var lows = quote.TryGetProperty("low", out var l) ? l : default;
                    var closes = quote.TryGetProperty("close", out var c) ? c : default;
                    var volumes = quote.TryGetProperty("volume", out var v) ? v : default;

                    int len = timestamps.GetArrayLength();
                    for (int i = 0; i < len; i++)
                    {
                        if (closes.ValueKind == JsonValueKind.Array && closes[i].ValueKind == JsonValueKind.Null) continue;

                        long timestamp = timestamps[i].GetInt64();
                        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;

                        bars.Add(new WeeklyBar
                        {
                            Ticker = ticker,
                            Date = date,
                            Open = opens[i].GetDouble(),
                            High = highs[i].GetDouble(),
                            Low = lows[i].GetDouble(),
                            Close = closes[i].GetDouble(),
                            Volume = volumes[i].ValueKind == JsonValueKind.Null ? 0 : volumes[i].GetInt64()
                        });
                    }
                }
            }

            return bars;
        }

        private double[] CalculateEma(double[] values, int period)
        {
            double[] ema = new double[values.Length];
            if (values.Length == 0) return ema;

            double k = 2.0 / (period + 1);
            ema[0] = values[0];

            for (int i = 1; i < values.Length; i++)
            {
                ema[i] = (values[i] * k) + (ema[i - 1] * (1.0 - k));
            }
            return ema;
        }
    }
}
