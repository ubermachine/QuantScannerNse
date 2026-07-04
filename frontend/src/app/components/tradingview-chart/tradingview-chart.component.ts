import { Component, ElementRef, Input, OnChanges, OnDestroy, OnInit, SimpleChanges, ViewChild } from '@angular/core';
import { createChart, IChartApi, ISeriesApi, LineStyle, CandlestickSeries, LineSeries, HistogramSeries } from 'lightweight-charts';
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

  private chart: IChartApi | null = null;
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
    if (changes['data'] && !changes['data'].firstChange) {
      this.updateData();
    }
  }

  ngOnDestroy() {
    if (this.resizeObserver) {
      this.resizeObserver.disconnect();
    }
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
        mode: 1,
        vertLine: { color: '#3b82f6', width: 1, style: LineStyle.Dashed },
        horzLine: { color: '#3b82f6', width: 1, style: LineStyle.Dashed }
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

    // ── STEP 1: Add ALL series first before any applyOptions calls ──

    // Candlestick Series
    this.candlestickSeries = this.chart.addSeries(CandlestickSeries, {
      upColor: '#10b981',
      downColor: '#ef4444',
      borderVisible: false,
      wickUpColor: '#10b981',
      wickDownColor: '#ef4444'
    });

    // Overlay Lines (share the main right price scale)
    this.ema8Series = this.chart.addSeries(LineSeries, {
      color: '#3b82f6', lineWidth: 2, title: 'EMA 8'
    });
    this.ema21Series = this.chart.addSeries(LineSeries, {
      color: '#f97316', lineWidth: 2, title: 'EMA 21'
    });
    this.ema200Series = this.chart.addSeries(LineSeries, {
      color: '#8b5cf6', lineWidth: 3, title: 'EMA 200'
    });
    this.jnsarSeries = this.chart.addSeries(LineSeries, {
      color: '#eab308', lineWidth: 1, lineStyle: LineStyle.Dotted, title: 'JNSAR'
    });

    // MACD pane — all three series share 'macd' price scale
    this.macdHistogramSeries = this.chart.addSeries(HistogramSeries, {
      priceScaleId: 'macd',
      title: 'MACD Hist'
    });
    this.macdLineSeries = this.chart.addSeries(LineSeries, {
      color: '#3b82f6', lineWidth: 2, priceScaleId: 'macd', title: 'MACD'
    });
    this.macdSignalSeries = this.chart.addSeries(LineSeries, {
      color: '#f97316', lineWidth: 1, priceScaleId: 'macd', title: 'Signal'
    });

    // ── STEP 2: Apply scale options AFTER all series are attached ──
    this.chart.priceScale('macd').applyOptions({
      scaleMargins: { top: 0.75, bottom: 0 }
    });
    this.chart.priceScale('right').applyOptions({
      scaleMargins: { top: 0.05, bottom: 0.30 }
    });

    this.updateData();

    // Responsive resize
    this.resizeObserver = new ResizeObserver(entries => {
      if (entries.length === 0 || !this.chart) return;
      const { width, height } = entries[0].contentRect;
      this.chart.resize(width, height || 400);
    });
    this.resizeObserver.observe(this.chartContainer.nativeElement);
  }

  private updateData() {
    if (!this.chart || !this.data || this.data.length === 0) return;

    // Map candles
    const candles = this.data.map(c => ({
      time: c.date, open: c.open, high: c.high, low: c.low, close: c.close
    }));
    this.candlestickSeries.setData(candles);

    // EMA overlays
    this.ema8Series.setData(this.data.filter(c => c.ema8 != null).map(c => ({ time: c.date, value: c.ema8! })));
    this.ema21Series.setData(this.data.filter(c => c.ema21 != null).map(c => ({ time: c.date, value: c.ema21! })));
    this.ema200Series.setData(this.data.filter(c => c.ema200 != null).map(c => ({ time: c.date, value: c.ema200! })));
    this.jnsarSeries.setData(this.data.filter(c => c.jnsar != null).map(c => ({ time: c.date, value: c.jnsar! })));

    // MACD — fix O(n²) indexOf bug by using index directly
    this.macdLineSeries.setData(this.data.filter(c => c.macdLine != null).map(c => ({ time: c.date, value: c.macdLine! })));
    this.macdSignalSeries.setData(this.data.filter(c => c.macdSignal != null).map(c => ({ time: c.date, value: c.macdSignal! })));
    this.macdHistogramSeries.setData(
      this.data
        .filter(c => c.macdHistogram != null)
        .map((c, idx, arr) => ({
          time: c.date,
          value: c.macdHistogram!,
          color: c.macdHistogram! >= 0
            ? (idx > 0 && c.macdHistogram! > arr[idx - 1].macdHistogram! ? '#10b981' : '#34d399')
            : (idx > 0 && c.macdHistogram! < arr[idx - 1].macdHistogram! ? '#ef4444' : '#f87171')
        }))
    );

    // Fibonacci level price line
    this.candlestickSeries.clearPriceLines();
    const lastCandle = this.data[this.data.length - 1];
    if (lastCandle?.fib618) {
      this.candlestickSeries.createPriceLine({
        price: lastCandle.fib618,
        color: '#14b8a6',
        lineWidth: 2,
        lineStyle: LineStyle.Dashed,
        axisLabelVisible: true,
        title: 'Fib 61.8%'
      });
    }

    this.chart.timeScale().fitContent();
  }
}
