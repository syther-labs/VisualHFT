using System;
using System.Linq;
using System.Threading;
using Studies.MarketResilience.Model;
using VisualHFT;
using VisualHFT.Commons.Model;
using VisualHFT.Model;
using VisualHFT.Studies.MarketResilience.Model;
using Xunit;

namespace Studies.MarketResilience.Tests
{
    /// <summary>
    /// Numerical-stability contract for <see cref="MarketResilienceCalculator"/>.
    ///
    /// The calculator publishes <c>CurrentMRScore</c> as a resilience score that is documented and
    /// consumed as a value in [0,1]. Two properties must hold for EVERY input sequence:
    ///   1. no public entry point (<c>OnTrade</c>, <c>OnOrderBookUpdate</c>) may throw;
    ///   2. <c>CurrentMRScore</c> must never leave [0,1].
    ///
    /// Three arithmetic hazards sit behind those properties, and each has its own facts here:
    ///   - the shock trade is anchored when it is flagged as large, but its z-score is recomputed
    ///     later against whatever the rolling window holds at trigger time. The anchored size can
    ///     by then be far BELOW the window mean, making the z-score large and negative, so the
    ///     trade score has to be clamped at BOTH ends, not only at zero;
    ///   - a trade window with dispersion vanishingly small relative to its mean gives a z-score
    ///     that carries no information, so the component must be omitted rather than scored;
    ///   - the spread- and depth-recovery scores are <c>avgHistory / (avgHistory + duration)</c>,
    ///     which is 0/0 = NaN when a shock and its recovery land on the same clock reading. NaN
    ///     survives a clamp untouched, and casting it to decimal throws, so the component is
    ///     omitted when the denominator is zero and the final cast is guarded by a finiteness
    ///     check.
    ///
    /// The last group of facts are hand-computed worked examples: a normal shock and recovery, a
    /// near-constant trade window, a zero-duration recovery against an empty history, and a cycle
    /// in which every component is omitted (which must leave the previously published score alone
    /// rather than invent one).
    /// </summary>
    public class MarketResilienceCalculatorNumericalStabilityTests
    {
        private static PlugInSettings Settings(int timeoutMs) => new PlugInSettings { MaxShockMsTimeout = timeoutMs };

        /// <summary>Single-level book; the spread is simply ask - bid.</summary>
        private static OrderBookSnapshot Book(decimal bidPrice, decimal askPrice, double size = 100d)
        {
            var ob = new OrderBook();
            ob.LoadData(
                new[] { new BookItem { Price = (double)askPrice, Size = size, IsBid = false, LocalTimeStamp = DateTime.Now, ServerTimeStamp = DateTime.Now } },
                new[] { new BookItem { Price = (double)bidPrice, Size = size, IsBid = true, LocalTimeStamp = DateTime.Now, ServerTimeStamp = DateTime.Now } });
            var snapshot = new OrderBookSnapshot();
            snapshot.UpdateFrom(ob);
            return snapshot;
        }

        /// <summary>Multi-level book, best-first on each side.</summary>
        private static OrderBookSnapshot MultiLevelBook((decimal px, double sz)[] asks, (decimal px, double sz)[] bids)
        {
            var ob = new OrderBook();
            ob.LoadData(
                asks.Select(a => new BookItem { Price = (double)a.px, Size = a.sz, IsBid = false, LocalTimeStamp = DateTime.Now, ServerTimeStamp = DateTime.Now }).ToArray(),
                bids.Select(b => new BookItem { Price = (double)b.px, Size = b.sz, IsBid = true, LocalTimeStamp = DateTime.Now, ServerTimeStamp = DateTime.Now }).ToArray());
            var snapshot = new OrderBookSnapshot();
            snapshot.UpdateFrom(ob);
            return snapshot;
        }

