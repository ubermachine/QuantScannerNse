import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ScannerService } from '../../services/scanner.service';
import { TradingViewChartComponent } from '../tradingview-chart/tradingview-chart.component';
import { 
  PortfolioRequest, 
  PortfolioSimulationResult, 
  PortfolioTrade, 
  EquityCurvePoint, 
  ChartCandle 
} from '../../models/scanner.model';

@Component({
  selector: 'app-backtest',
  standalone: true,
  imports: [
    CommonModule, 
    FormsModule, 
    RouterLink, 
    RouterLinkActive, 
    TradingViewChartComponent
  ],
  templateUrl: './backtest.component.html'
})
export class BacktestComponent implements OnInit {
  // Navigation Tabs
  activeModeTab: 'single' | 'bulk' | 'portfolio' = 'portfolio';

  // Portfolio Simulator State
  request: PortfolioRequest = {
    startingCapital: 1000000,
    maxPositions: 10,
    sizingModel: 'Equal',
    riskPerTradePercent: 1.0,
    positionSizePercent: 10.0,
    transactionCostPercent: 0.05,
    slippagePercent: 0.10,
    strategy: 'Both',
    startDate: '2023-01-01',
    endDate: '2026-07-01'
  };
  isLoading: boolean = false;
  hasRun: boolean = false;
  results: PortfolioSimulationResult | null = null;
  activeChartTab: 'portfolio' | 'stock' = 'portfolio';
  equityChartData: ChartCandle[] = [];
  stockChartData: ChartCandle[] = [];
  stockChartTicker: string = '';
  stockMarkers: any[] = [];
  selectedTrade: PortfolioTrade | null = null;

  // Single Stock Backtest State
  singleTicker: string = 'DRREDDY';
  singleStrategy: 'HCT' | 'LRHR' = 'HCT';
  isSingleLoading: boolean = false;
  singleHasRun: boolean = false;
  singleChartData: ChartCandle[] = [];
  singleMarkers: any[] = [];
  singleTrades: any[] = [];
  singleStats: any = null;

  // Bulk Backtest State
  isBulkLoading: boolean = false;
  bulkHasRun: boolean = false;
  bulkWatchlistResults: any[] = [];
  bulkSummary: any = null;

  constructor(private scannerService: ScannerService) {}

  ngOnInit() {
    const today = new Date();
    const threeYearsAgo = new Date();
    threeYearsAgo.setFullYear(today.getFullYear() - 3);
    
    this.request.endDate = today.toISOString().split('T')[0];
    this.request.startDate = threeYearsAgo.toISOString().split('T')[0];
  }

  // PORTFOLIO SIMULATOR ACTIONS
  runSimulation() {
    this.isLoading = true;
    this.hasRun = true;
    this.selectedTrade = null;
    this.stockChartData = [];
    this.stockChartTicker = '';
    this.stockMarkers = [];
    this.activeChartTab = 'portfolio';

    const reqCopy = { ...this.request };
    
    this.scannerService.runPortfolioSimulation(reqCopy).subscribe({
      next: (res) => {
        this.results = res;
        this.isLoading = false;
        
        if (res.equityCurve && res.equityCurve.length > 0) {
          this.equityChartData = res.equityCurve.map(pt => {
            const dateStr = pt.date.includes('T') ? pt.date.split('T')[0] : pt.date;
            return {
              date: dateStr,
              open: pt.balance,
              high: pt.balance,
              low: pt.balance,
              close: pt.balance,
              volume: 0,
              jnsar: null,
              fib618: null,
              macdLine: null,
              macdSignal: null,
              macdHistogram: null
            };
          });
        }
      },
      error: (err) => {
        console.error('Simulation failed', err);
        this.isLoading = false;
      }
    });
  }

  selectTrade(trade: PortfolioTrade) {
    this.selectedTrade = trade;
    this.activeChartTab = 'stock';
    this.stockChartTicker = trade.ticker;
    this.stockChartData = [];
    this.stockMarkers = [];

    this.scannerService.getChartData(trade.ticker, 500).subscribe({
      next: (res) => {
        this.stockChartData = res.candles;

        const entryDateStr = trade.entryDate.includes('T') ? trade.entryDate.split('T')[0] : trade.entryDate;
        const exitDateStr = trade.exitDate.includes('T') ? trade.exitDate.split('T')[0] : trade.exitDate;

        this.stockMarkers = [
          {
            time: entryDateStr,
            position: 'belowBar',
            color: '#3b82f6', // blue entry arrow
            shape: 'arrowUp',
            text: `Buy @ ${trade.entryPrice.toFixed(2)}`
          },
          {
            time: exitDateStr,
            position: 'aboveBar',
            color: '#ef4444', // red exit arrow
            shape: 'arrowDown',
            text: `Sell (${trade.exitReason}) @ ${trade.exitPrice.toFixed(2)}`
          }
        ];
      },
      error: (err) => {
        console.error(`Failed to fetch chart data for ${trade.ticker}`, err);
      }
    });
  }

