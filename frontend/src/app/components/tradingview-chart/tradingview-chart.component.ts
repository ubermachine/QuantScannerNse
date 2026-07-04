import { Component, ElementRef, Input, OnChanges, OnDestroy, OnInit, SimpleChanges, ViewChild } from '@angular/core';
import { createChart, IChartApi, ISeriesApi, LineStyle, CandlestickSeries, LineSeries, HistogramSeries, AreaSeries } from 'lightweight-charts';
import { ChartCandle } from '../../models/scanner.model';

@Component({
  selector: 'app-tradingview-chart',
  standalone: true,
  template: `<div #chartContainer class="w-full h-[400px] border border-slate-800/80 rounded-xl overflow-hidden shadow-2xl shadow-blue-900/10"></div>`,
  styles: [`
    :host {
      display: block;
      width: 100%;
    }
  `]
})
export class TradingViewChartComponent implements OnInit, OnChanges, OnDestroy {
  @ViewChild('chartContainer', { static: true }) chartContainer!: ElementRef;
  @Input() data: ChartCandle[] = [];
  @Input() ticker: string = '';
  @Input() chartMode: 'candlestick' | 'equity' = 'candlestick';
  @Input() markers: any[] = [];
  @Input() multiData: { name: string; data: ChartCandle[] }[] = [];

  private chart: IChartApi | null = null;
  private equitySeries: any = null;
  private multiEquitySeries: any[] = [];
  private candlestickSeries: any = null;
  private ema8Series: any = null;
  private ema21Series: any = null;
  private ema200Series: any = null;
  private jnsarSeries: any = null;
  private macdLineSeries: any = null;
  private macdSignalSeries: any = null;
  private macdHistogramSeries: any = null;
  private resizeObserver: ResizeObserver | null = null;

  ngOnInit() {
    this.initChart();
  }

  ngOnChanges(changes: SimpleChanges) {
    if (changes['chartMode'] && !changes['chartMode'].firstChange) {
      if (this.chart) {
        this.chart.remove();
        this.chart = null;
      }
      this.initChart();
    } else {
      if (changes['data']) {
        this.updateData();
      }
      if (changes['markers'] && !changes['markers'].firstChange) {
        this.updateMarkers();
      }
    }
  }

  ngOnDestroy() {
    if (this.resizeObserver) {
      this.resizeObserver.disconnect();
    }
    this.multiEquitySeries = [];
    if (this.chart) {
      this.chart.remove();
    }
  }

  private initChart() {
    if (!this.chartContainer) return;

    // Create the chart with customized financial aesthetics
    this.chart = createChart(this.chartContainer.nativeElement, {
      layout: {
        background: { color: '#090d16' },
        textColor: '#94a3b8',
        fontSize: 12,
        fontFamily: "'Outfit', sans-serif"
      },
      grid: {
        vertLines: { color: 'rgba(30, 41, 59, 0.3)' },
        horzLines: { color: 'rgba(30, 41, 59, 0.3)' }
      },
      crosshair: {
        mode: 1, // Magnet mode
        vertLine: {
          color: '#3b82f6',
          width: 1,
          style: LineStyle.Dashed
        },
        horzLine: {
          color: '#3b82f6',
          width: 1,
          style: LineStyle.Dashed
        }
      },
      timeScale: {
        borderColor: '#1e293b',
        timeVisible: true,
        secondsVisible: false
      },
      rightPriceScale: {
        borderColor: '#1e293b',
        autoScale: true
      }
    });

    if (this.chartMode === 'equity') {
      // If multiData is provided, create one AreaSeries per strategy
      if (this.multiData && this.multiData.length > 0) {
        const colors = ['#6366f1','#10b981','#f59e0b','#ef4444','#ec4899',
                        '#06b6d4','#a78bfa','#f97316','#22c55e'];
        this.multiData.forEach((s, i) => {
          const series = this.chart!.addSeries(AreaSeries, {
            topColor: colors[i % colors.length] + '55',
            bottomColor: colors[i % colors.length] + '11',
            lineColor: colors[i % colors.length],
            lineWidth: 2,
            title: s.name
          });
          const areaData = s.data.map(d => ({ time: d.date, value: d.close }));
          series.setData(areaData);
          this.multiEquitySeries.push(series);
        });
      } else {
        this.equitySeries = this.chart.addSeries(AreaSeries, {
          topColor: 'rgba(99, 102, 241, 0.35)',
          bottomColor: 'rgba(16, 185, 129, 0.05)',
          lineColor: '#6366f1',
          lineWidth: 3,
          title: 'Equity Balance'
        });
      }
      
      this.chart.priceScale('right').applyOptions({
        scaleMargins: {
          top: 0.1,
          bottom: 0.1,
        }
      });
    } else {
      // 1. Candlestick Series (Unified v5 signature)
      this.candlestickSeries = this.chart.addSeries(CandlestickSeries, {
        upColor: '#10b981',
        downColor: '#ef4444',
        borderVisible: false,
        wickUpColor: '#10b981',
        wickDownColor: '#ef4444'
      });

      // 2. Overlay Lines (Unified v5 signature)
      this.ema8Series = this.chart.addSeries(LineSeries, {
        color: '#3b82f6',
        lineWidth: 2,
        title: 'EMA 8'
      });

      this.ema21Series = this.chart.addSeries(LineSeries, {
        color: '#f97316',
        lineWidth: 2,
        title: 'EMA 21'
      });

      this.ema200Series = this.chart.addSeries(LineSeries, {
        color: '#8b5cf6',
        lineWidth: 3,
        title: 'EMA 200'
      });

      this.jnsarSeries = this.chart.addSeries(LineSeries, {
        color: '#eab308',
        lineWidth: 1,
        lineStyle: LineStyle.Dotted,
        title: 'JNSAR'
      });

      // 3. MACD Series (Pane 2)
      this.macdHistogramSeries = this.chart.addSeries(HistogramSeries, {
        priceScaleId: 'macd',
        title: 'MACD Hist'
      });
      this.macdLineSeries = this.chart.addSeries(LineSeries, {
        color: '#3b82f6', // Blue MACD line
        lineWidth: 2,
        priceScaleId: 'macd',
        title: 'MACD'
      });
      this.macdSignalSeries = this.chart.addSeries(LineSeries, {
        color: '#f97316', // Orange Signal line
        lineWidth: 1,
        priceScaleId: 'macd',
        title: 'Signal'
      });

      // Configure MACD scale to occupy bottom 25% of chart
      this.chart.priceScale('macd').applyOptions({
        scaleMargins: {
          top: 0.75, // 75% spacing from top
          bottom: 0,
        },
      });

      // Compress main scale slightly so they don't overlap as much
      this.chart.priceScale('right').applyOptions({
        scaleMargins: {
          top: 0.05,
          bottom: 0.30,
        }
      });
    }

    this.updateData();

    // 3. Make Responsive
    this.resizeObserver = new ResizeObserver(entries => {
      if (entries.length === 0 || !this.chart) return;
      const { width, height } = entries[0].contentRect;
      this.chart.resize(width, height || 400);
    });
    this.resizeObserver.observe(this.chartContainer.nativeElement);
  }

