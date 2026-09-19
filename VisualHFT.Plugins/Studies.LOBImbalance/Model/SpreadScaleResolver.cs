using System;

namespace VisualHFT.Studies.LOBImbalance.Model
{
    /// <summary>
    /// Resolves the spread scale (S) used to turn a price distance into a number of spreads for
    /// touch weighting. Before the P2 median estimator has warmed up (200 samples), falls back to
    /// the current spread floored at the instrument's OWN tick - never a hardcoded 1.0. A 1.0 floor
    /// is a hardcoded price-unit constant: on any instrument whose spread is below 1.0 in quote
    /// units (most crypto pairs, most equities), it collapses every distance toward 0 and silently
    /// degenerates touch weighting into equal weighting.
    /// </summary>
    internal static class SpreadScaleResolver
    {
        internal const int WarmupSamples = 200;

        internal static double Resolve(int priceDecimalPlaces, double currentSpread, int samples, double medianEstimate)
        {
            if (samples >= WarmupSamples)
                return medianEstimate;

            double oneTick = Math.Pow(10, -priceDecimalPlaces);
            return Math.Max(currentSpread, oneTick);
        }
    }
}
