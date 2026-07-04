const fs = require('fs');
let code = fs.readFileSync('backend/Services/ScannerService.cs', 'utf8');

code = code.replace(/var stocks = connection\.Query<StockMetadata>\("SELECT \* FROM StockMetadatas"\)\.ToList\(\);/, 'using var syncConn = new DuckDBConnection(_connectionString); syncConn.Open(); var stocks = syncConn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();');
code = code.replace(/using \(var scope = _scopeFactory\.CreateScope\(\)\)[\s\S]*?var localContext = scope\.ServiceProvider\.GetRequiredService<AppDbContext>\(\);/, 'using var localContext = new DuckDBConnection(_connectionString); localContext.Open();');

fs.writeFileSync('backend/Services/ScannerService.cs', code);
console.log("Sync fixed.");
