using backend.Models;

namespace backend.Strategies
{
    public class LrhrStrategy : IStrategy
    {
        public string Name => "Quant LRHR Base";

        public bool Evaluate(StrategyContext context)
        {
            int last = context.Obv.Length - 1;
            
            if (last < 10) return false;

            return context.ZScore < -1.0 &&
                   context.Discount52W >= 0.25 &&
                   context.Cmf[last] > 0 && context.Cmf[last - 5] <= 0 &&
                   context.Obv[last] > context.Obv[last - 10] &&
                   context.VolPctRank < 50 &&
                   context.CurrentPrice > context.Poc;
        }

        public string GetLogic(StrategyContext context)
        {
            return "LRHR (Institutional Base): Deep Value (Z < -1, >25% pullback), Institutional Accumulation (CMF/OBV Inflection). | ";
        }

        public bool ShouldExit(StrategyContext context, BacktestTrade openTrade)
        {
            return context.CurrentPrice < context.Poc;
        }
    }
}
