using System;
using System.Linq;
using backend.Models;

namespace backend.Strategies
{
    public class JustNiftyLrhrStrategy : IStrategy
    {
        public string Name => "JustNifty LRHR";

        public bool Evaluate(StrategyContext context)
        {
            // JustNifty LRHR: Pullback to Fib retracement levels
            double currentPrice = context.CurrentPrice;
            double ema200 = context.Ema200;
            
            // Simplified proxy: price is near but above 200 EMA
            return currentPrice > ema200 && currentPrice < ema200 * 1.05;
        }

        public string GetLogic(StrategyContext context)
        {
            return "JustNifty LRHR: Price retracing to 200 EMA support.";
        }

        public bool ShouldExit(StrategyContext context, BacktestTrade openTrade)
        {
            return context.CurrentPrice < context.Ema200;
        }
    }
}
