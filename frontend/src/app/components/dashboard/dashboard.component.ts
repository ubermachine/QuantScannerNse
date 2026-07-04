import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ScannerService } from '../../services/scanner.service';
import { ScanResponse, StockScanResult, WatchlistItem, SyncStatus, ChartCandle } from '../../models/scanner.model';
import { TradingViewChartComponent } from '../tradingview-chart/tradingview-chart.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, TradingViewChartComponent],
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
  strategies: string[] = [];
  selectedStrategy: string = '';

  
  // Watchlist addition fields
  showWatchlistAdd: boolean = false;
  manualTicker: string = '';
  manualPrice: number = 0;

  private pollInterval: any = null;

  constructor(private scannerService: ScannerService) {}

  ngOnInit() {
    this.loadWatchlist();
    this.checkSyncStatus();
    this.scannerService.getStrategies().subscribe(s => this.strategies = s);
    this.runScan();
  }

  ngOnDestroy() {
    if (this.pollInterval) {
      clearInterval(this.pollInterval);
    }
  }

  runScan() {
    this.isScanning = true;
    this.scannerService.scan(this.selectedStrategy).subscribe({
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

  changeTab(tab: 'all' | 'watchlist') {
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

    const all = this.scanResponse.results;
    let baseList: StockScanResult[];

    if (this.activeTab === 'watchlist') {
      const watchlistTickers = this.watchlist.map(w => w.ticker);
      baseList = all.filter(r => watchlistTickers.includes(r.ticker));
    } else {
      baseList = all;
    }

    // Search filter applies across all tabs
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
      const priceStr = window.prompt(`Enter your entry price for ${stock.ticker}:`, stock.price.toString());
      if (priceStr === null) return; // User cancelled the prompt
      const entryPrice = parseFloat(priceStr);
      
      if (isNaN(entryPrice)) {
        alert("Invalid entry price entered.");
        return;
      }

      this.scannerService.addToWatchlist(stock.ticker, entryPrice).subscribe(() => {
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
    if (score >= 72) return 'text-emerald-400 bg-emerald-950/40 border-emerald-900/60 shadow-emerald-500/5';
    if (score >= 54) return 'text-sky-400 bg-sky-950/40 border-sky-900/60 shadow-sky-500/5';
    if (score >= 36) return 'text-amber-400 bg-amber-950/40 border-amber-900/60 shadow-amber-500/5';
    return 'text-rose-400 bg-rose-950/40 border-rose-900/60 shadow-rose-500/5';
  }

  get fyersNeedsLogin(): boolean {
    if (!this.scanResponse) return false;
    return this.scanResponse.results.some(r => r.score >= 63 && (
      r.fyersOptionsFlow?.needsLogin === true ||
      r.fyersOptionsFlow?.squeezeStatus === 'Fyers Unavailable' ||
      r.fyersOptionsFlow?.squeezeStatus === 'Fyers Connection Error'
    ));
  }

  triggerFyersLogin() {
    // Always request a fresh login URL from the backend.
    // The backend resets its circuit breaker and starts a new MCP connection.
    this.scannerService.getFyersLoginUrl().subscribe({
      next: (res) => {
        if (res.loginUrl) {
          window.open(res.loginUrl, '_blank');
          this.pollForFyersAuthentication();
        } else {
          alert('FYERS MCP server could not be reached. Please ensure npx/Node.js is installed and try again.');
        }
      },
      error: (err) => {
        console.error('Failed to get FYERS login URL', err);
        alert('Failed to connect to FYERS. Check that the backend is running and npx is available.');
      }
    });
  }

  private pollForFyersAuthentication() {
    const interval = setInterval(() => {
      this.scannerService.getFyersStatus().subscribe({
        next: (status) => {
          if (!status.needsLogin) {
            clearInterval(interval);
            this.runScan(); // Run a full scan once to populate the page with the newly loaded data
          }
        },
        error: (err) => {
          console.error('Failed to check Fyers status', err);
        }
      });
    }, 4000);
  }

  populateOptionsData(ticker: string) {
    if (!this.selectedStock) return;
    
    // Visually flag options loading status
    this.selectedStock.fyersOptionsFlow = {
      needsLogin: false,
      squeezeStatus: 'Loading...',
      pcr: 0,
      skew: 0
    };

    this.scannerService.getFyersOptions(ticker).subscribe({
      next: (data) => {
        if (this.selectedStock && this.selectedStock.ticker === ticker) {
          this.selectedStock.fyersOptionsFlow = data;
        }
      },
      error: (err) => {
        console.error('Failed to populate options data', err);
        if (this.selectedStock && this.selectedStock.ticker === ticker) {
          this.selectedStock.fyersOptionsFlow = {
            needsLogin: false,
            squeezeStatus: 'Error Loading',
            pcr: 0,
            skew: 0
          };
        }
      }
    });
  }
}