  setChartTab(tab: 'portfolio' | 'stock') {
    if (tab === 'stock' && !this.selectedTrade) return;
    this.activeChartTab = tab;
  }

  // SINGLE STOCK BACKTESTER ACTIONS
  runSingleBacktest() {
    if (!this.singleTicker) return;
    this.isSingleLoading = true;
    this.singleHasRun = true;
    this.singleChartData = [];
    this.singleMarkers = [];
    this.singleTrades = [];
    this.singleStats = null;

    let ticker = this.singleTicker.trim().toUpperCase();

    this.scannerService.getChartData(ticker, 500).subscribe({
      next: (res) => {
        this.singleChartData = res.candles;
        const sim = this.simulateSingleStock(res.candles, this.singleStrategy, ticker);
        this.singleMarkers = sim.markers;
        this.singleTrades = sim.trades;
        
        // Compute Stats
        const wins = sim.trades.filter(t => t.profit > 0);
        const losses = sim.trades.filter(t => t.profit <= 0);
        const totalProfit = sim.trades.reduce((sum, t) => sum + t.profit, 0);
        const returnPercent = sim.trades.reduce((sum, t) => sum + t.profitPercent, 0);
        const winRate = sim.trades.length > 0 ? (wins.length / sim.trades.length) * 100 : 0;
        
        const grossWins = wins.reduce((sum, t) => sum + t.profit, 0);
        const grossLosses = Math.abs(losses.reduce((sum, t) => sum + t.profit, 0));
        const profitFactor = grossLosses > 0 ? grossWins / grossLosses : (grossWins > 0 ? 99.9 : 0);

        this.singleStats = {
          totalTrades: sim.trades.length,
          winningTrades: wins.length,
          losingTrades: losses.length,
          winRate,
          totalProfit,
          returnPercent,
          profitFactor
        };
        this.isSingleLoading = false;
      },
      error: (err) => {
        console.error(`Single backtest failed for ${ticker}`, err);
        this.isSingleLoading = false;
      }
    });
  }

  // BULK BACKTESTER (WATCHLIST WIDE) ACTIONS
  runBulkBacktest() {
    this.isBulkLoading = true;
    this.bulkHasRun = true;
    this.bulkWatchlistResults = [];
    this.bulkSummary = null;

    this.scannerService.getWatchlist().subscribe({
      next: (list) => {
        if (list.length === 0) {
          this.isBulkLoading = false;
          return;
        }

        let completed = 0;
        const resultsList: any[] = [];

        list.forEach(item => {
          this.scannerService.getChartData(item.ticker, 500).subscribe({
            next: (res) => {
              const simHct = this.simulateSingleStock(res.candles, 'HCT', item.ticker);
              const simLrhr = this.simulateSingleStock(res.candles, 'LRHR', item.ticker);
              
              const hctWins = simHct.trades.filter(t => t.profit > 0);
              const hctReturn = simHct.trades.reduce((sum, t) => sum + t.profitPercent, 0);
              const lrhrWins = simLrhr.trades.filter(t => t.profit > 0);
              const lrhrReturn = simLrhr.trades.reduce((sum, t) => sum + t.profitPercent, 0);

              resultsList.push({
                ticker: item.ticker,
                entryPrice: item.entryPrice,
                hctTrades: simHct.trades.length,
                hctWinRate: simHct.trades.length > 0 ? (hctWins.length / simHct.trades.length) * 100 : 0,
                hctReturn: hctReturn,
                lrhrTrades: simLrhr.trades.length,
                lrhrWinRate: simLrhr.trades.length > 0 ? (lrhrWins.length / simLrhr.trades.length) * 100 : 0,
                lrhrReturn: lrhrReturn
              });

              completed++;
              if (completed === list.length) {
                this.finalizeBulk(resultsList);
              }
            },
            error: (err) => {
              console.error(`Failed bulk fetch for ${item.ticker}`, err);
              completed++;
              if (completed === list.length) {
                this.finalizeBulk(resultsList);
              }
            }
          });
        });
      },
      error: (err) => {
        console.error('Watchlist fetch failed for bulk backtest', err);
        this.isBulkLoading = false;
      }
    });
  }

