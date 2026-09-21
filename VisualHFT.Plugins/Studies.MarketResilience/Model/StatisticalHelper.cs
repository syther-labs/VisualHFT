using System;
using VisualHFT.Commons.Pools;

namespace Studies.MarketResilience.Model
{
    public static class StatisticalHelper
    {
        public static decimal Average(this RollingWindow<decimal> window)
        {
            if (window.Count == 0) return 0;
            decimal sum = 0;
            foreach (var item in window.Items)
                sum += item;
            return sum / window.Count;
        }

        public static decimal StandardDeviation(this RollingWindow<decimal> window)
        {
            if (window.Count == 0) return 0;
            var avg = window.Average();
            decimal variance = 0;
            foreach (var item in window.Items)
            {
                var diff = item - avg;
                variance += diff * diff;
            }
            variance /= window.Count;
            return (decimal)Math.Sqrt((double)variance);
        }

        public static double Average(this RollingWindow<double> window)
        {
            if (window.Count == 0) return 0;
            double sum = 0;
            foreach (var item in window.Items)
                sum += item;
            return sum / window.Count;
        }

        public static double StandardDeviation(this RollingWindow<double> window)
        {
            if (window.Count == 0) return 0;
            var avg = window.Average();
            double variance = 0;
            foreach (var item in window.Items)
            {
                var diff = item - avg;
                variance += diff * diff;
            }
            variance /= window.Count;
            return Math.Sqrt(variance);
        }
    }
}
