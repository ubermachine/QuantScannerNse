using backend.Models;

namespace backend.Strategies
{
    public struct StrategyContext
    {
        public double CurrentPrice { get; set; }
        public double YtdVwap { get; set; }
        public double Poc { get; set; }
        public double[] Obv { get; set; }
        public double[] Cmf { get; set; }
        public double VolPctRank { get; set; }
        public double RsRank { get; set; }
        public double RsSharpe { get; set; }
        public double ZScore { get; set; }
        public double ChandelierExit { get; set; }
        public double Discount52W { get; set; }

        // Added for JustNifty Strategy
        public double Ema8 { get; set; }
        public double Ema10 { get; set; }
        public double Ema21 { get; set; }
        public double Ema50 { get; set; }
        public double Ema200 { get; set; }
        public double Rsi14 { get; set; }
        public double MacdLine { get; set; }
        public double MacdSignal { get; set; }
        public double Jnsar { get; set; }
    }

    public interface IStrategy
    {
        string Name { get; }
        bool Evaluate(StrategyContext context);
        string GetLogic(StrategyContext context);
        bool ShouldExit(StrategyContext context, BacktestTrade openTrade);
    }
}