  private updateData() {
    if (!this.chart || !this.data || this.data.length === 0) return;

    if (this.chartMode === 'equity') {
      const areaData = this.data.map(d => ({
        time: d.date,
        value: d.close
      }));
      this.equitySeries.setData(areaData);
    } else {
      // Map candles
      const candles = this.data.map(c => ({
        time: c.date,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close
      }));
      this.candlestickSeries.setData(candles);

      // Map EMA8
      const ema8Data = this.data
        .filter(c => c.ema8 !== null && c.ema8 !== undefined)
        .map(c => ({ time: c.date, value: c.ema8! }));
      this.ema8Series.setData(ema8Data);

      // Map EMA21
      const ema21Data = this.data
        .filter(c => c.ema21 !== null && c.ema21 !== undefined)
        .map(c => ({ time: c.date, value: c.ema21! }));
      this.ema21Series.setData(ema21Data);

      // Map EMA200
      const ema200Data = this.data
        .filter(c => c.ema200 !== null && c.ema200 !== undefined)
        .map(c => ({ time: c.date, value: c.ema200! }));
      this.ema200Series.setData(ema200Data);

      // Map JNSAR
      const jnsarData = this.data
        .filter(c => c.jnsar !== null && c.jnsar !== undefined)
        .map(c => ({ time: c.date, value: c.jnsar! }));
      this.jnsarSeries.setData(jnsarData);

      // Map MACD Line
      const macdLineData = this.data
        .filter(c => c.macdLine !== null && c.macdLine !== undefined)
        .map(c => ({ time: c.date, value: c.macdLine! }));
      this.macdLineSeries.setData(macdLineData);

      // Map MACD Signal
      const macdSignalData = this.data
        .filter(c => c.macdSignal !== null && c.macdSignal !== undefined)
        .map(c => ({ time: c.date, value: c.macdSignal! }));
      this.macdSignalSeries.setData(macdSignalData);

      // Map MACD Histogram
      const macdHistData = this.data
        .filter(c => c.macdHistogram !== null && c.macdHistogram !== undefined)
        .map(c => ({ 
          time: c.date, 
          value: c.macdHistogram!,
          color: c.macdHistogram! > 0 ? (this.data.indexOf(c) > 0 && c.macdHistogram! > this.data[this.data.indexOf(c)-1].macdHistogram! ? '#10b981' : '#34d399') : (this.data.indexOf(c) > 0 && c.macdHistogram! < this.data[this.data.indexOf(c)-1].macdHistogram! ? '#ef4444' : '#f87171')
        }));
      this.macdHistogramSeries.setData(macdHistData);

      // Remove any existing price lines from previous stocks
      this.candlestickSeries.clearPriceLines();

      // 4. Add Dynamic 61.8% Fibonacci Pullback line if available
      const lastCandle = this.data[this.data.length - 1];
      if (lastCandle && lastCandle.fib618) {
        this.candlestickSeries.createPriceLine({
          price: lastCandle.fib618,
          color: '#14b8a6',
          lineWidth: 2,
          lineStyle: LineStyle.Dashed,
          axisLabelVisible: true,
          title: 'Dynamic Fib 61.8%'
        });
      }

      this.updateMarkers();
    }

    // Fit chart content
    this.chart.timeScale().fitContent();
  }

  private updateMarkers() {
    if (!this.candlestickSeries) return;
    if (this.markers && this.markers.length > 0) {
      this.candlestickSeries.setMarkers(this.markers);
    } else {
      this.candlestickSeries.setMarkers([]);
    }
  }
}