using backend.Models;

namespace backend.Strategies
{
    public class HctStrategy : IStrategy
    {
        public string Name => "Quant HCT Pullback";

        public bool Evaluate(StrategyContext context)
        {
            int last = context.Obv.Length - 1;
            
            // Wait for 20 days for OBV trend
            if (last < 20) return false;

            return (context.CurrentPrice >= context.YtdVwap * 0.97 && context.CurrentPrice <= context.YtdVwap * 1.05 ||
                    context.CurrentPrice >= context.Poc * 0.97 && context.CurrentPrice <= context.Poc * 1.05) &&
                   context.Obv[last] > context.Obv[last - 20] &&
                   context.Cmf[last] > 0 &&
                   context.VolPctRank < 30 &&
                   context.RsRank > 80 &&
                   context.ZScore >= -1.0 && context.ZScore <= 0.5 &&
                   context.CurrentPrice > context.ChandelierExit;
        }

        public string GetLogic(StrategyContext context)
        {
            return "HCT (Institutional Pullback): Price near VWAP/POC, Positive CMF, High RS, Volatility Compressed. | ";
        }

        public bool ShouldExit(StrategyContext context, BacktestTrade openTrade)
        {
            return context.CurrentPrice < context.ChandelierExit;
        }
    }
}
