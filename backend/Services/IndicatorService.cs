using System;
using System.Collections.Generic;
using System.Linq;
using backend.Models;

namespace backend.Services
{
    public class IndicatorService
    {
        // ─── EMA ────────────────────────────────────────────────────────────────────
        // Returns only the final EMA value – avoids allocating a full array when only
        // the last value is needed.
        public double CalculateEmaLast(double[] values, int period)
        {
            if (values.Length == 0) return 0;
            double k = 2.0 / (period + 1);
            double ema = values[0];
            for (int i = 1; i < values.Length; i++)
                ema = values[i] * k + ema * (1.0 - k);
            return ema;
        }

        // Full EMA array (used when every value is needed, e.g. MACD signal, JNSAR)
        public double[] CalculateEma(double[] values, int period)
        {
            double[] ema = new double[values.Length];
            if (values.Length == 0) return ema;
            double k = 2.0 / (period + 1);
            ema[0] = values[0];
            for (int i = 1; i < values.Length; i++)
                ema[i] = values[i] * k + ema[i - 1] * (1.0 - k);
            return ema;
        }

        // ─── MULTI-EMA (compute several EMAs in ONE pass over the data) ─────────────
        // Computes EMA-8, 10, 21, 50, 200 in a single loop – huge savings for 1500+ stocks.
        public void CalculateMultiEmaLast(double[] values,
            out double ema8, out double ema10, out double ema21,
            out double ema50, out double ema200)
        {
            const double k8   = 2.0 / (8   + 1);
            const double k10  = 2.0 / (10  + 1);
            const double k21  = 2.0 / (21  + 1);
            const double k50  = 2.0 / (50  + 1);
            const double k200 = 2.0 / (200 + 1);

            ema8 = ema10 = ema21 = ema50 = ema200 = values.Length > 0 ? values[0] : 0;

            for (int i = 1; i < values.Length; i++)
            {
                double v = values[i];
                ema8   = v * k8   + ema8   * (1 - k8);
                ema10  = v * k10  + ema10  * (1 - k10);
                ema21  = v * k21  + ema21  * (1 - k21);
                ema50  = v * k50  + ema50  * (1 - k50);
                ema200 = v * k200 + ema200 * (1 - k200);
            }
        }

        // ─── JNSAR ──────────────────────────────────────────────────────────────────
        // Optimised: uses running sums instead of re-scanning a 5-element window each bar.
        public double[] CalculateJnsar(double[] closes, double[] highs, double[] lows)
        {
            int length = closes.Length;
            double[] jnsar = new double[length];
            if (length < 15) return jnsar;

            double[] hema5 = CalculateEma(highs, 5);
            double[] lema5 = CalculateEma(lows,  5);
            double[] cema5 = CalculateEma(closes, 5);

            // Prime the rolling window sums with indices 0-3
            double hSum = 0, lSum = 0, cSum = 0;
            for (int j = 0; j < 4; j++) { hSum += hema5[j]; lSum += lema5[j]; cSum += cema5[j]; }

            for (int i = 4; i < length; i++)
            {
                hSum += hema5[i]; lSum += lema5[i]; cSum += cema5[i];
                jnsar[i] = Math.Round((hSum + lSum + cSum) / 15.0, 2);
                hSum -= hema5[i - 4]; lSum -= lema5[i - 4]; cSum -= cema5[i - 4];
            }

            for (int i = 0; i < 4; i++) jnsar[i] = closes[i];
            return jnsar;
        }

        // Returns only the last JNSAR value (avoids full array when only tail is needed)
        public double CalculateJnsarLast(double[] closes, double[] highs, double[] lows)
        {
            int length = closes.Length;
            if (length < 15) return closes[^1];

            double[] hema5 = CalculateEma(highs,  5);
            double[] lema5 = CalculateEma(lows,   5);
            double[] cema5 = CalculateEma(closes, 5);

            double hSum = 0, lSum = 0, cSum = 0;
            for (int j = 0; j < 4; j++) { hSum += hema5[j]; lSum += lema5[j]; cSum += cema5[j]; }

            double result = closes[0];
            for (int i = 4; i < length; i++)
            {
                hSum += hema5[i]; lSum += lema5[i]; cSum += cema5[i];
                result = Math.Round((hSum + lSum + cSum) / 15.0, 2);
                hSum -= hema5[i - 4]; lSum -= lema5[i - 4]; cSum -= cema5[i - 4];
            }
            return result;
        }

