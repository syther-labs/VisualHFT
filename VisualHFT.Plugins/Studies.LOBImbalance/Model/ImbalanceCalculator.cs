using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.Studies.LOBImbalance.Model
{
    /// <summary>
    /// Computes the LOB Imbalance tile's value, equal-weighted (today's behaviour) or touch-weighted
    /// (Gould &amp; Bonart-style, generalised across captured depth). Distance is measured from each
    /// side's OWN touch, never the mid, so the spread is charged to neither side. The exponent (2) is
    /// a fixed modelling choice, not configurable.
    /// </summary>
    internal static class ImbalanceCalculator
    {
        internal static double Calculate(
            CachedCollection<BookItem> bids,
            CachedCollection<BookItem> asks,
            int depth,
            ImbalanceWeighting weighting,
            double spreadScale)
        {
            int bidsCount = bids?.Count() ?? 0;
            int asksCount = asks?.Count() ?? 0;
            if (bidsCount == 0 || asksCount == 0)
                return 0;

            double totalBid = 0;
            double totalAsk = 0;

            if (weighting == ImbalanceWeighting.EqualWeight)
            {
                for (int i = 0; i < depth; i++)
                {
                    if (i < asksCount) totalAsk += asks[i].Size.GetValueOrDefault();
                    if (i < bidsCount) totalBid += bids[i].Size.GetValueOrDefault();
                }
            }
            else
            {
                double s = spreadScale > 0 ? spreadScale : double.Epsilon;
                double bidTouch = bids[0].Price.GetValueOrDefault();
                double askTouch = asks[0].Price.GetValueOrDefault();

                for (int i = 0; i < depth; i++)
                {
                    if (i < asksCount)
                    {
                        double d = System.Math.Abs(asks[i].Price.GetValueOrDefault() - askTouch) / s;
                        double w = 1.0 / ((1.0 + d) * (1.0 + d));
                        totalAsk += asks[i].Size.GetValueOrDefault() * w;
                    }
                    if (i < bidsCount)
                    {
                        double d = System.Math.Abs(bids[i].Price.GetValueOrDefault() - bidTouch) / s;
                        double w = 1.0 / ((1.0 + d) * (1.0 + d));
                        totalBid += bids[i].Size.GetValueOrDefault() * w;
                    }
                }
            }

            double denom = totalBid + totalAsk;
            return denom != 0 ? (totalBid - totalAsk) / denom : 0;
        }
    }
}
