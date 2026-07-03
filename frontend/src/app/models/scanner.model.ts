export interface StockScanResult {
  ticker: string;
  name: string;
  sector: string;
  price: number;
  score: number;
  isHctMatch: boolean;
  isLrhrMatch: boolean;
  strategy: string;
  conviction: string;
  logic: string;

  // Technical Indicators
  jnsar: number;
  distanceToJnsar: number;
  ema200: number;
  ema50: number;
  fib618: number;
  atr14: number;
  isVolatilityCoiled: boolean;
  isSqueezeFiring: boolean;
  rsi14: number;
  adx14: number;
  zScore: number;
  proximityTo52WHigh: number;
  volumeScore: number;
  
  pointOfControl: number;
  ytdVwap: number;
  chandelierExit: number;

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

  // FYERS Options Flow
  fyersOptionsFlow?: FyersOptionsFlowData;
}

export interface FyersOptionsFlowData {
  pcr: number;
  skew: number;
  squeezeStatus: string;
  needsLogin: boolean;
  loginUrl?: string;
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
  jnsar: number | null;
  fib618: number | null;
  macdLine: number | null;
  macdSignal: number | null;
  macdHistogram: number | null;
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
