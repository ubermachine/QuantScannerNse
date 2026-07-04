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
  obv: number;
  cmf: number;
  volatilityPctRank: number;
  rsSharpe: number;
  rsPercentileRank: number;

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

export interface PortfolioRequest {
  startingCapital: number;
  maxPositions: number;
  sizingModel: string;
  riskPerTradePercent: number;
  positionSizePercent: number;
  transactionCostPercent: number;
  slippagePercent: number;
  strategy: string;
  minScore: number;
  startDate?: string;
  endDate?: string;
}

export interface PortfolioTrade {
  ticker: string;
  entryDate: string;
  entryPrice: number;
  exitDate: string;
  exitPrice: number;
  shares: number;
  profit: number;
  profitPercent: number;
  exitReason: string;
}

export interface EquityCurvePoint {
  date: string;
  balance: number;
  drawdownPercent: number;
}

export interface PortfolioSimulationResult {
  startingCapital: number;
  endingCapital: number;
  totalProfit: number;
  returnPercent: number;
  sharpeRatio: number;
  maxDrawdownPercent: number;
  profitFactor: number;
  winRate: number;
  totalTrades: number;
  winningTrades: number;
  losingTrades: number;
  trades: PortfolioTrade[];
  equityCurve: EquityCurvePoint[];
}

// Multi-strategy comparison response
export interface MultiStrategySimulationResult {
  strategies: StrategySimLine[];
  startingCapital: number;
}

export interface StrategySimLine {
  strategyName: string;
  equityCurve: EquityCurvePoint[];
  summary: PortfolioSimulationResult;
}

// Sector screener types
export interface QuadrantSnapshot {
  date: string;
  quadrant: string;
}

export interface SectorRRGPoint {
  ticker: string;
  name: string;
  rsRatio: number;
  rsMomentum: number;
  quadrant: string;
  price: number;
  priceChangePct: number;
  isNewImproving: boolean;
  isNewWeakening: boolean;
  history: QuadrantSnapshot[];
}

export interface RotationSuggestion {
  sector: string;
  action: string;
  from: string;
  to: string;
  daysSinceChange: number;
  reason: string;
}

export interface SectorRotationResult {
  sectors: SectorRRGPoint[];
  suggestions: RotationSuggestion[];
  rotationActive: boolean;
  lastUpdated: string;
}

export interface RotationBacktestTrade {
  sector: string;
  signal: string;
  date: string;
  price: number;
  returnPct: number;
  daysHeld: number;
}

export interface RotationBacktestResult {
  startingCapital: number;
  endingCapital: number;
  totalReturn: number;
  returnPercent: number;
  niftyReturn: number;
  maxDrawdown: number;
  totalTrades: number;
  wins: number;
  losses: number;
  winRate: number;
  trades: RotationBacktestTrade[];
  equityCurve: EquityCurvePoint[];
  niftyCurve: EquityCurvePoint[];
}