        // ─── ATR ─────────────────────────────────────────────────────────────────────
        // Inlined TR computation, no extra array allocation.
        // Returns last value only (caller only needs that + avg of last 60).
        public (double AtrLast, double AtrAvg60) CalculateAtrLastAndAvg60(double[] highs, double[] lows, double[] closes, int period = 14)
        {
            int length = closes.Length;
            if (length < period + 1) return (0, 0);

            // Compute full running ATR in-place to get the last value + rolling 60 avg
            // We only need the last 60 ATR values for the average.
            // Keep a circular buffer of size 60.
            const int avgWindow = 60;
            double atr = 0;

            // Bootstrap first TR
            double tr0 = highs[0] - lows[0];
            double sum = tr0;
            for (int i = 1; i < period; i++)
            {
                double tr = Math.Max(highs[i] - lows[i],
                            Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                                     Math.Abs(lows[i]  - closes[i - 1])));
                sum += tr;
            }
            atr = sum / period;

            // Running circular buffer for avg60
            double[] buf = new double[avgWindow];
            int bufCount = 0;
            int bufIdx = 0;
            double bufSum = 0;

            void pushAvgBuf(double val)
            {
                if (bufCount < avgWindow)
                {
                    buf[bufIdx] = val;
                    bufIdx = (bufIdx + 1) % avgWindow;
                    bufSum += val;
                    bufCount++;
                }
                else
                {
                    bufSum -= buf[bufIdx];
                    buf[bufIdx] = val;
                    bufIdx = (bufIdx + 1) % avgWindow;
                    bufSum += val;
                }
            }

            pushAvgBuf(atr);
            for (int i = period; i < length; i++)
            {
                double tr = Math.Max(highs[i] - lows[i],
                            Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                                     Math.Abs(lows[i]  - closes[i - 1])));
                atr = (atr * (period - 1) + tr) / period;
                pushAvgBuf(atr);
            }