        /// <summary>
        /// Long warm-up with per-level micro-noise: enough frames to train the depth detector's
        /// median/deviation baselines, plus the trade baseline. Mirrors the warm-up the existing
        /// depth tests use.
        /// </summary>
        private static void WarmUpWithDepth(MarketResilienceCalculator calc, int frames = 300)
        {
            var random = new Random(42);
            double Size() => Math.Max(95, 100d * (1.0 + ((random.NextDouble() - 0.5) * 0.05)));
            decimal Px(decimal basePx) => basePx + ((decimal)(random.NextDouble() - 0.5) * 0.01m);

            for (int i = 0; i < frames; i++)
            {
                calc.OnOrderBookUpdate(MultiLevelBook(
                    asks: new[] { (Px(100.50m), Size()), (Px(100.51m), Size()), (Px(100.52m), Size()) },
                    bids: new[] { (Px(100.49m), Size()), (Px(100.48m), Size()), (Px(100.47m), Size()) }));
                calc.OnTrade(new Trade { Size = 100m * ((i % 3) + 1), Price = 100.49m, Timestamp = DateTime.Now });
            }
        }

        /// <summary>
        /// Feeds a quiet book (spread fixed at 0.5) plus trades cycling 100/200/300 shares, so the
        /// trade baseline has a known mean (200) and a healthy, non-degenerate spread of sizes.
        /// Deliberately short: the depth detector needs far more samples to warm up, so no depth
        /// component participates and the weighting stays trade + spread-recovery + magnitude.
        /// </summary>
        private static decimal[] WarmUp(MarketResilienceCalculator calc, int frames = 30)
        {
            var sizes = new decimal[frames];
            for (int i = 0; i < frames; i++)
            {
                calc.OnOrderBookUpdate(Book(500m, 500.5m));
                sizes[i] = 100m * ((i % 3) + 1);
                calc.OnTrade(new Trade { Size = sizes[i], Price = 500.25m, Timestamp = DateTime.Now });
            }
            return sizes;
        }

        // (a) The published score leaves [0,1] because the anchored shock trade is scored against a
        // window that has moved on underneath it.
        //
        // Mechanism, step by step:
        //   1. the baseline window holds small prints (100-300 shares), so a 5,000-share print is
        //      flagged as a shock and anchored;
        //   2. a burst of block prints (40,000 / 40,001 shares) then fills the whole 500-item
        //      window. Those two sizes differ by one share, so the true standard deviation is 0.5;
        //   3. when the spread shock recovers, the trade component is computed with the CURRENT
        //      window: z = (5,000 - 40,000.5) / 0.5 = -70,001;
        //   4. tradeScore = 1 - z/6 = 11,668 — unclamped above — so the published score is ~7,000.
        // The smaller the residual standard deviation, the larger the published score; the variant
        // that throws outright is covered by the next test.
        [Fact]
        public void OnTrade_WhenTheWindowMeanOvertakesTheAnchoredShockTrade_KeepsScoreWithinZeroAndOne()
        {
            using var calc = new MarketResilienceCalculator(Settings(5000));
            WarmUp(calc);

            // 1. anchor the shock on a print that is large RELATIVE TO THE BASELINE.
            calc.OnTrade(new Trade { Size = 5000m, Price = 500.25m, Timestamp = DateTime.Now });

            // 2. the tape turns to near-identical block prints; the window mean overtakes the anchor.
            for (int i = 0; i < 500; i++)
            {
                calc.OnTrade(new Trade { Size = i % 2 == 0 ? 40000m : 40001m, Price = 500.25m, Timestamp = DateTime.Now });
            }

            // 3. spread shock, then a recovery a measurable number of milliseconds later (so the
            //    recovery duration is strictly positive and this test isolates the z-score defect).
            var duringShock = Record.Exception(() => calc.OnOrderBookUpdate(Book(495m, 500m)));
            Thread.Sleep(30);
            var duringRecovery = Record.Exception(() => calc.OnOrderBookUpdate(Book(500m, 500.5m)));

            // 4. the next ordinary print must also be safe: when the calculation throws, the
            //    calculator never clears its shock state, so every later trade re-enters it.
            var duringNextTrade = Record.Exception(() => calc.OnTrade(new Trade { Size = 100m, Price = 500.25m, Timestamp = DateTime.Now }));

            Assert.Null(duringShock);
            Assert.Null(duringRecovery);
            Assert.Null(duringNextTrade);
            Assert.InRange(calc.CurrentMRScore, 0m, 1m);
        }

