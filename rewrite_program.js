const fs = require('fs');
let code = fs.readFileSync('backend/Program.cs', 'utf8');

code = code.replace(/using Microsoft\.EntityFrameworkCore;\r?\n/, 'using DuckDB.NET.Data;\r\nusing Dapper;\r\n');
code = code.replace(/\/\/ Register Database Context \(SQLite\)\r?\nbuilder\.Services\.AddDbContext<AppDbContext>\(options =>\r?\n\s*options\.UseSqlite\("Data Source=quantscanner\.db"\)\);\r?\n/, '');

const seederTarget = `using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    context.Database.EnsureCreated();

    if (!context.StockMetadatas.Any())`;

const seederReplacement = `using (var connection = new DuckDBConnection("Data Source=quantscanner.duckdb"))
{
    connection.Open();
    
    // Create Tables
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS StockMetadatas (Ticker VARCHAR PRIMARY KEY, Name VARCHAR, Sector VARCHAR);
        CREATE TABLE IF NOT EXISTS WatchlistItems (Ticker VARCHAR PRIMARY KEY, EntryPrice DOUBLE);
        CREATE TABLE IF NOT EXISTS DailyBars (Ticker VARCHAR, Date TIMESTAMP, Open DOUBLE, High DOUBLE, Low DOUBLE, Close DOUBLE, Volume BIGINT, PRIMARY KEY (Ticker, Date));
        CREATE TABLE IF NOT EXISTS WeeklyBars (Ticker VARCHAR, Date TIMESTAMP, Open DOUBLE, High DOUBLE, Low DOUBLE, Close DOUBLE, Volume BIGINT, PRIMARY KEY (Ticker, Date));
    ");

    var count = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM StockMetadatas");
    if (count == 0)`;

code = code.replace(seederTarget, seederReplacement);
code = code.replace(/context\.StockMetadatas\.AddRange\(defaultStocks\);\r?\n\s*context\.SaveChanges\(\);\r?\n\s*\}/, `foreach(var t in defaultTickers) {
            connection.Execute("INSERT INTO StockMetadatas (Ticker, Name, Sector) VALUES (@Ticker, @Name, 'NSE')", new { Ticker = t, Name = t.Replace(".NS", "") });
        }
    }`);

fs.writeFileSync('backend/Program.cs', code, 'utf8');
console.log("Program.cs rewritten cleanly.");
