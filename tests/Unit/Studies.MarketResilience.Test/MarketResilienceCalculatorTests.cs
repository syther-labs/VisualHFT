using System;
using System.Linq;
using System.Threading;
using VisualHFT;
using VisualHFT.Model;
using Studies.MarketResilience.Model;
using VisualHFT.Commons.Model;
using VisualHFT.Studies.MarketResilience.Model;
using VisualHFT.Enums;
using Xunit;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Studies.MarketResilience.Test")]

namespace Studies.MarketResilience.Tests
{
    /// <summary>
    /// Scoring rules these tests assert:
    ///   * the first scored recovery of a session seeds the recovery history and publishes
    ///     nothing: a recovery with no history has nothing to be compared to;
    ///   * only the DEPLETED side redeploying counts as a depth recovery;
    ///   * a component whose window runs out is scored as a non-recovery (0 at its full weight),
    ///     not discarded.
    /// Recovery durations are driven through the shared time provider so they are exact.
    /// </summary>
    public class MarketResilienceTests
    {
        private static readonly DateTime ClockStart = new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local);

        private PlugInSettings _settings;
        public MarketResilienceTests()
        {
            _settings = new PlugInSettings()
            {
                MaxShockMsTimeout = 500
            };
        }

        /// <summary>Pins the shared time provider; disposing restores wall time.</summary>
        private sealed class FixedClock : IDisposable
        {
            public FixedClock() => HelperTimeProvider.SetFixedTime(ClockStart);
            public void Advance(long milliseconds) => HelperTimeProvider.IncrementByMilliseconds(milliseconds);
            public void Dispose() => HelperTimeProvider.ResetToSystemTime();
        }

        private OrderBookSnapshot CreateOrderBook(decimal spread, decimal bidPrice, decimal askPrice)
        {
            var ob = new OrderBook();
            
            ob.LoadData(new[] { new BookItem { Price = (double?)askPrice, Size = 100 } },
                        new[] { new BookItem { Price = (double?)bidPrice, Size = 100 } });
            var newSnapshot = new OrderBookSnapshot();
            newSnapshot.UpdateFrom(ob);
            return newSnapshot;
        }
        private OrderBookSnapshot BuildLOB((decimal px, double sz)[] asks, (decimal px, double sz)[] bids)
        {
            var ob = new OrderBook();

            var askItems = asks.Select(a => new BookItem
            {
                Price = (double)a.px,
                Size = a.sz,
                IsBid = false,
                LocalTimeStamp = DateTime.Now,
                ServerTimeStamp = DateTime.Now
            }).ToArray();

            var bidItems = bids.Select(b => new BookItem
            {
                Price = (double)b.px,
                Size = b.sz,
                IsBid = true,
                LocalTimeStamp = DateTime.Now,
                ServerTimeStamp = DateTime.Now
            }).ToArray();

            ob.LoadData(askItems, bidItems);
            var snapshot = new OrderBookSnapshot();
            snapshot.UpdateFrom(ob);
            return snapshot;
        }