        // (a, throwing variant) A shock and its recovery observed on the SAME clock reading make the
        // recovery duration 0. On the first calculation there is no recovery history yet, so the
        // historical average is that same 0, giving 0/(0+0) = NaN. NaN survives the Min/Max clamp,
        // reaches the final cast to decimal, and throws OverflowException — the exception text and
        // the conversion frame reported from the field.
        //
        // The clock used for these durations is the shared time provider, which runs on wall time
        // for a live feed but is DRIVEN BY THE DATA when a recorded session is replayed: it is set
        // once per message and simply does not advance between two updates that carry the same
        // source timestamp. That is the condition reproduced here. The wall clock cannot reproduce
        // it because it has sub-microsecond resolution.
        //
        // Because the throw happens BEFORE the calculator clears its shock state, the state is
        // never cleared, so the very next trade re-enters the same calculation and throws again —
        // one bad recovery turns into a repeating fault on the market-data thread.
        [Fact]
        public void ShockAndRecoveryUnderADataDrivenClock_DoesNotThrowAndKeepsScoreWithinZeroAndOne()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));
                WarmUp(calc);

                calc.OnTrade(new Trade { Size = 5000m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });

                var duringShock = Record.Exception(() => calc.OnOrderBookUpdate(Book(480m, 500m)));
                var duringRecovery = Record.Exception(() => calc.OnOrderBookUpdate(Book(500m, 500.5m)));
                var duringNextTrade = Record.Exception(() => calc.OnTrade(new Trade { Size = 100m, Price = 500.25m, Timestamp = HelperTimeProvider.Now }));

                Assert.Null(duringShock);
                Assert.Null(duringRecovery);
                Assert.Null(duringNextTrade);
                Assert.InRange(calc.CurrentMRScore, 0m, 1m);
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }

        // The depth-recovery component divides the same way and fails the same way, independently of
        // the spread component. Here the book thins WITHOUT widening — sizes collapse at unchanged
        // prices — so no spread shock is ever raised and the depth term is the only recovery term in
        // the calculation. Under a data-driven clock its duration is 0 against an empty history.
        [Fact]
        public void DepthShockAndRecoveryUnderADataDrivenClock_DoesNotThrowAndKeepsScoreWithinZeroAndOne()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));
                WarmUpWithDepth(calc);

                calc.OnTrade(new Trade { Size = 5000m, Price = 100.49m, Timestamp = HelperTimeProvider.Now });

                // Same prices, a fraction of the size: an immediacy collapse with an unchanged spread.
                var thinned = MultiLevelBook(
                    asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                    bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) });
                var restored = MultiLevelBook(
                    asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                    bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) });

                var duringThinning = Record.Exception(() => calc.OnOrderBookUpdate(thinned));
                var duringRestore = Record.Exception(() => calc.OnOrderBookUpdate(restored));
                var duringNextTrade = Record.Exception(() => calc.OnTrade(new Trade { Size = 100m, Price = 100.49m, Timestamp = HelperTimeProvider.Now }));

                Assert.Null(duringThinning);
                Assert.Null(duringRestore);
                Assert.Null(duringNextTrade);
                Assert.InRange(calc.CurrentMRScore, 0m, 1m);
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }

        // One zero-duration recovery must not poison the calculator. Today it does, twice over:
        // the failed calculation never clears the shock state (the clear runs after it), so the
        // NEXT trade walks straight back into the same divide and throws again; and the zero
        // duration is recorded into the recovery history before the failure, so the historical
        // average stays pinned at zero afterwards. A later, ordinary shock and recovery — with a
        // clock that has advanced normally — must still produce a finite, in-range score.
        [Fact]
        public void AfterAZeroDurationRecovery_ALaterNormalCycleStillProducesAnInRangeScore()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));
                WarmUp(calc);

                // First cycle: shock and recovery on the same clock reading. Whatever it does is
                // the subject of the fact above; this one is about what happens AFTERWARDS.
                calc.OnTrade(new Trade { Size = 5000m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
                Record.Exception(() => calc.OnOrderBookUpdate(Book(480m, 500m)));
                Record.Exception(() => calc.OnOrderBookUpdate(Book(500m, 500.5m)));

                // The clock moves on, as it does when the next messages carry later timestamps.
                HelperTimeProvider.IncrementByMilliseconds(10_000);

                // Second cycle: an ordinary shock with a measurable 100 ms recovery.
                var duringTrade = Record.Exception(() => calc.OnTrade(new Trade { Size = 5000m, Price = 500.25m, Timestamp = HelperTimeProvider.Now }));
                var duringShock = Record.Exception(() => calc.OnOrderBookUpdate(Book(480m, 500m)));
                HelperTimeProvider.IncrementByMilliseconds(100);
                var duringRecovery = Record.Exception(() => calc.OnOrderBookUpdate(Book(500m, 500.5m)));

                Assert.Null(duringTrade);
                Assert.Null(duringShock);
                Assert.Null(duringRecovery);
                Assert.InRange(calc.CurrentMRScore, 0m, 1m);
                Assert.NotEqual(1m, calc.CurrentMRScore);   // a score was actually produced
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }

        // (b) Property: over random equity-shaped tape (round lots, occasional blocks) interleaved
        // with spread shocks and recoveries, the calculator never throws and the score never leaves
        // [0,1]. Same two defects, reached from randomised input instead of a hand-built sequence.
        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(13)]
        [InlineData(101)]
        [InlineData(2029)]
        public void RandomEquityTapeWithShocks_NeverThrowsAndKeepsScoreWithinZeroAndOne(int seed)
        {
            var random = new Random(seed);
            using var calc = new MarketResilienceCalculator(Settings(5000));
            WarmUp(calc, 40);
            int cyclesThatScored = 0;

            for (int cycle = 0; cycle < 8; cycle++)
            {
                decimal scoreBefore = calc.CurrentMRScore;

                // A print large enough to stand out against the CURRENT baseline gets anchored.
                decimal shockSize = 100m * random.Next(50, 120);
                var duringShockTrade = Record.Exception(() => calc.OnTrade(new Trade { Size = shockSize, Price = 500.25m, Timestamp = DateTime.Now }));
                Assert.Null(duringShockTrade);

                // Then the tape runs long enough to turn the whole 500-print window over. Two
                // regimes, both ordinary on an equity open: retail-sized round lots, or an algo
                // slicing near-identical clips (which is what collapses the size variance).
                int prints = random.Next(520, 900);
                bool algoClips = random.Next(2) == 0;
                decimal clip = 100m * random.Next(100, 500);
                for (int i = 0; i < prints; i++)
                {
                    decimal size = algoClips
                        ? clip + (i % 2)                        // near-identical clips, one share apart
                        : 100m * random.Next(1, 30);            // ordinary round lots
                    var duringTrade = Record.Exception(() => calc.OnTrade(new Trade { Size = size, Price = 500.25m, Timestamp = DateTime.Now }));
                    Assert.Null(duringTrade);
                    Assert.InRange(calc.CurrentMRScore, 0m, 1m);
                }

                decimal widened = 5m + random.Next(0, 40);
                var duringWiden = Record.Exception(() => calc.OnOrderBookUpdate(Book(500m - widened, 500m)));
                Thread.Sleep(2);
                var duringRecover = Record.Exception(() => calc.OnOrderBookUpdate(Book(500m, 500.5m)));

                Assert.Null(duringWiden);
                Assert.Null(duringRecover);
                Assert.InRange(calc.CurrentMRScore, 0m, 1m);

                if (calc.CurrentMRScore != scoreBefore)
                    cyclesThatScored++;
            }

            // Guard against a vacuous pass: the scenario must actually have produced scores.
            Assert.True(cyclesThatScored > 0, "No shock/recovery cycle produced a score, so nothing was exercised.");
        }

        // (c) Regression guard for the numerics themselves. The same shock/recovery cycle is scored
        // by hand from the known window contents, with the variance computed the exact (two-pass)
        // way, and the published score must match. The weights mirrored here are the ones the
        // component-weight test already asserts: trade 0.30, spread recovery 0.10, magnitude 0.10,
        // normalised by the total weight actually used. On the FIRST recovery the historical
        // average equals the measured duration, so the recovery term is exactly 0.5.
        [Fact]
        public void ShockAndRecovery_ScoreMatchesTwoPassVarianceComputedByHand()
        {
            const decimal shockTradeSize = 500m;   // above baseline mean + 2 sigma, below mean + 6 sigma
            const decimal shockSpread = 5m;        // bid 495 / ask 500
            const int warmUpFrames = 30;

            using var calc = new MarketResilienceCalculator(Settings(5000));
            decimal[] window = WarmUp(calc, warmUpFrames);

            calc.OnTrade(new Trade { Size = shockTradeSize, Price = 500.25m, Timestamp = DateTime.Now });
            calc.OnOrderBookUpdate(Book(495m, 500m));
            Thread.Sleep(30);                       // keep the recovery duration strictly positive
            calc.OnOrderBookUpdate(Book(500m, 500.5m));

            // --- trade severity (weight 0.30), variance computed the exact two-pass way ---
            decimal mean = window.Sum() / window.Length;
            decimal sumSquaredDeviations = 0m;
            foreach (decimal size in window)
            {
                decimal deviation = size - mean;
                sumSquaredDeviations += deviation * deviation;
            }
            decimal variance = sumSquaredDeviations / window.Length;
            decimal standardDeviation = (decimal)Math.Sqrt((double)variance);
            double tradeZ = (double)((shockTradeSize - mean) / standardDeviation);
            double tradeScore = Math.Max(0, 1.0 - (tradeZ / 6.0));

            // --- spread recovery (weight 0.10): first recovery scores exactly 0.5 ---
            const double spreadRecoveryScore = 0.5;

            // --- spread shock magnitude (weight 0.10) ---
            // The spread history at trigger time is: warm-up frames at 0.5, the shock, the recovery.
            decimal averageSpread = ((warmUpFrames * 0.5m) + shockSpread + 0.5m) / (warmUpFrames + 2);
            double magnitudeRatio = (double)(shockSpread / Math.Max(averageSpread, 0.0001m));
            double magnitudeScore = Math.Max(0, Math.Min(1, 1.0 / magnitudeRatio));

            double expected = ((0.30 * tradeScore) + (0.10 * spreadRecoveryScore) + (0.10 * magnitudeScore)) / 0.50;

            Assert.InRange(tradeScore, 0.0, 1.0);   // guards the scenario: the z-score must be in the scored band
            Assert.Equal((decimal)expected, calc.CurrentMRScore, 6);
        }

        // ---------------------------------------------------------------------------------
        // WORKED EXAMPLES - hand-computed expected scores.
        //
        // Component weights as declared in the calculator: trade severity 0.30, spread recovery
        // 0.10, depth recovery 0.50, spread-shock magnitude 0.10. A component whose evidence is
        // unusable is OMITTED from both the weighted sum and the total weight, so the published
        // score is the weighted average over the components that actually participated.
        //
        // All three run under a fixed clock, so every recovery duration is exact rather than
        // whatever the machine happened to measure. The warm-up is deliberately short (30 book
        // frames): the depth detector needs 200 samples before it reports anything, so the depth
        // component never participates in these examples and the arithmetic stays checkable by
        // hand.
        // ---------------------------------------------------------------------------------

        // WORKED EXAMPLE 1 - a healthy shock/recovery cycle: a strictly positive recovery
        // duration measured against an existing recovery history. Every component that has
        // evidence participates.
        //
        //   trade window ...... 30 prints cycling 100/200/300 -> mean 200,
        //                       population sd sqrt(200000/30) = 81.6496580927726
        //   shock print ....... 500 -> z = (500 - 200) / 81.6496580927726 = 3.674234614
        //                       score = 1 - z/6 = 0.387627564                     (weight 0.30)
        //   spread recovery ... history holds one 50 ms sample; this recovery took 100 ms
        //                       score = 50 / (50 + 100) = 0.333333333             (weight 0.10)
        //   magnitude ......... spread window = 30x0.5, 5, 0.5, 5, 0.5 -> mean 26/34 = 0.764705882
        //                       score = mean / shock = 0.764705882 / 5 = 0.152941176
        //                                                                         (weight 0.10)
        //   depth recovery .... no evidence (detector not warmed) -> omitted
        //
        //   score = (0.30*0.387627564 + 0.10*0.333333333 + 0.10*0.152941176) / 0.50
        //         = 0.164915720 / 0.50
        //         = 0.329831441
        //
        // This example is GREEN both before and after the omission rule: it is the baseline that
        // proves a healthy score did not move.
        [Fact]
        public void WorkedExample_NormalShockAndRecoveryWithHistory_ScoresTradeRecoveryAndMagnitude()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));
                WarmUp(calc, 30);

                // First cycle: a 50 ms recovery, which becomes the single sample of recovery history.
                calc.OnTrade(new Trade { Size = 500m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(Book(495m, 500m));
                HelperTimeProvider.IncrementByMilliseconds(50);
                calc.OnOrderBookUpdate(Book(500m, 500.5m));

                // Well clear of the shock timeout, so the second cycle starts from a clean state.
                HelperTimeProvider.IncrementByMilliseconds(10_000);

                // Second cycle: the same shape, recovering in 100 ms.
                calc.OnTrade(new Trade { Size = 500m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(Book(495m, 500m));
                HelperTimeProvider.IncrementByMilliseconds(100);
                calc.OnOrderBookUpdate(Book(500m, 500.5m));

                decimal mrScore = calc.CurrentMRScore;
                Assert.InRange(mrScore, 0.329831441m - 0.01m, 0.329831441m + 0.01m);
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }

        // WORKED EXAMPLE 2 - a near-constant trade window. The dispersion is real and strictly
        // positive, but negligible relative to the mean, so a z-score measured against it carries
        // no information about the shock print and the trade component is omitted.
        //
        //   trade window ...... 30 prints alternating 1,000,000 and 1,000,000.0001
        //                       mean 1,000,000.00005, population sd 0.00005
        //                       0.00005 < 1e-6 * 1,000,000.00005 = 1.00000000005 -> OMITTED
        //   spread recovery ... empty history, 100 ms recovery
        //                       score = 100 / (100 + 100) = 0.5                   (weight 0.10)
        //   magnitude ......... spread window = 30x0.5, 5, 0.5 -> mean 20.5/32 = 0.640625
        //                       score = 0.640625 / 5 = 0.128125                   (weight 0.10)
        //   depth recovery .... no evidence -> omitted
        //
        //   score = (0.10*0.5 + 0.10*0.128125) / 0.20 = 0.0628125 / 0.20 = 0.3140625
        //
        // RED before the relative-dispersion floor (the trade component participated at weight
        // 0.30 with a score of 0, publishing 0.1256); GREEN after.
        [Fact]
        public void WorkedExample_NearConstantTradeWindow_OmitsTheTradeComponent()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));

                // Quiet book, and an algo slicing near-identical clips one ten-thousandth apart.
                const decimal clip = 1_000_000m;
                for (int i = 0; i < 30; i++)
                {
                    calc.OnOrderBookUpdate(Book(500m, 500.5m));
                    calc.OnTrade(new Trade
                    {
                        Size = i % 2 == 0 ? clip : clip + 0.0001m,
                        Price = 500.25m,
                        Timestamp = HelperTimeProvider.Now
                    });
                }

                calc.OnTrade(new Trade { Size = 1_100_000m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(Book(495m, 500m));
                HelperTimeProvider.IncrementByMilliseconds(100);
                calc.OnOrderBookUpdate(Book(500m, 500.5m));

                decimal mrScore = calc.CurrentMRScore;
                Assert.InRange(mrScore, 0.3140625m - 0.01m, 0.3140625m + 0.01m);
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }

        // WORKED EXAMPLE 3 - a shock and its recovery observed on the same clock reading, against
        // an empty recovery history. The recovery score would be 0/(0+0): an instantaneous
        // recovery measured against no history is an absence of evidence, so the spread-recovery
        // component is omitted. The components that remain are trade severity (0.30) and
        // spread-shock magnitude (0.10).
        //
        //   trade ............. as in worked example 1 -> 0.387627564             (weight 0.30)
        //   spread recovery ... duration 0 against an empty history -> OMITTED
        //   magnitude ......... spread window = 30x0.5, 5, 0.5 -> mean 20.5/32 = 0.640625
        //                       score = 0.640625 / 5 = 0.128125                   (weight 0.10)
        //   depth recovery .... no evidence -> omitted
        //
        //   score = (0.30*0.387627564 + 0.10*0.128125) / 0.40 = 0.129100769 / 0.40 = 0.322751923
        //
        // RED before the omission rule: the division produced NaN, the clamp propagated it and the
        // final cast to decimal threw OverflowException. GREEN after.
        [Fact]
        public void WorkedExample_ZeroDurationRecoveryAgainstEmptyHistory_OmitsTheRecoveryComponent()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));
                WarmUp(calc, 30);

                calc.OnTrade(new Trade { Size = 500m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(Book(495m, 500m));

                // No clock movement between the shock and its recovery.
                calc.OnOrderBookUpdate(Book(500m, 500.5m));

                decimal mrScore = calc.CurrentMRScore;
                Assert.InRange(mrScore, 0.322751923m - 0.01m, 0.322751923m + 0.01m);
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }

        // A cycle in which EVERY component is omitted carries no evidence at all, so it must not
        // move the published score. Publishing a fixed value there would be worse than publishing
        // nothing: the fixed value the calculator uses for "no data" is 1.0, the TOP of the scale,
        // so an all-omitted cycle during a real depth depletion would report maximum resilience at
        // the exact moment liquidity vanished.
        //
        // Reaching a zero total weight takes a depth-only cycle: a spread shock always contributes
        // the magnitude component, so anything involving the spread carries weight by construction.
        // The trade window is flushed to a constant size so the trade component is omitted too, and
        // the depth recovery is instantaneous against an empty depth history so that component is
        // omitted as well.
        //
        // The second half of this fact is the guard against a vacuous pass: the same depth cycle
        // run with a MEASURABLE duration must move the score. If it does not, the depth pair was
        // never completing and the first assertion proved nothing.
        [Fact]
        public void AnAllOmittedCycle_LeavesThePreviouslyPublishedScoreUnchanged()
        {
            HelperTimeProvider.SetFixedTime(new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local));
            try
            {
                using var calc = new MarketResilienceCalculator(Settings(5000));

                // 1. A normal spread cycle on a single-level book, which publishes a real score.
                //    The depth detector stays cold here, so the depth history is still empty.
                WarmUp(calc, 30);
                calc.OnTrade(new Trade { Size = 500m, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(Book(495m, 500m));
                HelperTimeProvider.IncrementByMilliseconds(100);
                calc.OnOrderBookUpdate(Book(500m, 500.5m));

                decimal publishedScore = calc.CurrentMRScore;
                Assert.InRange(publishedScore, 0m, 1m);
                Assert.NotEqual(1m, publishedScore);    // a real score, distinguishable from the no-data value

                // 2. Warm the depth detector, and flush the trade window to a single constant size
                //    so the trade component has no dispersion left to score.
                var random = new Random(42);
                double Size() => Math.Max(95, 100d * (1.0 + ((random.NextDouble() - 0.5) * 0.05)));
                decimal Px(decimal basePx) => basePx + ((decimal)(random.NextDouble() - 0.5) * 0.01m);
                for (int i = 0; i < 500; i++)
                {
                    calc.OnOrderBookUpdate(MultiLevelBook(
                        asks: new[] { (Px(100.50m), Size()), (Px(100.51m), Size()), (Px(100.52m), Size()) },
                        bids: new[] { (Px(100.49m), Size()), (Px(100.48m), Size()), (Px(100.47m), Size()) }));
                    calc.OnTrade(new Trade { Size = 100m, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
                }

                var thinned = MultiLevelBook(
                    asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                    bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) });
                var restored = MultiLevelBook(
                    asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                    bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) });

                // 3. The all-omitted cycle: an instantaneous depth recovery, no spread shock, and a
                //    constant trade window. Nothing carries weight, so nothing may be published.
                calc.OnTrade(new Trade { Size = 5000m, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(thinned);
                calc.OnOrderBookUpdate(restored);

                decimal afterOmittedCycle = calc.CurrentMRScore;
                Assert.Equal(publishedScore, afterOmittedCycle);

                // 4. The vacuity guard: the same cycle with a measurable recovery DOES score.
                calc.OnTrade(new Trade { Size = 5000m, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
                calc.OnOrderBookUpdate(MultiLevelBook(
                    asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                    bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) }));
                HelperTimeProvider.IncrementByMilliseconds(100);
                calc.OnOrderBookUpdate(MultiLevelBook(
                    asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                    bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }));

                decimal afterScoringCycle = calc.CurrentMRScore;
                Assert.InRange(afterScoringCycle, 0m, 1m);
                Assert.NotEqual(publishedScore, afterScoringCycle);
            }
            finally
            {
                HelperTimeProvider.ResetToSystemTime();
            }
        }
    }
}
