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
  MultiStrategySimulationResult,
  StrategySimLine,
  EquityCurvePoint, 
  ChartCandle 
} from '../../models/scanner.model';
import { RotationBacktestResult } from '../../models/scanner.model';

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
  activeModeTab: 'single' | 'bulk' | 'portfolio' | 'indexRotation' = 'portfolio';

  // Portfolio Simulator State
  request: PortfolioRequest = {
    startingCapital: 1000000,
    maxPositions: 10,
    sizingModel: 'Equal',
    riskPerTradePercent: 1.0,
    positionSizePercent: 10.0,
    transactionCostPercent: 0.05,
    slippagePercent: 0.10,
    strategy: 'All',
    minScore: 40,
    startDate: '2023-01-01',
    endDate: '2026-07-01'
  };
  isLoading: boolean = false;
  hasRun: boolean = false;
  results: PortfolioSimulationResult | null = null;
  isRotationLoading = false;
  rotationBtResult: RotationBacktestResult | null = null;
  multiResults: StrategySimLine[] | null = null;
  activeChartTab: 'portfolio' | 'stock' = 'portfolio';
  equityChartData: ChartCandle[] = [];
  multiEquityData: { name: string; data: ChartCandle[] }[] = [];
  stockChartData: ChartCandle[] = [];
  stockChartTicker: string = '';
  stockMarkers: any[] = [];
  selectedTrade: PortfolioTrade | null = null;
  // Color palette for strategy lines
  stratColors = ['#6366f1','#10b981','#f59e0b','#ef4444','#ec4899',
                         '#06b6d4','#a78bfa','#f97316','#22c55e'];

  // Single Stock Backtest State
  singleTicker: string = 'DRREDDY';
  singleStrategy: 'HCT' | 'LRHR' = 'HCT';
  isSingleLoading: boolean = false;
  singleHasRun: boolean = false;
  singleChartData: ChartCandle[] = [];
  singleMarkers: any[] = [];
  singleTrades: any[] = [];
  singleStats: any = null;

  // Universe Scan Backtest State
  isBulkLoading: boolean = false;
  bulkHasRun: boolean = false;
  bulkUniverseResults: any[] = [];
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
    this.multiResults = null;
    this.multiEquityData = [];

    const reqCopy = { ...this.request };
    
    // If "All Strategies", use the compare-all endpoint
    if (reqCopy.strategy === 'All') {
      this.scannerService.compareAllStrategies(reqCopy).subscribe({
        next: (res) => {
          this.multiResults = res.strategies;
          this.isLoading = false;

          this.multiEquityData = res.strategies.map((s, i) => ({
            name: s.strategyName,
            data: (s.equityCurve || []).map((pt: any) => {
              const dateStr = pt.date.includes('T') ? pt.date.split('T')[0] : pt.date;
              return { date: dateStr, open: pt.balance, high: pt.balance, low: pt.balance, close: pt.balance, volume: 0, jnsar: null, fib618: null, macdLine: null, macdSignal: null, macdHistogram: null };
            })
          }));
        },
        error: (err) => {
          console.error('Compare-all failed', err);
          this.isLoading = false;
        }
      });
    } else {
      this.scannerService.runPortfolioSimulation(reqCopy).subscribe({
        next: (res) => {
          this.results = res;
          this.isLoading = false;
          
          if (res.equityCurve && res.equityCurve.length > 0) {
            this.equityChartData = res.equityCurve.map(pt => {
              const dateStr = pt.date.includes('T') ? pt.date.split('T')[0] : pt.date;
              return { date: dateStr, open: pt.balance, high: pt.balance, low: pt.balance, close: pt.balance, volume: 0, jnsar: null, fib618: null, macdLine: null, macdSignal: null, macdHistogram: null };
            });
          }
        },
        error: (err) => {
          console.error('Simulation failed', err);
          this.isLoading = false;
        }
      });
    }
  }

  runIndexRotation() {
    this.isRotationLoading = true;
    this.rotationBtResult = null;
    this.scannerService.runRotationBacktest({ startingCapital: 1000000 }).subscribe({
      next: (res: any) => {
        this.rotationBtResult = res;
        this.isRotationLoading = false;
      },
      error: () => {
        this.isRotationLoading = false;
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

  // UNIVERSE SCAN BULK BACKTESTER ACTIONS
  runBulkBacktest() {
    this.isBulkLoading = true;
    this.bulkHasRun = true;
    this.bulkUniverseResults = [];
    this.bulkSummary = null;

    // Fetch full scanned universe — NOT just watchlist
    this.scannerService.scan().subscribe({
      next: (scanResponse) => {
        const tickers: string[] = scanResponse.results
          .map((r: any) => r.ticker as string)
          .filter((t: string, i: number, arr: string[]) => arr.indexOf(t) === i); // dedupe

        if (tickers.length === 0) {
          this.isBulkLoading = false;
          return;
        }

        let completed = 0;
        const resultsList: any[] = [];

        tickers.forEach((ticker: string) => {
          this.scannerService.getChartData(ticker.replace('.NS', ''), 500).subscribe({
            next: (res) => {
              const simHct = this.simulateSingleStock(res.candles, 'HCT', ticker);
              const simLrhr = this.simulateSingleStock(res.candles, 'LRHR', ticker);

              const hctWins = simHct.trades.filter((t: any) => t.profit > 0);
              const hctReturn = simHct.trades.reduce((sum: number, t: any) => sum + t.profitPercent, 0);
              const lrhrWins = simLrhr.trades.filter((t: any) => t.profit > 0);
              const lrhrReturn = simLrhr.trades.reduce((sum: number, t: any) => sum + t.profitPercent, 0);

              resultsList.push({
                ticker: ticker.replace('.NS', ''),
                hctTrades: simHct.trades.length,
                hctWinRate: simHct.trades.length > 0 ? (hctWins.length / simHct.trades.length) * 100 : 0,
                hctReturn: hctReturn,
                lrhrTrades: simLrhr.trades.length,
                lrhrWinRate: simLrhr.trades.length > 0 ? (lrhrWins.length / simLrhr.trades.length) * 100 : 0,
                lrhrReturn: lrhrReturn
              });

              completed++;
              if (completed === tickers.length) {
                this.finalizeBulk(resultsList);
              }
            },
            error: (err) => {
              console.error(`Failed universe bulk fetch for ${ticker}`, err);
              completed++;
              if (completed === tickers.length) {
                this.finalizeBulk(resultsList);
              }
            }
          });
        });
      },
      error: (err) => {
        console.error('Universe scan fetch failed for bulk backtest', err);
        this.isBulkLoading = false;
      }
    });
  }

  private finalizeBulk(results: any[]) {
    this.bulkUniverseResults = results.sort((a, b) => (b.hctReturn + b.lrhrReturn) - (a.hctReturn + a.lrhrReturn));

    const totalHctTrades = results.reduce((sum, r) => sum + r.hctTrades, 0);
    const avgHctReturn = results.length > 0 ? results.reduce((sum, r) => sum + r.hctReturn, 0) / results.length : 0;
    const totalLrhrTrades = results.reduce((sum, r) => sum + r.lrhrTrades, 0);
    const avgLrhrReturn = results.length > 0 ? results.reduce((sum, r) => sum + r.lrhrReturn, 0) / results.length : 0;

    this.bulkSummary = {
      totalHctTrades,
      avgHctReturn,
      totalLrhrTrades,
      avgLrhrReturn,
      universeSize: results.length
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
          // LRHR: stock must be 30%+ below 52-week high (deep discount value),
          // then near a long-term weekly moving average (approximated via ema200 proximity)
          const jnsar = c.jnsar || 0;

          // Compute rolling 52-week (252 bars) high up to this candle
          const lookback = Math.min(252, i);
          let high52W = 0;
          for (let k = i - lookback; k <= i; k++) {
            if (candles[k].high > high52W) high52W = candles[k].high;
          }

          if (high52W > 0) {
            const discount = (high52W - c.close) / high52W;
            // Entry: 30%+ off the 52-week high (deep value), and near EMA200 (within 5%)
            const ema200 = c.ema200 || 0;
            const nearEma200 = ema200 > 0 && Math.abs((c.close - ema200) / ema200) <= 0.05;

            if (discount >= 0.30 && nearEma200) {
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

  changeModeTab(tab: 'single' | 'bulk' | 'portfolio' | 'indexRotation') {
    this.activeModeTab = tab;
  }
}