        private void WarmUp(MarketResilienceCalculator calc, int frames = 300)
        {
            // Warm up with realistic micro-noise to properly train the MAD estimator
            // This ensures the detector learns what "normal" market variance looks like
            var random = new Random(42); // Fixed seed for test reproducibility

            for (int i = 0; i < frames; i++)
            {
                var lob = BuildLOB(
                    asks: new[] {
                (100.50m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.51m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.52m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    },
                    bids: new[] {
                (100.49m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.48m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.47m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    }
                );

                // ✅ Train BOTH order book AND trade baselines
                calc.OnOrderBookUpdate(lob);  // Trains: recentSpreads, depth baselines

                // ✅ ADD: Train trade size baseline
                calc.OnTrade(new Trade
                {
                    Size = 100, // Normal trade size (baseline)
                    Price = 100.49m,
                    Timestamp = DateTime.Now
                });
            }
        }

        /// <summary>
        /// The first shock/recovery cycle of a session seeds the recovery history and publishes
        /// nothing; the second, measured against it, publishes a score.
        ///
        /// Defects this catches: a stand-in published on the first recovery (the score would move
        /// after cycle 1); the second recovery not being scored (the score would still read 1).
        /// </summary>
        [Fact]
        public void MarketResilienceCalculator_ShouldTrigger_AfterShockAndRecovery()
        {
            using var clock = new FixedClock();
            var mrCalc = new MarketResilienceCalculator(_settings);

            // Feed historical stable data
            for (int i = 0; i < 30; i++)
            {
                mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
                mrCalc.OnTrade(new Trade { Size = 10, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
            }

            // First cycle: shock, recovery 400 ms later. Seeds the history, publishes nothing.
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            clock.Advance(_settings.MaxShockMsTimeout.Value - 100);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));

            Assert.Equal(1m, mrCalc.CurrentMRScore);

            // Second cycle, the same shape, measured against the first.
            clock.Advance(10_000);
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            clock.Advance(_settings.MaxShockMsTimeout.Value - 100);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));

            // Verify MR recalculation occurred
            Assert.NotEqual(1m, mrCalc.CurrentMRScore);
            Assert.InRange(mrCalc.CurrentMRScore, 0, 1);
        }
        /// <summary>
        /// A spread widening and a BID depletion, and the bid never comes back. The ask improving
        /// meanwhile is not a recovery of anything; the event closes when its window runs out,
        /// scored as a non-recovery, and the direction points away from the bid that failed.
        ///
        /// Defects this catches: crediting the ask's growth as the recovery (the score would move
        /// at the ask frame); discarding a timed-out event (the score would stay at the seeded
        /// value, above 0.30, and no direction would be spoken); a swapped direction.
        /// </summary>
        [Fact]
        public void MarketResilienceWithBias_ShouldDetectBearishBias()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 600 };
            var mrCalcBias = new MarketResilienceWithBias(_settings);
            WarmUp(mrCalcBias, 300);

            var shock = BuildLOB(
                asks: new[] { (105.00m, 100.0), (105.01m, 100.0), (105.02m, 100.0) },
                bids: new[] { (100.30m, 20.0), (100.29m, 15.0) }
            );
            var recover = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            // Seed the recovery history with fast (100 ms) same-side recoveries. The first cycle
            // only seeds; the rest publish a middling score that leaves the bias unarmed.
            for (int i = 0; i < 5; i++)
            {
                mrCalcBias.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
                mrCalcBias.OnOrderBookUpdate(shock);
                clock.Advance(100);
                mrCalcBias.OnOrderBookUpdate(recover);
            }

            var seededScore = mrCalcBias.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);
            Assert.Equal(eMarketBias.Neutral, mrCalcBias.CurrentMarketBias);

            // The event under test: the spread widens, then the bid is taken out.
            mrCalcBias.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });

            var spreadShock = BuildLOB(
                asks: new[] { (105.00m, 100.0), (105.01m, 100.0), (105.02m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );
            mrCalcBias.OnOrderBookUpdate(spreadShock);
            mrCalcBias.OnOrderBookUpdate(shock);

            clock.Advance(450);

            // The ask comes back in while the bid is still gone. Not a recovery.
            var askImproved = BuildLOB(
                asks: new[] { (100.48m, 120.0), (100.49m, 120.0), (100.50m, 120.0) },
                bids: new[] { (100.30m, 20.0), (100.29m, 15.0) }
            );
            mrCalcBias.OnOrderBookUpdate(askImproved);

            Assert.Equal(seededScore, mrCalcBias.CurrentMRScore);
            Assert.Equal(eMarketBias.Neutral, mrCalcBias.CurrentMarketBias);

            // The window (600 ms) runs out with the bid still gone.
            clock.Advance(151);
            mrCalcBias.OnOrderBookUpdate(askImproved);

            Assert.NotEqual(seededScore, mrCalcBias.CurrentMRScore);
            Assert.True(mrCalcBias.CurrentMRScore <= 0.30m,
                $"MR score {mrCalcBias.CurrentMRScore} should be ≤ 0.30");
            Assert.Equal(eMarketBias.Bearish, mrCalcBias.CurrentMarketBias);
        }

        /// <summary>
        /// Mirror of the Bearish case: the ASK is taken out and never comes back; the bid
        /// improving meanwhile is not a recovery. Defects this catches: the same three, on the
        /// ask side.
        /// </summary>
        [Fact]
        public void MarketResilienceWithBias_ShouldDetectBullishBias()
        {
            using var clock = new FixedClock();
            var _settings = new PlugInSettings() { MaxShockMsTimeout = 600 };
            var mrCalcBias = new MarketResilienceWithBias(_settings);
            WarmUp(mrCalcBias, 300);

            var askDepleted = BuildLOB(
                asks: new[] { (100.70m, 20.0), (100.71m, 15.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );
            var recover = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            for (int i = 0; i < 5; i++)
            {
                mrCalcBias.OnTrade(new Trade { Size = 5000, Price = 100.50m, Timestamp = HelperTimeProvider.Now });
                mrCalcBias.OnOrderBookUpdate(askDepleted);
                clock.Advance(100);
                mrCalcBias.OnOrderBookUpdate(recover);
            }

            var seededScore = mrCalcBias.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);
            Assert.Equal(eMarketBias.Neutral, mrCalcBias.CurrentMarketBias);

            mrCalcBias.OnTrade(new Trade { Size = 5000, Price = 100.50m, Timestamp = HelperTimeProvider.Now });
            mrCalcBias.OnOrderBookUpdate(askDepleted);

            clock.Advance(450);

            // The bid comes back in while the ask is still gone. Not a recovery.
            var bidImproved = BuildLOB(
                asks: new[] { (100.70m, 20.0), (100.71m, 15.0) },
                bids: new[] { (100.50m, 120.0), (100.49m, 120.0), (100.48m, 120.0) }
            );
            mrCalcBias.OnOrderBookUpdate(bidImproved);

            Assert.Equal(seededScore, mrCalcBias.CurrentMRScore);
            Assert.Equal(eMarketBias.Neutral, mrCalcBias.CurrentMarketBias);

            clock.Advance(151);
            mrCalcBias.OnOrderBookUpdate(bidImproved);

            Assert.NotEqual(seededScore, mrCalcBias.CurrentMRScore);
            Assert.True(mrCalcBias.CurrentMRScore <= 0.30m,
                $"MR score {mrCalcBias.CurrentMRScore} should be ≤ 0.30");
            Assert.Equal(eMarketBias.Bullish, mrCalcBias.CurrentMarketBias);
        }

        /// <summary>
        /// The BID is taken out and comes back, slowly enough to score poorly. A poor score
        /// arms the bias, but the depleted side redeployed, so there is no side to point away
        /// from: Neutral.
        ///
        /// Defects this catches: a direction attributed from the depleted side alone (Bearish);
        /// a same-side refill not being credited as the recovery (the event would stay open and
        /// the score would stay at the seeded value, above 0.30).
        /// </summary>
        [Fact]
        public void MarketResilienceWithBias_ShouldDetectNeutralBias_WhenFullyRecovered()
        {
            using var clock = new FixedClock();
            var mrCalcBias = new MarketResilienceWithBias(_settings);
            WarmUp(mrCalcBias, 300);

            var bidDepleted = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.40m, 50.0), (100.39m, 30.0) }
            );
            var bidRecovered = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            // Fast (100 ms) recoveries set the reference, so the 450 ms one below scores poorly.
            for (int i = 0; i < 5; i++)
            {
                mrCalcBias.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
                mrCalcBias.OnOrderBookUpdate(bidDepleted);
                clock.Advance(100);
                mrCalcBias.OnOrderBookUpdate(bidRecovered);
            }

            var seededScore = mrCalcBias.CurrentMRScore;
            Assert.NotEqual(1m, seededScore);

            mrCalcBias.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
            mrCalcBias.OnOrderBookUpdate(bidDepleted);
            clock.Advance(450);
            mrCalcBias.OnOrderBookUpdate(bidRecovered);

            Assert.NotEqual(seededScore, mrCalcBias.CurrentMRScore);
            Assert.True(mrCalcBias.CurrentMRScore <= 0.30m,
                $"MR score {mrCalcBias.CurrentMRScore} should be ≤ 0.30 so the bias is armed");
            Assert.Equal(eMarketBias.Neutral, mrCalcBias.CurrentMarketBias);
        }
        /// <summary>
        /// Two identical cycles, each a spread shock plus a depth depletion recovering 100 ms
        /// later. The first seeds the history; the second is measured against it, so both
        /// recovery components score exactly 0.5 and the published score is decided by the
        /// weights alone:
        ///
        ///   trade severity .... warm-up prints are all one size → no dispersion → omitted
        ///   spread recovery ... 100 ms vs 100 ms → 0.5                              (weight 0.10)
        ///   depth recovery .... 100 ms vs 100 ms → 0.5                              (weight 0.50)
        ///   magnitude ......... usual spread (~0.04, two 5.0 shocks in the window) / 5.0 ≈ 0.009
        ///                                                                             (weight 0.10)
        ///   score = (0.10·0.5 + 0.50·0.5 + 0.10·0.009) / 0.70 ≈ 0.4298
        ///
        /// Defects this catches: any change to the 0.10 / 0.50 / 0.10 weights (equal weights
        /// would give 0.417 + the magnitude term); the recovery-time ratio not being 0.5 for an
        /// equal duration; the first cycle publishing.
        /// </summary>
        [Fact]
        public void MRCalculation_ComponentWeights_AreCorrect()
        {
            using var clock = new FixedClock();
            var mrCalc = new MarketResilienceCalculator(_settings);
            WarmUp(mrCalc); // 300 samples

            var depthShock = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.40m, 50.0), (100.39m, 30.0) }
            );
            var recovery = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            void Cycle()
            {
                mrCalc.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });

                // Spread shock: 5.0 wide against a baseline of ~0.01 (bid 97.99 / ask 102.99).
                mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 97.99m, 102.99m));
                mrCalc.OnOrderBookUpdate(depthShock);

                clock.Advance(100);
                mrCalc.OnOrderBookUpdate(recovery);
            }

            Cycle();
            Assert.Equal(1m, mrCalc.CurrentMRScore);   // the first cycle seeds, publishes nothing

            clock.Advance(10_000);
            Cycle();

            Assert.InRange(mrCalc.CurrentMRScore, 0.428m, 0.431m);
        }

        /// <summary>
        /// A depth depletion whose side never comes back inside the window is scored as a
        /// non-recovery: depth 0 at its full 0.50 weight. With the spread unchanged and the trade
        /// component omitted (one-size warm-up prints), depth is the only component, so the
        /// published score is exactly 0. The event state is cleared: a later cycle seeds and
        /// then scores normally.
        ///
        /// Defects this catches: a timed-out event being discarded (the score would stay 1);
        /// the window closing early (the score would move before the deadline); the timed-out
        /// depth being omitted rather than scored 0 (nothing would be published); the state not
        /// being cleared (the later cycles could not be anchored).
        /// </summary>
        [Fact]
        public void MRCalculation_DepthTimeout_ScoresZeroAtFullWeight()
        {
            using var clock = new FixedClock();
            var mrCalc = new MarketResilienceCalculator(_settings);
            WarmUp(mrCalc);

            // Same prices, a fraction of the size: an immediacy collapse with an unchanged spread.
            var thinned = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 5.0), (100.48m, 5.0), (100.47m, 5.0) }
            );
            var restored = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            mrCalc.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(thinned);

            clock.Advance(450);
            mrCalc.OnOrderBookUpdate(thinned);
            Assert.Equal(1m, mrCalc.CurrentMRScore);    // inside the window: still open

            clock.Advance(51);
            mrCalc.OnOrderBookUpdate(thinned);
            Assert.Equal(0m, mrCalc.CurrentMRScore);    // the window ran out: scored, 0 at full weight

            // A quiet frame so the next depletion is a new edge, then two ordinary cycles.
            mrCalc.OnOrderBookUpdate(restored);
            clock.Advance(10_000);

            mrCalc.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(thinned);
            clock.Advance(100);
            mrCalc.OnOrderBookUpdate(restored);
            Assert.Equal(0m, mrCalc.CurrentMRScore);    // seeds the depth history, publishes nothing

            clock.Advance(10_000);
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(thinned);
            clock.Advance(100);
            mrCalc.OnOrderBookUpdate(restored);
            Assert.Equal(0.5m, mrCalc.CurrentMRScore);  // 100 ms vs 100 ms, depth the only component
        }

        [Fact]
        public void MRCalculation_TradeTimeout_PreventsCalculation()
        {
            var mrCalc = new MarketResilienceCalculator(_settings);

            // Feed baseline data
            for (int i = 0; i < 30; i++)
            {
                mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
                mrCalc.OnTrade(new Trade { Size = 10, Price = 500.25m });
            }

            // Trigger trade shock
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500 });

            // Wait for trade to timeout (500ms from settings)
            Thread.Sleep(550);

            // Trigger spread shock AFTER trade timeout
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));

            // ✅ VALIDATE: No calculation (trade anchor missing)
            Assert.Equal(1m, mrCalc.CurrentMRScore);
        }

        /// <summary>
        /// A spread widening that never returns inside the window is scored as a non-recovery:
        /// spread recovery 0 at its 0.10 weight, alongside the magnitude component. The trade
        /// component is omitted (one-size prints), and the depth detector is cold (30 frames).
        ///
        ///   spread recovery ... window ran out → 0                                (weight 0.10)
        ///   magnitude ......... usual spread (30×0.5, 5, 0.5 → 0.640625) / 5 = 0.128125
        ///                                                                          (weight 0.10)
        ///   score = 0.10·0.128125 / 0.20 = 0.0640625
        ///
        /// The event state is then cleared: the next cycle seeds the history (publishing
        /// nothing) and the one after scores 0.5 on recovery time.
        ///
        /// Defects this catches: a timed-out spread being discarded (the score would stay 1) or
        /// omitted (nothing would be published); the wrong weight on it; the event state not
        /// being cleared after the timeout.
        /// </summary>
        [Fact]
        public void MRCalculation_SpreadTimeout_ScoresZeroAndClearsState()
        {
            using var clock = new FixedClock();
            var mrCalc = new MarketResilienceCalculator(_settings);

            // Setup
            for (int i = 0; i < 30; i++)
            {
                mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
                mrCalc.OnTrade(new Trade { Size = 10, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
            }

            // Trigger shocks
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));

            // The window (500 ms) runs out before the spread returns.
            clock.Advance(501);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));

            Assert.Equal(0.0640625m, mrCalc.CurrentMRScore, 6);

            // State cleared: a new event can be anchored. The first recovery seeds ...
            clock.Advance(10_000);
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            clock.Advance(100);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
            Assert.Equal(0.0640625m, mrCalc.CurrentMRScore, 6);

            // ... and the second scores: spread recovery 100 ms vs 100 ms = 0.5, magnitude
            // (30×0.5 + 3×(5 + 0.5) → 0.875) / 5 = 0.175 → (0.05 + 0.0175) / 0.20 = 0.3375.
            clock.Advance(10_000);
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            clock.Advance(100);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
            Assert.Equal(0.3375m, mrCalc.CurrentMRScore, 6);
        }

        /// <summary>
        /// Trade + spread shock with no depth event. The first cycle seeds the spread-recovery
        /// history; the second, with the same 200 ms return, scores exactly 0.5 on it:
        ///
        ///   trade severity .... one-size prints → omitted
        ///   spread recovery ... 200 ms vs 200 ms → 0.5                             (weight 0.10)
        ///   magnitude ......... (30×0.5 + 2×(5 + 0.5) → 26/34) / 5 = 0.152941        (weight 0.10)
        ///   score = (0.05 + 0.0152941) / 0.20 = 0.3264706
        ///
        /// Defects this catches: the first cycle publishing; the recovery ratio, the magnitude
        /// ratio, or their weights being wrong; the omitted trade component still carrying weight.
        /// </summary>
        [Fact]
        public void MRCalculation_SpreadOnly_NoDepth_CalculatesCorrectly()
        {
            using var clock = new FixedClock();
            var mrCalc = new MarketResilienceCalculator(_settings);

            // Feed baseline
            for (int i = 0; i < 30; i++)
            {
                mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
                mrCalc.OnTrade(new Trade { Size = 10, Price = 500.25m, Timestamp = HelperTimeProvider.Now });
            }

            // First cycle: trade + spread (but NO depth shock). Seeds, publishes nothing.
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            clock.Advance(200);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));
            Assert.Equal(1m, mrCalc.CurrentMRScore);

            // Second cycle, measured against the first.
            clock.Advance(10_000);
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 500, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(CreateOrderBook(5m, 495, 500));
            clock.Advance(200);
            mrCalc.OnOrderBookUpdate(CreateOrderBook(0.5m, 500, 500.5m));

            Assert.Equal(0.326470588m, mrCalc.CurrentMRScore, 6);
        }
    }

    public class DepthDepletionRecoveryTests
    {
        private static readonly DateTime ClockStart = new DateTime(2024, 3, 1, 14, 31, 0, DateTimeKind.Local);

        private PlugInSettings _settings;

        public DepthDepletionRecoveryTests()
        {
            _settings = new PlugInSettings()
            {
                MaxShockMsTimeout = 500
            };
        }

        /// <summary>Pins the shared time provider; disposing restores wall time.</summary>
        private sealed class FixedClock : IDisposable
        {
            public FixedClock() => HelperTimeProvider.SetFixedTime(ClockStart);
            public void Advance(long milliseconds) => HelperTimeProvider.IncrementByMilliseconds(milliseconds);
            public void Dispose() => HelperTimeProvider.ResetToSystemTime();
        }

        // Test utilities
        private OrderBookSnapshot BuildLOB((decimal px, double sz)[] asks, (decimal px, double sz)[] bids)
        {
            var ob = new OrderBook();
            
            var askItems = asks.Select(a => new BookItem { 
                Price = (double)a.px, 
                Size = a.sz, 
                IsBid = false,
                LocalTimeStamp = DateTime.Now,
                ServerTimeStamp = DateTime.Now
            }).ToArray();
            
            var bidItems = bids.Select(b => new BookItem { 
                Price = (double)b.px, 
                Size = b.sz, 
                IsBid = true,
                LocalTimeStamp = DateTime.Now,
                ServerTimeStamp = DateTime.Now
            }).ToArray();
            
            ob.LoadData(askItems, bidItems);
            var snapshot = new OrderBookSnapshot();
            snapshot.UpdateFrom(ob);
            return snapshot;
        }

        private void WarmUp(MarketResilienceCalculator calc, int frames = 300)
        {
            // Warm up with realistic micro-noise to properly train the MAD estimator
            // This ensures the detector learns what "normal" market variance looks like
            var random = new Random(42); // Fixed seed for test reproducibility

            for (int i = 0; i < frames; i++)
            {
                var lob = BuildLOB(
                    asks: new[] { 
                // Each level gets independent micro-noise (±0.5 cent, ±2.5% size)
                (100.50m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.51m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.52m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    },
                    bids: new[] {
                (100.49m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.48m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.47m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    }
                );
                calc.IsLOBDepleted(lob); // Train both median and MAD estimators
            }
        }

        private void ActivateIfNeeded(MarketResilienceCalculator calc, OrderBookSnapshot lob)
        {
            var side = calc.IsLOBDepleted(lob);
            if (side != eLOBSIDE.NONE)
            {
                calc.ActivateDepthEvent(lob, side);
            }
        }

        // A. Depletion detection (edge-triggered)
        [Fact]
        public void IsLOBDepleted_BidInnerWipe_OuterRefill_FiresBid()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // ✅ FIXED: Create TRUE depletion scenario
            // Inner bid levels disappear, replaced by FEWER levels at WORSE prices with LESS total size
            var depletedLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 80.0), (100.52m, 60.0) }, // Asks unchanged
                bids: new[] {
            (100.40m, 50.0),  // 9 cents worse, much smaller
            (100.39m, 30.0)   // Even worse, even smaller
                              // Total: 80 vs baseline ~280 → clear depletion
                }
            );

            var result = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.BID, result);

            // Second call should return NONE (edge-triggered)
            var secondResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);
        }
        [Fact]
        public void IsLOBDepleted_AskInnerWipe_OuterRefill_FiresAsk()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // ✅ FIXED: Create TRUE depletion scenario
            // Inner ask levels disappear, replaced by FEWER levels at WORSE prices with LESS total size
            var depletedLob = BuildLOB(
                asks: new[] {
            (100.60m, 50.0),  // 10 cents worse, much smaller
            (100.61m, 30.0)   // Even worse, even smaller
                              // Total: 80 vs baseline ~280 → clear depletion
                },
                bids: new[] { (100.49m, 100.0), (100.48m, 80.0), (100.47m, 60.0) } // Bids unchanged
            );

            var result = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.ASK, result);

            // Second call should return NONE (edge-triggered)
            var secondResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);
        }

        [Fact]
        public void IsLOBDepleted_BothSidesThinned_FiresBoth()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Create depletion scenario: both sides significantly thinned
            var depletedLob = BuildLOB(
                asks: new[] { (100.60m, 20.0) }, // Much worse and smaller
                bids: new[] { (100.40m, 20.0) }  // Much worse and smaller
            );

            var result = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.BOTH, result);

            // Second call should return NONE (edge-triggered)
            var secondResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);
        }

        [Fact]
        public void IsLOBDepleted_DuringWarmup_DoesNotFire()
        {
            var calc = new MarketResilienceCalculator(_settings);
            
            // During warm-up, create dramatic change that would trigger depletion after warm-up
            for (int i = 0; i < 50; i++) // Less than warmup samples
            {
                var lob = i < 25 
                    ? BuildLOB(
                        asks: new[] { (100.50m, 100.0), (100.51m, 80.0) },
                        bids: new[] { (100.49m, 100.0), (100.48m, 80.0) })
                    : BuildLOB(
                        asks: new[] { (100.60m, 20.0) }, // Dramatic change
                        bids: new[] { (100.40m, 20.0) }); // Dramatic change
                
                var result = calc.IsLOBDepleted(lob);
                Assert.Equal(eLOBSIDE.NONE, result); // Should not fire during warm-up
                
                Thread.Sleep(10);
            }
        }

        [Fact]
        public void IsLOBDepleted_SmallNoisyChurn_DoesNotFire()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // ✅ FIXED: Small INDEPENDENT random changes around baseline
            // Matches the warm-up structure (3 levels) with independent micro-noise
            var random = new Random(42);
            for (int i = 0; i < 50; i++)
            {
                // Generate independent noise for each level (same pattern as warm-up)
                var lob = BuildLOB(
                    asks: new[] {
                (100.50m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.51m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.52m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    },
                    bids: new[] {
                (100.49m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.48m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.47m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    }
                );

                var result = calc.IsLOBDepleted(lob);
                Assert.Equal(eLOBSIDE.NONE, result);

                Thread.Sleep(10);
            }
        }

        // B. Recovery (dynamic + timeout)
        [Fact]
        public void IsLOBRecovered_SameSideEarlyRecovery_ReportsBid()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // ✅ QUALITY FIX: Trigger BID depletion with realistic 3-level structure
            // Create clear depletion that will actually trigger
            var depletedLob = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // Ask side normal (3 levels matching warm-up)
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.40m, 50.0),   // Bid depleted: 9 cents worse, half size
            (100.39m, 30.0)    // Only 2 levels vs baseline 3
                }
            );

            // Verify BID depletion triggered before testing recovery
            var depletionResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.BID, depletionResult);

            // Activate the event
            calc.ActivateDepthEvent(depletedLob, depletionResult);

            // ✅ Quickly restore bid liquidity to FULL baseline (tests same-side recovery)
            Thread.Sleep(100);
            var recoveredLob = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // Asks unchanged
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.49m, 100.0),  // Bid restored to baseline
            (100.48m, 100.0),  // Full 3 levels
            (100.47m, 100.0)   // Same-side recovery ≥90%
                }
            );

            // ✅ TEST: BID recovery should be detected (same side that was depleted)
            var result = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.BID, result);

            // Second call should return NONE (edge-triggered)
            var secondResult = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);
        }

        /// <summary>
        /// Only the DEPLETED side is measured. The ask growing while the bid is still gone is a
        /// price move, not a recovery: the event stays open until the bid itself comes back.
        ///
        /// Defect this catches: the untouched side's growth being credited as the recovery, which
        /// would close a bid depletion on the ask's first uptick.
        /// </summary>
        [Fact]
        public void IsLOBRecovered_OppositeSideImprovesFirst_IsNotARecovery()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            var depletedLob = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // Ask side normal (3 levels matching warm-up)
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.40m, 50.0),   // Bid severely depleted (9 cents worse, half size)
            (100.39m, 30.0)    // Only 2 levels vs baseline 3
                }
            );

            // Verify BID depletion triggered
            var depletionResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.BID, depletionResult);

            // Activate the event
            calc.ActivateDepthEvent(depletedLob, depletionResult);

            // ASK side improves well past its baseline while bids stay weak.
            var askImprovedLob = BuildLOB(
                asks: new[] {
            (100.49m, 110.0),  // ASK improved: 1 cent better + more size
            (100.50m, 110.0),
            (100.51m, 110.0)
                },
                bids: new[] {
            (100.40m, 50.0),   // Bids still weak (unchanged)
            (100.39m, 30.0)
                }
            );

            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(askImprovedLob));
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(askImprovedLob));

            // The bid itself comes back: that is the recovery.
            var bidRestoredLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            Assert.Equal(eLOBSIDE.BID, calc.IsLOBRecovered(bidRestoredLob));

            // Second call should return NONE (edge-triggered)
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(bidRestoredLob));
        }

        [Fact]
        public void IsLOBRecovered_BothSidesRecoverStrongly_ReportsBoth()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Trigger BOTH sides depletion with extreme scenario
            // This creates a clear, measurable depletion baseline
            var depletedLob = BuildLOB(
                asks: new[] { (100.60m, 10.0) }, // 10 cents worse, tiny size
                bids: new[] { (100.40m, 10.0) }  // 9 cents worse, tiny size
            );

            var depletionResult = calc.IsLOBDepleted(depletedLob);

            // Verify depletion actually triggered before testing recovery
            Assert.Equal(eLOBSIDE.BOTH, depletionResult);

            // Now activate the event
            if (depletionResult != eLOBSIDE.NONE)
            {
                calc.ActivateDepthEvent(depletedLob, depletionResult);
            }

            // ✅ QUALITY FIX: Both sides recover STRONGLY and COMPLETELY
            // This tests that the recovery logic properly detects:
            // 1. Sufficient depth restoration (≥90% recovery)
            // 2. Both sides recovering simultaneously
            // 3. Recovery meeting the multi-side threshold
            Thread.Sleep(200);
            var recoveredLob = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // level 1: full baseline restoration
            (100.51m, 100.0),  // level 2: full baseline restoration
            (100.52m, 100.0)   // level 3: full baseline restoration
                },
                bids: new[] {
            (100.49m, 100.0),  // level 1: full baseline restoration
            (100.48m, 100.0),  // level 2: full baseline restoration
            (100.47m, 100.0)   // level 3: full baseline restoration
                }
            );

            var result = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.BOTH, result);

            // Second call should return NONE (edge-triggered)
            var secondResult = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);
        }


        /// <summary>
        /// A partial refill is not a recovery. The depleted bid climbs from its trough to about a
        /// third of what it lost; the 90% target is not met, so the event stays open. There is no
        /// timeout inside this check: the recovery window belongs to the caller, which closes the
        /// event and scores it as a non-recovery when the window runs out.
        ///
        /// Defects this catches: a partial refill being reported as the recovery; a "dominant
        /// side" fallback that reports a side which never reached the target.
        /// </summary>
        [Fact]
        public void IsLOBRecovered_PartialRefill_IsNotARecovery()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Trigger BID depletion
            var depletedLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.40m, 50.0), (100.39m, 30.0) }
            );
            var depletionResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.BID, depletionResult);
            calc.ActivateDepthEvent(depletedLob, depletionResult);

            // Bid improves, but only part of the way back (one level at 85 against ~136 usual).
            var partialRefillLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.45m, 85.0) }
            );

            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(partialRefillLob));
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(partialRefillLob));

            // The full refill is the recovery.
            var restoredLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );
            Assert.Equal(eLOBSIDE.BID, calc.IsLOBRecovered(restoredLob));
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(restoredLob));
        }

        [Fact]
        public void IsLOBRecovered_EdgeTriggered_NoDoubleFire()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // ✅ QUALITY FIX: Create realistic depletion scenario that ACTUALLY triggers
            // Use 3 levels (matching warm-up) but with severely depleted bid side
            var depletedLob = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // Ask side normal (3 levels)
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.40m, 50.0),   // Bid depleted: 9 cents worse, half size
            (100.39m, 30.0)    // Even worse
                               // Missing level 3 - only 2 levels vs baseline 3
                }
            );

            // Explicitly verify depletion triggered before testing recovery
            var depletionResult = calc.IsLOBDepleted(depletedLob);
            Assert.Equal(eLOBSIDE.BID, depletionResult);

            // Now activate
            calc.ActivateDepthEvent(depletedLob, depletionResult);

            // ✅ Recovery: Restore bid side to baseline (3 levels)
            var recoveredLob = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.49m, 100.0),  // Restored to baseline
            (100.48m, 100.0),  // 3 levels
            (100.47m, 100.0)
                }
            );

            // ✅ TEST: First call should return BID (recovery detected)
            var firstResult = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.BID, firstResult);

            // ✅ TEST: Second call should return NONE (edge-triggered)
            var secondResult = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);
        }

        // C. Concurrency / overlap policy
        [Fact]
        public void NewDepletionWhileActive_IgnoredByPolicy()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // ✅ QUALITY FIX: First depletion - create realistic scenario that ACTUALLY triggers
            var firstDepletion = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // Ask side normal (3 levels)
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.40m, 50.0),   // Bid depleted: 9 cents worse, half size
            (100.39m, 30.0)    // Only 2 levels vs baseline 3
                }
            );

            // Verify BID depletion triggered
            var firstResult = calc.IsLOBDepleted(firstDepletion);
            Assert.Equal(eLOBSIDE.BID, firstResult);

            // Policy layer: activate if no active event
            if (firstResult != eLOBSIDE.NONE)
            {
                calc.ActivateDepthEvent(firstDepletion, firstResult);
            }

            // ✅ Second depletion while first is active - create ASK depletion scenario
            // Policy layer should NOT re-activate while event is already active
            Thread.Sleep(100);
            var secondDepletion = BuildLOB(
                asks: new[] {
            (100.60m, 50.0),   // ASK depletion: 10 cents worse
            (100.61m, 30.0)    // Only 2 levels
                },
                bids: new[] {
            (100.40m, 50.0),   // Bids still depleted (unchanged)
            (100.39m, 30.0)
                }
            );

            // IsLOBDepleted might detect ASK depletion, but we don't activate again (policy)
            var secondResult = calc.IsLOBDepleted(secondDepletion);
            // Policy layer: do NOT call ActivateDepthEvent again while event is active

            // ✅ Recovery should still work for original BID event with proper 3-level structure
            Thread.Sleep(100);
            var recovery = BuildLOB(
                asks: new[] {
            (100.50m, 100.0),  // Asks back to normal
            (100.51m, 100.0),
            (100.52m, 100.0)
                },
                bids: new[] {
            (100.49m, 100.0),  // BID fully restored
            (100.48m, 100.0),  // 3 levels
            (100.47m, 100.0)
                }
            );

            var recoveryResult = calc.IsLOBRecovered(recovery);
            Assert.Equal(eLOBSIDE.BID, recoveryResult); // Original BID event finalizes correctly

            // Edge-triggered check
            var secondRecoveryResult = calc.IsLOBRecovered(recovery);
            Assert.Equal(eLOBSIDE.NONE, secondRecoveryResult);
        }
        // D. Corner cases
        [Fact]
        public void ZeroSpread_Guards_NoNaN()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Zero spread scenario - ask=bid
            var zeroSpreadLob = BuildLOB(
                asks: new[] { (100.50m, 100.0) },
                bids: new[] { (100.50m, 100.0) } // Same price = zero spread
            );

            // Should not throw or produce NaN
            var result = calc.IsLOBDepleted(zeroSpreadLob);
            // Result should be a valid enum value (no NaN/exceptions)
            Assert.True(Enum.IsDefined(typeof(eLOBSIDE), result));
        }

        [Fact]
        public void EmptyBidSide_TriggersAndRecovers()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Empty bid side (swept)
            var emptyBidLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new (decimal, double)[0]
            );

            var result = calc.IsLOBDepleted(emptyBidLob);
            Assert.Equal(eLOBSIDE.BID, result);

            // ✅ Manually activate with the result we already got
            if (result != eLOBSIDE.NONE)
            {
                calc.ActivateDepthEvent(emptyBidLob, result);
            }

            // Edge-triggered check
            var secondResult = calc.IsLOBDepleted(emptyBidLob);
            Assert.Equal(eLOBSIDE.NONE, secondResult);

            // Add back bid liquidity
            Thread.Sleep(200);
            var recoveredLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            var recoveryResult = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.BID, recoveryResult);

            // Edge-triggered check
            var secondRecoveryResult = calc.IsLOBRecovered(recoveredLob);
            Assert.Equal(eLOBSIDE.NONE, secondRecoveryResult);
        }

        /// <summary>
        /// A book too thin to show the usual number of levels does not short-circuit into a
        /// recovery. While it stays thin the event stays open, however many times it is asked;
        /// it closes only when the depth actually comes back (or, at the calculator level, when
        /// the window runs out and the event is scored as a non-recovery).
        ///
        /// Defect this catches: a guard on level count that reports the depleted side as
        /// recovered when there are too few levels to measure.
        /// </summary>
        [Fact]
        public void InsufficientTopN_WithoutRefill_IsNotReportedAsRecovered()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Book too thin to reach typical Q - trigger depletion with minimal visible book
            var minimalLob = BuildLOB(
                asks: new[] { (100.60m, 20.0) }, // Single level, far from market
                bids: new[] { (100.40m, 20.0) }  // Single level, far from market
            );
            var depletionResult = calc.IsLOBDepleted(minimalLob);
            Assert.Equal(eLOBSIDE.BOTH, depletionResult);
            calc.ActivateDepthEvent(minimalLob, depletionResult);

            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(minimalLob));
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(minimalLob));

            // The event is still live: a full refill on both sides closes it.
            var restoredLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );
            Assert.Equal(eLOBSIDE.BOTH, calc.IsLOBRecovered(restoredLob));
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBRecovered(restoredLob));
        }

        [Fact]
        public void BaselineShift_PostWarmup_NoSpuriousTriggers()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc); // ~300 frames at 100.49/100.50, inner sizes ~100

            // Regime shift: move the whole book up gradually BUT keep spread and inner mass stable
            // Baseline spread assumed ~0.01; preserve that throughout the shift.
            for (int i = 0; i < 60; i++)
            {
                decimal mid = 100.495m + i * 0.005m; // small +0.005 steps
                decimal bestBid = mid - 0.005m;      // spread = 0.01 preserved
                decimal bestAsk = mid + 0.005m;

                // Keep inner levels’ sizes close to baseline so immediacy near the touch doesn’t drop
                var shifted = BuildLOB(
                    asks: new[] { (bestAsk, 100d), (bestAsk + 0.01m, 100d), (bestAsk + 0.02m, 100d) },
                    bids: new[] { (bestBid, 100d), (bestBid - 0.01m, 100d), (bestBid - 0.02m, 100d) }
                );

                var side = calc.IsLOBDepleted(shifted);
                Assert.Equal(eLOBSIDE.NONE, side);
            }

            // Now prove the detector still fires on a real depletion
            // Make bids essentially empty near the touch; keep asks normal.
            // This is an unmistakable immediacy collapse on the BID side.
            var obviousDepletion = BuildLOB(
                asks: new[] { (100.505m, 100d), (100.515m, 100d), (100.525m, 100d) }, // keep asks normal
                bids: new[] { (100.475m, 5d), (100.465m, 5d), (100.455m, 5d) } // Move best bid out by 2 ticks (if your baseline spread ≈ 0.01) and slash sizes
            );
            var depletionResult = calc.IsLOBDepleted(obviousDepletion);
            Assert.NotEqual(eLOBSIDE.NONE, depletionResult); // must trigger
            Assert.Equal(eLOBSIDE.NONE, calc.IsLOBDepleted(obviousDepletion)); // edge-trigger
        }


        // E. Stress
        [Fact]
        public void HighFrequencyNoisyUpdates_StableUnderLoad()
        {
            var calc = new MarketResilienceCalculator(_settings);
            WarmUp(calc);

            // Run 1000 updates with REALISTIC but INDEPENDENT noise per level
            var random = new Random(42); // Fixed seed for reproducibility
            int depletionCount = 0;

            for (int i = 0; i < 1000; i++)
            {
                // ✅ FIX: Generate INDEPENDENT noise for each level
                // This simulates realistic order book microstructure where
                // different levels churn independently

                var lob = BuildLOB(
                    asks: new[] {
                // Each level gets its own independent noise
                (100.50m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(90, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.51m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(90, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.52m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(90, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    },
                    bids: new[] {
                // Each level gets its own independent noise
                (100.49m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(90, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.48m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(90, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05))),

                (100.47m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(90, 100.0 * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    }
                );

                var result = calc.IsLOBDepleted(lob);
                if (result != eLOBSIDE.NONE)
                {
                    depletionCount++;
                }

                Thread.Sleep(1); // High frequency updates
            }

            // ✅ With independent micro-noise, depletion triggers should be rare (< 1% of frames)
            Assert.True(depletionCount < 10,
                $"Too many depletions ({depletionCount}/1000 = {depletionCount / 10.0}%) - threshold too sensitive");
        }

        [Fact]
        public void MRCalculation_WithDepthShock_CalculatesCorrectly()
        {
            using var clock = new FixedClock();
            var mrCalc = new MarketResilienceCalculator(_settings);

            // ✅ FIX: Warm up ALL baselines (depth, spread, AND trade)
            var random = new Random(42);
            for (int i = 0; i < 300; i++)
            {
                var lob = BuildLOB(
                    asks: new[] {
                (100.50m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                (100.51m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                (100.52m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    },
                    bids: new[] {
                (100.49m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                (100.48m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05))),
                (100.47m + (decimal)(random.NextDouble() - 0.5) * 0.01m,
                 Math.Max(95, 100d * (1.0 + (random.NextDouble() - 0.5) * 0.05)))
                    }
                );
                mrCalc.OnOrderBookUpdate(lob);

                // ✅ ADD: Train trade size baseline
                mrCalc.OnTrade(new Trade
                {
                    Size = 100,  // Normal baseline trade size
                    Price = 100.49m,
                    Timestamp = DateTime.Now
                });
            }

            var depletedLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.40m, 50.0), (100.39m, 30.0) } // Depleted; spread 0.10 vs ~0.01 usual
            );
            var recoveredLob = BuildLOB(
                asks: new[] { (100.50m, 100.0), (100.51m, 100.0), (100.52m, 100.0) },
                bids: new[] { (100.49m, 100.0), (100.48m, 100.0), (100.47m, 100.0) }
            );

            // Two identical cycles, 200 ms recovery each. The first seeds the history and
            // publishes nothing; the second is measured against it, so both recovery
            // components score exactly 0.5. Trade severity is omitted (one-size prints):
            //   (0.10·0.5 + 0.50·0.5 + 0.10·magnitude) / 0.70, magnitude = ~0.011 / 0.10 ≈ 0.11
            //   ≈ 0.444
            // Defects this catches: the first cycle publishing; the depth recovery ratio not
            // being 0.5 for an equal duration; the 0.50 depth weight being wrong.
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(depletedLob);
            clock.Advance(200);
            mrCalc.OnOrderBookUpdate(recoveredLob);
            Assert.Equal(1m, mrCalc.CurrentMRScore);

            clock.Advance(10_000);
            mrCalc.OnTrade(new Trade { Size = 5000, Price = 100.49m, Timestamp = HelperTimeProvider.Now });
            mrCalc.OnOrderBookUpdate(depletedLob);
            clock.Advance(200);
            mrCalc.OnOrderBookUpdate(recoveredLob);

            Assert.NotEqual(1m, mrCalc.CurrentMRScore);
            Assert.InRange(mrCalc.CurrentMRScore, 0.43m, 0.46m);
        }
    }
}
