export interface StockScanResult {
  ticker: string;
  name: string;
  sector: string;
  price: number;
  score: number;
  isHctMatch: boolean;
  isLrhrMatch: boolean;
  strategy: string;

  // Technical Indicators
  jnsar: number;
  distanceToJnsar: number;
  ema200: number;
  ema50: number;
  fib618: number;
  atr14: number;
  isVolatilityCoiled: boolean;
  proximityTo52WHigh: number;
  volumeScore: number;

  // Fundamentals
  epsGrowthYoY: number;
  debtToEquity: number;

  // Levels
  stopLoss: number;
  target1: number;
  target2: number;

  // Score breakdown
  trendScore: number;
  relativeStrengthScore: number;
  proximityScore: number;
  volumeAccumulationScore: number;
  volatilitySetupScore: number;
  fundamentalsScore: number;
  institutionalFootprintScore: number;
}

export interface ScanResponse {
  marketRegime: string; // BULLISH or BEARISH
  indexClose: number;
  indexEma200: number;
  results: StockScanResult[];
}

export interface ChartCandle {
  date: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  ema8?: number;
  ema21?: number;
  ema200?: number;
  jnsar?: number;
  fib618?: number;
}

export interface HistoricalChartResponse {
  ticker: string;
  candles: ChartCandle[];
}

export interface WatchlistItem {
  ticker: string;
  entryPrice: number;
  addedAt: string;
}

export interface SyncStatus {
  isRunning: boolean;
  currentTicker: string;
  progressPercent: number;
  message: string;
}
