import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ScannerService } from '../../services/scanner.service';
import { ScanResponse, StockScanResult, WatchlistItem, SyncStatus, ChartCandle } from '../../models/scanner.model';
import { TradingViewChartComponent } from '../tradingview-chart/tradingview-chart.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, TradingViewChartComponent],
  templateUrl: './dashboard.component.html'
})
export class DashboardComponent implements OnInit, OnDestroy {
  scanResponse: ScanResponse | null = null;
  filteredResults: StockScanResult[] = [];
  watchlist: WatchlistItem[] = [];
  activeTab: 'all' | 'lrhr' | 'hct' | 'watchlist' = 'all';
  selectedStock: StockScanResult | null = null;
  chartData: ChartCandle[] = [];
  isScanning: boolean = false;
  syncStatus: SyncStatus | null = null;
  searchTerm: string = '';
  
  // Watchlist addition fields
  showWatchlistAdd: boolean = false;
  manualTicker: string = '';
  manualPrice: number = 0;

  private pollInterval: any = null;

  constructor(private scannerService: ScannerService) {}

  ngOnInit() {
    this.loadWatchlist();
    this.checkSyncStatus();
    this.runScan();
  }

  ngOnDestroy() {
    if (this.pollInterval) {
      clearInterval(this.pollInterval);
    }
  }

  runScan() {
    this.isScanning = true;
    this.scannerService.scan().subscribe({
      next: (res) => {
        this.scanResponse = res;
        this.applyFilter();
        if (this.filteredResults.length > 0 && !this.selectedStock) {
          this.selectStock(this.filteredResults[0]);
        }
        this.isScanning = false;
      },
      error: (err) => {
        console.error('Scan failed', err);
        this.isScanning = false;
      }
    });
  }

  startSync() {
    this.scannerService.startSync().subscribe({
      next: () => {
        this.checkSyncStatus();
      },
      error: (err) => console.error('Sync failed to start', err)
    });
  }

  checkSyncStatus() {
    this.scannerService.getSyncStatus().subscribe((status) => {
      this.syncStatus = status;
      if (status.isRunning) {
        this.startStatusPolling();
      } else {
        this.stopStatusPolling();
      }
    });
  }

  private startStatusPolling() {
    if (this.pollInterval) return;

    this.pollInterval = setInterval(() => {
      this.scannerService.getSyncStatus().subscribe((status) => {
        this.syncStatus = status;
        if (!status.isRunning) {
          this.stopStatusPolling();
          this.runScan(); // Auto refresh once sync finishes
        }
      });
    }, 1500);
  }

  private stopStatusPolling() {
    if (this.pollInterval) {
      clearInterval(this.pollInterval);
      this.pollInterval = null;
    }
  }

  loadWatchlist() {
    this.scannerService.getWatchlist().subscribe({
      next: (list) => {
        this.watchlist = list;
        if (this.activeTab === 'watchlist') {
          this.applyFilter();
        }
      },
      error: (err) => console.error('Watchlist fetch failed', err)
    });
  }

  changeTab(tab: 'all' | 'lrhr' | 'hct' | 'watchlist') {
    this.activeTab = tab;
    this.applyFilter();
    
    // Auto-select first item in the new view for the chart
    if (this.filteredResults.length > 0) {
      this.selectStock(this.filteredResults[0]);
    } else {
      this.selectedStock = null;
      this.chartData = [];
    }
  }

  applyFilter() {
    if (!this.scanResponse) return;

    let baseList = this.scanResponse.results;

    // Filter by Strategy Tab
    if (this.activeTab === 'lrhr') {
      baseList = baseList.filter(r => r.isLrhrMatch);
    } else if (this.activeTab === 'hct') {
      baseList = baseList.filter(r => r.isHctMatch);
    } else if (this.activeTab === 'watchlist') {
      const watchlistTickers = new Set(this.watchlist.map(w => w.ticker));
      baseList = baseList.filter(r => watchlistTickers.has(r.ticker));
    }

    // Filter by Search Term
    if (this.searchTerm.trim() !== '') {
      const term = this.searchTerm.toLowerCase();
      baseList = baseList.filter(r =>
        r.ticker.toLowerCase().includes(term) ||
        r.name.toLowerCase().includes(term) ||
        r.sector.toLowerCase().includes(term)
      );
    }

    this.filteredResults = baseList;
  }