            double avg60 = bufCount > 0 ? bufSum / bufCount : atr;
            return (atr, avg60);
        }

        // Full ATR array kept for callers that need it
        public double[] CalculateAtr(double[] highs, double[] lows, double[] closes, int period = 14)
        {
            int length = closes.Length;
            double[] atr = new double[length];
            if (length < 2) return atr;

            double[] tr = new double[length];
            tr[0] = highs[0] - lows[0];
            for (int i = 1; i < length; i++)
                tr[i] = Math.Max(highs[i] - lows[i],
                         Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                                  Math.Abs(lows[i]  - closes[i - 1])));

            if (length < period) return atr;
            double sum = 0;
            for (int i = 0; i < period; i++) sum += tr[i];
            atr[period - 1] = sum / period;
            for (int i = period; i < length; i++)
                atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;

            return atr;
        }

        // ─── 52-WEEK HIGH (no LINQ, no allocation) ───────────────────────────────────
        public double Max52WeekHigh(double[] highs)
        {
            int start = Math.Max(0, highs.Length - 250);
            double max = double.MinValue;
            for (int i = start; i < highs.Length; i++)
                if (highs[i] > max) max = highs[i];
            return max;
        }

        // ─── FIB 618 ─────────────────────────────────────────────────────────────────
        public double CalculateFib618(double[] closes, double[] highs, double[] lows,
            out double lastSwingHigh, out double lastSwingLow)
        {
            lastSwingHigh = 0;
            lastSwingLow  = 0;
            if (closes.Length < 10) return 0;

            double currentSwingHigh = highs[0];
            double currentSwingLow  = lows[0];
            string state = "UP";

            double recordedSwingHigh = 0;
            double recordedSwingLow  = 0;

            for (int i = 1; i < closes.Length; i++)
            {
                if (state == "UP")
                {
                    if (highs[i] > currentSwingHigh) currentSwingHigh = highs[i];
                    else if (closes[i] < currentSwingHigh * 0.95)
                    {
                        recordedSwingHigh = currentSwingHigh;
                        state = "DOWN";
                        currentSwingLow = lows[i];
                    }
                }
                else
                {
                    if (lows[i] < currentSwingLow) currentSwingLow = lows[i];
                    else if (closes[i] > currentSwingLow * 1.05)
                    {
                        recordedSwingLow = currentSwingLow;
                        state = "UP";
                        currentSwingHigh = highs[i];
                    }
                }
            }

            // Fallbacks – single-pass min/max instead of LINQ
            if (recordedSwingHigh == 0)
            {
                double m = double.MinValue;
                foreach (var h in highs) if (h > m) m = h;
                lastSwingHigh = m;
            }
            else lastSwingHigh = recordedSwingHigh;

            if (recordedSwingLow == 0)
            {
                double m = double.MaxValue;
                foreach (var l in lows) if (l < m) m = l;
                lastSwingLow = m;
            }
            else lastSwingLow = recordedSwingLow;

            if (lastSwingHigh <= lastSwingLow)
            {
                double maxH = double.MinValue, minL = double.MaxValue;
                for (int i = 0; i < highs.Length; i++)
                {
                    if (highs[i] > maxH) maxH = highs[i];
                    if (lows[i]  < minL) minL = lows[i];
                }
                lastSwingHigh = maxH;
                lastSwingLow  = minL;
            }

            return lastSwingHigh - 0.618 * (lastSwingHigh - lastSwingLow);
        }

        // ─── VOLUME SCORE ────────────────────────────────────────────────────────────
        public int CalculateVolumeScore(double[] closes, double[] volumes, int lookback = 15)
        {
            if (closes.Length < lookback + 1) return 0;
            double upVolumeSum = 0, downVolumeSum = 0;
            int len = closes.Length;
            for (int i = len - lookback; i < len; i++)
            {
                if      (closes[i] > closes[i - 1]) upVolumeSum   += volumes[i];
                else if (closes[i] < closes[i - 1]) downVolumeSum += volumes[i];
            }
            if (upVolumeSum + downVolumeSum == 0) return 5;
            return (int)Math.Round(upVolumeSum / (upVolumeSum + downVolumeSum) * 10);
        }

        // ─── RELATIVE STRENGTH ───────────────────────────────────────────────────────
        public double CalculateRelativeStrength(double stockReturn3M, double indexReturn3M,
            double stockReturn6M, double indexReturn6M)
            => (stockReturn3M - indexReturn3M) + (stockReturn6M - indexReturn6M);

        // ─── RETURN ──────────────────────────────────────────────────────────────────
        public double CalculateReturn(double[] closes, int daysAgo)
        {
            if (closes.Length < daysAgo + 1) return 0;
            double cur  = closes[^1];
            double old  = closes[closes.Length - 1 - daysAgo];
            return (cur - old) / old * 100.0;
        }

        // ─── VOLATILITY FIB TARGETS ──────────────────────────────────────────────────
        public (double Target1, double Target2, double StopLoss) CalculateVolatilityFibTargets(double[] closes)
        {
            if (closes.Length < 11)
                return (Math.Round(closes[^1] * 1.05, 2),
                        Math.Round(closes[^1] * 1.10, 2),
                        Math.Round(closes[^1] * 0.95, 2));

            int len  = closes.Length;
            double mean = 0;
            // compute log returns for last 10 bars
            double[] lr = new double[10];
            for (int i = 0; i < 10; i++)
                lr[i] = Math.Log(closes[len - 10 + i] / closes[len - 11 + i]);
            for (int i = 0; i < 10; i++) mean += lr[i];
            mean /= 10;
            double variance = 0;
            for (int i = 0; i < 10; i++) { double d = lr[i] - mean; variance += d * d; }
            variance /= 9;
            double dailyVol   = Math.Sqrt(variance);
            double projRange  = closes[^1] * dailyVol * Math.Sqrt(10);
            double cur        = closes[^1];
            return (Math.Round(cur + 0.382 * projRange, 2),
                    Math.Round(cur + 0.618 * projRange, 2),
                    Math.Round(cur - 0.618 * projRange, 2));
        }

        // ─── MACD ─────────────────────────────────────────────────────────────
        public (double[] MacdLine, double[] SignalLine) CalculateMacd(double[] closes)
        {
            double[] ema12 = CalculateEma(closes, 12);
            double[] ema26 = CalculateEma(closes, 26);
            double[] macd  = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++) macd[i] = ema12[i] - ema26[i];
            return (macd, CalculateEma(macd, 9));
        }

        // ─── RSI ──────────────────────────────────────────────────────────────
        public double[] CalculateRsi(double[] closes, int period = 14)
        {
            double[] rsi = new double[closes.Length];
            if (closes.Length <= period) return rsi;

            double avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                double change = closes[i] - closes[i - 1];
                if (change > 0) avgGain += change;
                else avgLoss += Math.Abs(change);
            }
            avgGain /= period;
            avgLoss /= period;
            rsi[period] = avgLoss == 0 ? 100 : 100 - (100 / (1 + (avgGain / avgLoss)));

            for (int i = period + 1; i < closes.Length; i++)
            {
                double change = closes[i] - closes[i - 1];
                double gain = change > 0 ? change : 0;
                double loss = change < 0 ? Math.Abs(change) : 0;

                avgGain = ((avgGain * (period - 1)) + gain) / period;
                avgLoss = ((avgLoss * (period - 1)) + loss) / period;

                rsi[i] = avgLoss == 0 ? 100 : 100 - (100 / (1 + (avgGain / avgLoss)));
            }
            return rsi;
        }

        // ─── ADX ──────────────────────────────────────────────────────────────
        public double[] CalculateAdx(double[] highs, double[] lows, double[] closes, int period = 14)
        {
            int length = closes.Length;
            double[] adx = new double[length];
            if (length <= period) return adx;

            double[] tr = new double[length];
            double[] plusDm = new double[length];
            double[] minusDm = new double[length];

            for (int i = 1; i < length; i++)
            {
                tr[i] = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
                double upMove = highs[i] - highs[i - 1];
                double downMove = lows[i - 1] - lows[i];
                if (upMove > downMove && upMove > 0) plusDm[i] = upMove;
                if (downMove > upMove && downMove > 0) minusDm[i] = downMove;
            }

            double smoothedTr = tr.Skip(1).Take(period).Sum();
            double smoothedPlusDm = plusDm.Skip(1).Take(period).Sum();
            double smoothedMinusDm = minusDm.Skip(1).Take(period).Sum();

            double[] dx = new double[length];
            for (int i = period; i < length; i++)
            {
                if (i > period)
                {
                    smoothedTr = smoothedTr - (smoothedTr / period) + tr[i];
                    smoothedPlusDm = smoothedPlusDm - (smoothedPlusDm / period) + plusDm[i];
                    smoothedMinusDm = smoothedMinusDm - (smoothedMinusDm / period) + minusDm[i];
                }

                double plusDi = 100 * (smoothedPlusDm / smoothedTr);
                double minusDi = 100 * (smoothedMinusDm / smoothedTr);
                
                double diDiff = Math.Abs(plusDi - minusDi);
                double diSum = plusDi + minusDi;
                dx[i] = diSum == 0 ? 0 : 100 * (diDiff / diSum);
            }

            double adxSum = dx.Skip(period).Take(period).Sum();
            adx[period * 2 - 1] = adxSum / period;

            for (int i = period * 2; i < length; i++)
            {
                adx[i] = ((adx[i - 1] * (period - 1)) + dx[i]) / period;
            }
            return adx;
        }

        // ─── BOLLINGER BANDS ───────────────────────────────────────────────────
        public (double[] Upper, double[] Middle, double[] Lower) CalculateBollingerBands(double[] closes, int period = 20, double multiplier = 2.0)
        {
            int length = closes.Length;
            double[] upper = new double[length];
            double[] middle = new double[length];
            double[] lower = new double[length];

            for (int i = period - 1; i < length; i++)
            {
                double sum = 0;
                for (int j = 0; j < period; j++) sum += closes[i - j];
                double mean = sum / period;
                
                double variance = 0;
                for (int j = 0; j < period; j++) variance += Math.Pow(closes[i - j] - mean, 2);
                double stdDev = Math.Sqrt(variance / period);

                middle[i] = mean;
                upper[i] = mean + (multiplier * stdDev);
                lower[i] = mean - (multiplier * stdDev);
            }
            return (upper, middle, lower);
        }

        // ─── KELTNER CHANNELS ──────────────────────────────────────────────────
        public (double[] Upper, double[] Middle, double[] Lower) CalculateKeltnerChannels(double[] highs, double[] lows, double[] closes, int period = 20, double multiplier = 1.5)
        {
            int length = closes.Length;
            double[] upper = new double[length];
            double[] middle = CalculateEma(closes, period);
            double[] lower = new double[length];

            // Calculate true range
            double[] tr = new double[length];
            for (int i = 1; i < length; i++)
            {
                tr[i] = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
            }
            double[] atr = CalculateEma(tr, period); // Technically ATR uses wilder's, but EMA is common for Keltner

            for (int i = period; i < length; i++)
            {
                upper[i] = middle[i] + (multiplier * atr[i]);
                lower[i] = middle[i] - (multiplier * atr[i]);
            }
            return (upper, middle, lower);
        }

        // ─── Z-SCORE ───────────────────────────────────────────────────────────
        public double CalculateZScoreLast(double[] closes, int period = 50)
        {
            if (closes.Length < period) return 0;
            int lastIdx = closes.Length - 1;
            
            double sum = 0;
            for (int j = 0; j < period; j++) sum += closes[lastIdx - j];
            double mean = sum / period;

            double variance = 0;
            for (int j = 0; j < period; j++) variance += Math.Pow(closes[lastIdx - j] - mean, 2);
            double stdDev = Math.Sqrt(variance / period);

            if (stdDev == 0) return 0;
            return (closes[lastIdx] - mean) / stdDev;
        }
        // ─── VOLUME PROFILE (Point of Control) ─────────────────────────────────
        public double CalculatePointOfControl(double[] closes, double[] volumes, int lookbackPeriod = 150)
        {
            int length = closes.Length;
            if (length == 0) return 0;
            
            int startIdx = Math.Max(0, length - lookbackPeriod);
            double minPrice = closes[startIdx];
            double maxPrice = closes[startIdx];
            
            for (int i = startIdx; i < length; i++)
            {
                if (closes[i] < minPrice) minPrice = closes[i];
                if (closes[i] > maxPrice) maxPrice = closes[i];
            }

            if (minPrice == maxPrice) return minPrice;

            int numBins = 20;
            double binSize = (maxPrice - minPrice) / numBins;
            if (binSize == 0) return minPrice;

            double[] volumeBins = new double[numBins];
            
            for (int i = startIdx; i < length; i++)
            {
                int binIdx = (int)((closes[i] - minPrice) / binSize);
                if (binIdx >= numBins) binIdx = numBins - 1;
                volumeBins[binIdx] += volumes[i];
            }

            int maxVolBin = 0;
            double maxVol = 0;
            for (int i = 0; i < numBins; i++)
            {
                if (volumeBins[i] > maxVol)
                {
                    maxVol = volumeBins[i];
                    maxVolBin = i;
                }
            }

            return minPrice + (maxVolBin * binSize) + (binSize / 2); // Center of the bin
        }

        // ─── YTD ANCHORED VWAP ─────────────────────────────────────────────────
        public double CalculateYtdVwap(double[] closes, double[] highs, double[] lows, double[] volumes, DateTime[] dates)
        {
            int length = closes.Length;
            if (length == 0 || dates.Length != length) return 0;

            int currentYear = dates[length - 1].Year;
            
            // Find the first trading day of the year
            int startIdx = length - 1;
            while (startIdx > 0 && dates[startIdx - 1].Year == currentYear)
            {
                startIdx--;
            }

            double cumulativePriceVolume = 0;
            double cumulativeVolume = 0;

            for (int i = startIdx; i < length; i++)
            {
                double typicalPrice = (highs[i] + lows[i] + closes[i]) / 3.0;
                cumulativePriceVolume += typicalPrice * volumes[i];
                cumulativeVolume += volumes[i];
            }

            if (cumulativeVolume == 0) return closes[^1];
            return cumulativePriceVolume / cumulativeVolume;
        }

        // ─── CHANDELIER EXIT (ATR Trailing Stop) ───────────────────────────────
        public double CalculateChandelierExit(double[] highs, double[] lows, double[] closes, int period = 22, double multiplier = 3.0)
        {
            int length = closes.Length;
            if (length < period) return 0;

            // Find highest high in the lookback period
            double highestHigh = highs[length - period];
            for (int i = length - period + 1; i < length; i++)
            {
                if (highs[i] > highestHigh) highestHigh = highs[i];
            }

            // ATR for the period (using our existing ATR logic - we need just the latest ATR)
            var (atrLast, _) = CalculateAtrLastAndAvg60(highs, lows, closes, period);
            
            return highestHigh - (atrLast * multiplier);
        }
    }
}
