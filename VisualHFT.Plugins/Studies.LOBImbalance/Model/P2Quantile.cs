using System;

namespace VisualHFT.Studies.LOBImbalance.Model
{
    /// <summary>
    /// Minimal P-squared quantile estimator (Jain and Chlamtac, 1985).
    /// Streaming algorithm - O(1) memory, O(1) per observation. Below 5 samples, sorts and
    /// linearly interpolates instead of returning a raw insertion-order slot.
    /// </summary>
    internal sealed class P2Quantile
    {
        private readonly double _p;
        private int _count;
        private readonly double[] q = new double[5];
        private readonly double[] n = new double[5];
        private readonly double[] np = new double[5];
        private readonly double[] dn = new double[5];

        public P2Quantile(double p)
        {
            if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p));
            _p = p;
        }

        public int Count => _count;

        public double Estimate
        {
            get
            {
                if (_count == 0)
                    return 0.0;

                if (_count < 5)
                {
                    Span<double> buf = stackalloc double[_count];
                    for (int i = 0; i < _count; i++)
                        buf[i] = q[i];
                    buf.Sort();

                    double idx = _p * (_count - 1);
                    int lo = (int)Math.Floor(idx);
                    int hi = (int)Math.Ceiling(idx);
                    if (lo == hi)
                        return buf[lo];
                    double frac = idx - lo;
                    return buf[lo] + frac * (buf[hi] - buf[lo]);
                }

                return q[2];
            }
        }

        public void Observe(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x))
                return;

            if (_count < 5)
            {
                q[_count++] = x;
                if (_count == 5)
                {
                    Array.Sort(q);
                    for (int i = 0; i < 5; i++) n[i] = i + 1;
                    np[0] = 1;
                    np[1] = 1 + 2 * _p;
                    np[2] = 1 + 4 * _p;
                    np[3] = 3 + 2 * _p;
                    np[4] = 5;
                    dn[0] = 0;
                    dn[1] = _p / 2;
                    dn[2] = _p;
                    dn[3] = (1 + _p) / 2;
                    dn[4] = 1;
                }
                return;
            }

            int k;
            if (x < q[0]) { q[0] = x; k = 0; }
            else if (x < q[1]) k = 0;
            else if (x < q[2]) k = 1;
            else if (x < q[3]) k = 2;
            else if (x < q[4]) k = 3;
            else { q[4] = x; k = 3; }

            for (int i = k + 1; i < 5; i++) n[i] += 1;
            for (int i = 0; i < 5; i++) np[i] += dn[i];

            for (int i = 1; i <= 3; i++)
            {
                double d = np[i] - n[i];

                if ((d >= 1 && n[i + 1] - n[i] > 1) || (d <= -1 && n[i - 1] - n[i] < -1))
                {
                    int sign = Math.Sign(d);

                    double qPar = q[i] + (sign / (n[i + 1] - n[i - 1])) * (
                        (n[i] - n[i - 1] + sign) * (q[i + 1] - q[i]) / (n[i + 1] - n[i]) +
                        (n[i + 1] - n[i] - sign) * (q[i] - q[i - 1]) / (n[i] - n[i - 1])
                    );

                    if (q[i - 1] < qPar && qPar < q[i + 1])
                    {
                        q[i] = qPar;
                    }
                    else
                    {
                        q[i] += sign * (q[i + sign] - q[i]) / (n[i + sign] - n[i]);
                    }

                    n[i] += sign;
                }
            }

            _count++;
        }
    }
}