  onSearchChange() {
    this.applyFilter();
  }

  selectStock(stock: StockScanResult) {
    this.selectedStock = stock;
    this.chartData = [];
    this.scannerService.getChartData(stock.ticker).subscribe({
      next: (res) => {
        this.chartData = res.candles;
      },
      error: (err) => console.error('Failed to load chart data', err)
    });
  }

  isWatchlisted(ticker: string): boolean {
    return this.watchlist.some(w => w.ticker === ticker);
  }

  getWatchlistEntry(ticker: string): number | null {
    const item = this.watchlist.find(w => w.ticker === ticker);
    return item ? item.entryPrice : null;
  }

  getWatchlistPL(stock: StockScanResult): { value: number, percent: number } | null {
    const entry = this.getWatchlistEntry(stock.ticker);
    if (entry === null) return null;
    const diff = stock.price - entry;
    const percent = (diff / entry) * 100.0;
    return { value: diff, percent };
  }

  toggleWatchlist(stock: StockScanResult) {
    if (this.isWatchlisted(stock.ticker)) {
      this.scannerService.removeFromWatchlist(stock.ticker).subscribe(() => {
        this.loadWatchlist();
      });
    } else {
      this.scannerService.addToWatchlist(stock.ticker, stock.price).subscribe(() => {
        this.loadWatchlist();
      });
    }
  }

  addManualWatchlist() {
    if (!this.manualTicker) return;
    this.scannerService.addToWatchlist(this.manualTicker.toUpperCase(), this.manualPrice).subscribe({
      next: () => {
        this.loadWatchlist();
        this.showWatchlistAdd = false;
        this.manualTicker = '';
        this.manualPrice = 0;
      },
      error: (err) => alert(err.error || 'Failed to add ticker to watchlist.')
    });
  }

  getScoreColorClass(score: number): string {
    if (score >= 80) return 'text-emerald-400 bg-emerald-950/40 border-emerald-900/60 shadow-emerald-500/5';
    if (score >= 60) return 'text-sky-400 bg-sky-950/40 border-sky-900/60 shadow-sky-500/5';
    if (score >= 40) return 'text-amber-400 bg-amber-950/40 border-amber-900/60 shadow-amber-500/5';
    return 'text-rose-400 bg-rose-950/40 border-rose-900/60 shadow-rose-500/5';
  }

  get fyersNeedsLogin(): boolean {
    if (!this.scanResponse) return false;
    return this.scanResponse.results.some(r => (r.isHctMatch || r.isLrhrMatch) && r.fyersOptionsFlow?.needsLogin === true);
  }

  triggerFyersLogin() {
    const matchedStock = this.scanResponse?.results.find(r => r.fyersOptionsFlow?.needsLogin);
    const loginUrl = matchedStock?.fyersOptionsFlow?.loginUrl || this.selectedStock?.fyersOptionsFlow?.loginUrl;

    if (loginUrl) {
      window.open(loginUrl, '_blank');
      this.pollForFyersAuthentication();
    } else {
      this.scannerService.getFyersLoginUrl().subscribe({
        next: (res) => {
          window.open(res.loginUrl, '_blank');
          this.pollForFyersAuthentication();
        },
        error: (err) => console.error('Failed to get login URL', err)
      });
    }
  }

  private pollForFyersAuthentication() {
    const interval = setInterval(() => {
      this.scannerService.scan().subscribe((res) => {
        const matches = res.results.filter(r => r.isHctMatch || r.isLrhrMatch);
        const stillNeedsLogin = matches.some(r => r.fyersOptionsFlow?.needsLogin);

        if (!stillNeedsLogin || matches.length === 0) {
          clearInterval(interval);
          this.scanResponse = res;
          this.applyFilter();
          if (this.selectedStock) {
            const updated = res.results.find(r => r.ticker === this.selectedStock!.ticker);
            if (updated) this.selectedStock = updated;
          }
        }
      });
    }, 3000);
  }
}
