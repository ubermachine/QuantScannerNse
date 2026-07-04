using System;
using System.Linq;
using backend.Models;

namespace backend.Strategies
{
    public class JustNiftyHctStrategy : IStrategy
    {
        public string Name => "JustNifty HCT";

        public bool Evaluate(StrategyContext context)
        {
            // JustNifty HCT: Price > 200 EMA, and short term EMA crossover
            double currentPrice = context.CurrentPrice;
            double ema200 = context.Ema200;
            double ema21 = context.Ema21; // Using 21 EMA
            
            return currentPrice > ema200 && currentPrice > ema21;
        }

        public string GetLogic(StrategyContext context)
        {
            return "JustNifty HCT: Price above 200 EMA and short-term EMA.";
        }

        public bool ShouldExit(StrategyContext context, BacktestTrade openTrade)
        {
            return context.CurrentPrice < context.Ema21;
        }
    }
}
