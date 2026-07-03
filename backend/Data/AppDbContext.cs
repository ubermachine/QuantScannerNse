using Microsoft.EntityFrameworkCore;
using backend.Models;

namespace backend.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
            Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        public DbSet<StockMetadata> StockMetadatas { get; set; } = null!;
        public DbSet<DailyBar> DailyBars { get; set; } = null!;
        public DbSet<WeeklyBar> WeeklyBars { get; set; } = null!;
        public DbSet<WatchlistItem> WatchlistItems { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Primary Keys
            modelBuilder.Entity<StockMetadata>().HasKey(s => s.Ticker);
            modelBuilder.Entity<WatchlistItem>().HasKey(w => w.Ticker);

            // Configure Indexes for performance
            modelBuilder.Entity<DailyBar>()
                .HasIndex(d => new { d.Ticker, d.Date })
                .IsUnique();

            modelBuilder.Entity<WeeklyBar>()
                .HasIndex(w => new { w.Ticker, w.Date })
                .IsUnique();
        }
    }
}
