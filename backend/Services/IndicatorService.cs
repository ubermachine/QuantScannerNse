using System;
using System.Collections.Generic;
using System.Linq;
using backend.Models;

namespace backend.Services
{
    public class IndicatorService
    {
        public double[] CalculateEma(double[] values, int period)
        {
            double[] ema = new double[values.Length];
            if (values.Length == 0) return ema;

            double k = 2.0 / (period + 1);
            ema[0] = values[0];

            for (int i = 1; i < values.Length; i++)
            {
                ema[i] = (values[i] * k) + (ema[i - 1] * (1.0 - k));
            }
            return ema;
        }

        public double[] CalculateJnsar(double[] closes, double[] highs, double[] lows)
        {
            int length = closes.Length;
            double[] jnsar = new double[length];
            if (length < 15) return jnsar;

            // 1. Calculate 5-period EMAs
            double[] hema5 = CalculateEma(highs, 5);
            double[] lema5 = CalculateEma(lows, 5);
            double[] cema5 = CalculateEma(closes, 5);

            // 2. 5-period rolling sum of all three divided by 15
            for (int i = 4; i < length; i++)
            {
                double hema5Sum = 0;
                double lema5Sum = 0;
                double cema5Sum = 0;

                for (int j = 0; j < 5; j++)
                {
                    hema5Sum += hema5[i - j];
                    lema5Sum += lema5[i - j];
                    cema5Sum += cema5[i - j];
                }

                jnsar[i] = Math.Round((hema5Sum + lema5Sum + cema5Sum) / 15.0, 2);
            }

            // Fill initial values
            for (int i = 0; i < 4; i++)
            {
                jnsar[i] = closes[i];
            }

            return jnsar;
        }

        public double[] CalculateAtr(double[] highs, double[] lows, double[] closes, int period = 14)
        {
            int length = closes.Length;
            double[] atr = new double[length];
            if (length < 2) return atr;

            double[] tr = new double[length];
            tr[0] = highs[0] - lows[0];

            for (int i = 1; i < length; i++)
            {
                double hMinusL = highs[i] - lows[i];
                double hMinusPrevC = Math.Abs(highs[i] - closes[i - 1]);
                double lMinusPrevC = Math.Abs(lows[i] - closes[i - 1]);
                tr[i] = Math.Max(hMinusL, Math.Max(hMinusPrevC, lMinusPrevC));
            }

            // Initialize ATR with simple moving average of TR
            if (length < period) return atr;
            
            double sum = 0;
            for (int i = 0; i < period; i++) sum += tr[i];
            atr[period - 1] = sum / period;

            for (int i = period; i < length; i++)
            {
                atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
            }

            return atr;
        }

        public double CalculateFib618(double[] closes, double[] highs, double[] lows, out double lastSwingHigh, out double lastSwingLow)
        {
            lastSwingHigh = 0;
            lastSwingLow = 0;
            if (closes.Length < 10) return 0;

            double currentSwingHigh = highs[0];
            double currentSwingLow = lows[0];
            string state = "UP"; // UP or DOWN

            var swingHighs = new List<double>();
            var swingLows = new List<double>();

            for (int i = 1; i < closes.Length; i++)
            {
                if (state == "UP")
                {
                    if (highs[i] > currentSwingHigh)
                    {
                        currentSwingHigh = highs[i];
                    }
                    else if (closes[i] < currentSwingHigh * 0.95) // 5% pullback
                    {
                        swingHighs.Add(currentSwingHigh);
                        state = "DOWN";
                        currentSwingLow = lows[i];
                    }
                }
                else // DOWN
                {
                    if (lows[i] < currentSwingLow)
                    {
                        currentSwingLow = lows[i];
                    }
                    else if (closes[i] > currentSwingLow * 1.05) // 5% bounce
                    {
                        swingLows.Add(currentSwingLow);
                        state = "UP";
                        currentSwingHigh = highs[i];
                    }
                }
            }

            // Fallbacks if no swings locked in
            if (swingHighs.Count == 0) lastSwingHigh = highs.Max();
            else lastSwingHigh = swingHighs[^1];

            if (swingLows.Count == 0) lastSwingLow = lows.Min();
            else lastSwingLow = swingLows[^1];

            if (lastSwingHigh <= lastSwingLow)
            {
                lastSwingHigh = highs.Max();
                lastSwingLow = lows.Min();
            }

            return lastSwingHigh - 0.618 * (lastSwingHigh - lastSwingLow);
        }

        public int CalculateVolumeScore(double[] closes, double[] volumes, int lookback = 15)
        {
            if (closes.Length < lookback + 1) return 0;

            double upVolumeSum = 0;
            double downVolumeSum = 0;

            int len = closes.Length;
            for (int i = len - lookback; i < len; i++)
            {
                if (closes[i] > closes[i - 1])
                {
                    upVolumeSum += volumes[i];
                }
                else if (closes[i] < closes[i - 1])
                {
                    downVolumeSum += volumes[i];
                }
            }

            if (upVolumeSum + downVolumeSum == 0) return 5;
            
            // Normalize to 0-10 scale
            double ratio = upVolumeSum / (upVolumeSum + downVolumeSum);
            return (int)Math.Round(ratio * 10);
        }

        public double CalculateRelativeStrength(double stockReturn3M, double indexReturn3M, double stockReturn6M, double indexReturn6M)
        {
            // Simple outperformance measure: sum of return differentials
            double diff3M = stockReturn3M - indexReturn3M;
            double diff6M = stockReturn6M - indexReturn6M;
            return diff3M + diff6M;
        }

        public double CalculateReturn(double[] closes, int daysAgo)
        {
            if (closes.Length < daysAgo + 1) return 0;
            double currentClose = closes[^1];
            double oldClose = closes[closes.Length - 1 - daysAgo];
            return ((currentClose - oldClose) / oldClose) * 100.0;
        }

        public (double Target1, double Target2, double StopLoss) CalculateVolatilityFibTargets(double[] closes)
        {
            if (closes.Length < 11) return (closes[^1] * 1.05, closes[^1] * 1.10, closes[^1] * 0.95);

            int len = closes.Length;
            var logReturns = new List<double>();

            for (int i = len - 10; i < len; i++)
            {
                logReturns.Add(Math.Log(closes[i] / closes[i - 1]));
            }

            double mean = logReturns.Average();
            double sumSqDiff = logReturns.Sum(r => Math.Pow(r - mean, 2));
            double variance = sumSqDiff / (logReturns.Count - 1);
            double dailyVolatility = Math.Sqrt(variance);

            // Projected 10-day range factor
            double projectedRange = closes[^1] * dailyVolatility * Math.Sqrt(10);

            double currentPrice = closes[^1];
            double target1 = Math.Round(currentPrice + 0.382 * projectedRange, 2);
            double target2 = Math.Round(currentPrice + 0.618 * projectedRange, 2);
            double stopLoss = Math.Round(currentPrice - 0.618 * projectedRange, 2);

            return (target1, target2, stopLoss);
        }

        public (double[] MacdLine, double[] SignalLine) CalculateWeeklyMacd(double[] closes)
        {
            double[] ema12 = CalculateEma(closes, 12);
            double[] ema26 = CalculateEma(closes, 26);
            
            double[] macdLine = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++)
            {
                macdLine[i] = ema12[i] - ema26[i];
            }

            double[] signalLine = CalculateEma(macdLine, 9);
            return (macdLine, signalLine);
        }
    }
}