  private finalizeBulk(results: any[]) {
    this.bulkWatchlistResults = results;
    
    const totalHctTrades = results.reduce((sum, r) => sum + r.hctTrades, 0);
    const avgHctReturn = results.length > 0 ? results.reduce((sum, r) => sum + r.hctReturn, 0) / results.length : 0;
    const totalLrhrTrades = results.reduce((sum, r) => sum + r.lrhrTrades, 0);
    const avgLrhrReturn = results.length > 0 ? results.reduce((sum, r) => sum + r.lrhrReturn, 0) / results.length : 0;

    this.bulkSummary = {
      totalHctTrades,
      avgHctReturn,
      totalLrhrTrades,
      avgLrhrReturn,
      watchlistSize: results.length
    };
    this.isBulkLoading = false;
  }

  // SIMULATOR CORE LOGIC
  simulateSingleStock(candles: ChartCandle[], strategy: 'HCT' | 'LRHR', ticker: string): { markers: any[], trades: any[] } {
    const markers: any[] = [];
    const trades: any[] = [];
    let position: any = null;

    for (let i = 200; i < candles.length; i++) {
      const c = candles[i];

      if (position) {
        if (c.jnsar && c.jnsar > position.stopLoss) {
          position.stopLoss = c.jnsar;
        }

        let exitPrice = 0;
        let exitReason = '';

        if (c.low <= position.stopLoss) {
          exitPrice = position.stopLoss;
          exitReason = 'Stop Loss';
        } else if (c.high >= position.target) {
          exitPrice = position.target;
          exitReason = 'Target Hit';
        }

        if (exitPrice > 0) {
          const profit = (exitPrice - position.entryPrice) * position.shares;
          const profitPercent = ((exitPrice - position.entryPrice) / position.entryPrice) * 100;

          trades.push({
            ticker: ticker,
            entryDate: position.entryDate,
            entryPrice: position.entryPrice,
            exitDate: c.date,
            exitPrice: exitPrice,
            shares: position.shares,
            profit: profit,
            profitPercent: profitPercent,
            exitReason: exitReason
          });

          markers.push({
            time: c.date,
            position: 'aboveBar',
            color: '#ef4444',
            shape: 'arrowDown',
            text: `Exit (${exitReason}) @ ${exitPrice.toFixed(2)}`
          });

          position = null;
        }
      } else {
        let isBuy = false;
        let target = 0;
        let stopLoss = 0;

        if (strategy === 'HCT') {
          const ema8 = c.ema8 || 0;
          const ema21 = c.ema21 || 0;
          const ema200 = c.ema200 || 0;
          const jnsar = c.jnsar || 0;
          const fib618 = c.fib618 || 0;

          if (c.close > ema200 && ema8 > ema21 && c.close > jnsar && fib618 > 0) {
            const distToFib = Math.abs(c.close - fib618) / fib618;
            if (distToFib <= 0.02) {
              isBuy = true;
              target = c.close * 1.15;
              stopLoss = jnsar;
            }
          }
        } else if (strategy === 'LRHR') {
          const ema200 = c.ema200 || 0;
          const jnsar = c.jnsar || 0;

          if (ema200 > 0 && c.close > ema200) {
            const dist = (c.close - ema200) / ema200;
            if (dist <= 0.04) {
              isBuy = true;
              target = c.close * 1.25;
              stopLoss = jnsar > 0 ? Math.min(jnsar, c.close * 0.93) : c.close * 0.93;
            }
          }
        }

        if (isBuy) {
          position = {
            ticker: ticker,
            entryDate: c.date,
            entryPrice: c.close,
            target: target,
            stopLoss: stopLoss,
            shares: Math.floor(50000 / c.close) || 1
          };

          markers.push({
            time: c.date,
            position: 'belowBar',
            color: '#10b981',
            shape: 'arrowUp',
            text: `Entry @ ${c.close.toFixed(2)}`
          });
        }
      }
    }

    return { markers, trades };
  }

  changeModeTab(tab: 'single' | 'bulk' | 'portfolio') {
    this.activeModeTab = tab;
  }
}
