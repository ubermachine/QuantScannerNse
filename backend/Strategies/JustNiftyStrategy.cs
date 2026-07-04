using System;
using backend.Models;

namespace backend.Strategies
{
    public class JustNiftyStrategy : IStrategy
    {
        public string Name => "JustNifty Positional";

        public bool Evaluate(StrategyContext context)
        {
            // 1. 200 EMA Major Bias: Price must be above 200 EMA for a long positional trade.
            if (context.CurrentPrice <= context.Ema200) return false;

            // 2. 8 & 21 MA Crossover: 8 EMA should be above 21 EMA (bullish momentum).
            if (context.Ema8 <= context.Ema21) return false;

            // 3. J10SAR Base (10-Day EMA): Price should be above 10-Day EMA.
            if (context.CurrentPrice <= context.Ema10) return false;

            // 4. Trend Indicator Confirmation: MACD Line should be above Signal Line.
            if (context.MacdLine <= context.MacdSignal) return false;

            return true;
        }

        public string GetLogic(StrategyContext context)
        {
            // J10SAR Envelope upper band for part booking (10-Day EMA + 2.5%)
            double envelopeUpper = Math.Round(context.Ema10 * 1.025, 2);

            return $"JustNifty Positional: Bullish Bias (Price > 200EMA). " +
                   $"8 & 21 EMA in positive crossover. " +
                   $"Price is above J10SAR (10 EMA). " +
                   $"MACD is bullish. " +
                   $"Target Envelope Upper Band (2.5%) at {envelopeUpper} for part booking.";
        }

        public bool ShouldExit(StrategyContext context, BacktestTrade openTrade)
        {
            return context.CurrentPrice < context.Ema10 || context.CurrentPrice < context.Ema21;
        }
    }
}
