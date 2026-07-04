using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using backend.Models;
using backend.Services;
using DuckDB.NET.Data;
using Dapper;

namespace backend.Tests
{
    public class BacktestServiceTests
    {
        private readonly string _connectionString = "Data Source=quantscanner.duckdb";

        private void SetupDatabase()
        {
            using var connection = new DuckDBConnection(_connectionString);
            connection.Open();
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS StockMetadatas (Ticker VARCHAR PRIMARY KEY, Name VARCHAR, Sector VARCHAR);
                CREATE TABLE IF NOT EXISTS WatchlistItems (Ticker VARCHAR PRIMARY KEY, EntryPrice DOUBLE);
                CREATE TABLE IF NOT EXISTS DailyBars (Ticker VARCHAR, Date TIMESTAMP, Open DOUBLE, High DOUBLE, Low DOUBLE, Close DOUBLE, Volume BIGINT, PRIMARY KEY (Ticker, Date));
                CREATE TABLE IF NOT EXISTS WeeklyBars (Ticker VARCHAR, Date TIMESTAMP, Open DOUBLE, High DOUBLE, Low DOUBLE, Close DOUBLE, Volume BIGINT, PRIMARY KEY (Ticker, Date));
            ");

            // Clean database tables for test isolation
            connection.Execute("DELETE FROM StockMetadatas");
            connection.Execute("DELETE FROM DailyBars");
            connection.Execute("DELETE FROM WeeklyBars");

            // Seed test ticker metadata
            connection.Execute("INSERT INTO StockMetadatas (Ticker, Name, Sector) VALUES ('AARTIIND.NS', 'Aarti Industries', 'Chemicals')");

            // Seed daily bars (at least 200 daily bars to fulfill simulator constraints)
            var startDate = new DateTime(2025, 1, 1);
            using var appender = connection.CreateAppender("DailyBars");
            for (int i = 0; i < 250; i++)
            {
                var date = startDate.AddDays(i);
                var row = appender.CreateRow();
                row.AppendValue("AARTIIND.NS");
                row.AppendValue(date);
                double price = 500.0 + i;
                row.AppendValue(price - 1.0);
                row.AppendValue(price + 2.0);
                row.AppendValue(price - 2.0);
                row.AppendValue(price);
                row.AppendValue((long)100000 + i);
                row.EndRow();
            }
            appender.Close();

            // Seed weekly bars
            using var wAppender = connection.CreateAppender("WeeklyBars");
            for (int i = 0; i < 50; i++)
            {
                var date = startDate.AddDays(i * 7);
                var row = wAppender.CreateRow();
                row.AppendValue("AARTIIND.NS");
                row.AppendValue(date);
                double price = 500.0 + (i * 7);
                row.AppendValue(price - 1.0);
                row.AppendValue(price + 2.0);
                row.AppendValue(price - 2.0);
                row.AppendValue(price);
                row.AppendValue((long)500000);
                row.EndRow();
            }
            wAppender.Close();
        }

        [Fact]
        public async Task TestRunPortfolioSimulationAsync_ExecutesChronologically_ReturnsValidResult()
        {
            // Arrange
            SetupDatabase();
            var indicatorService = new IndicatorService();
            var backtestService = new BacktestService(indicatorService);

            var request = new PortfolioRequest
            {
                StartingCapital = 1000000,
                MaxPositions = 10,
                SizingModel = "Equal",
                PositionSizePercent = 10.0,
                TransactionCostPercent = 0.05,
                SlippagePercent = 0.10,
                Strategy = "Both",
                StartDate = new DateTime(2025, 1, 1),
                EndDate = new DateTime(2025, 12, 31)
            };

            // Act
            var result = await backtestService.RunPortfolioSimulationAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(1000000, result.StartingCapital);
            Assert.True(result.EndingCapital >= 0);
            Assert.NotNull(result.EquityCurve);
            Assert.True(result.EquityCurve.Count > 0);
            
            // Check that the daily equity curve points are sorted chronologically
            var dates = result.EquityCurve.Select(pt => pt.Date).ToList();
            var sortedDates = dates.OrderBy(d => d).ToList();
            Assert.Equal(sortedDates, dates);
        }
    }
}
