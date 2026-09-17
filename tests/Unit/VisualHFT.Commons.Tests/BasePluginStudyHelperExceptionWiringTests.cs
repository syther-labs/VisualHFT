using System;
using System.Threading;
using System.Threading.Tasks;
using VisualHFT.Commons.PluginManager;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using Xunit;

namespace VisualHFT.Commons.Tests
{
    /// <summary>
    /// The market-data helpers isolate a faulting subscriber and raise <c>OnException</c> instead
    /// of letting the exception reach the producer thread. Isolating is only half the contract:
    /// the study that faulted must also be STOPPED and marked failed, otherwise it silently stops
    /// producing while still looking healthy in the UI.
    ///
    /// <c>BasePluginStudy</c> is the consumer of that signal. When the faulting subscriber belongs
    /// to this study instance, it stops the study and sets <c>STOPPED_FAILED</c>. These facts
    /// assert that this happens for BOTH streams - a study can subscribe to either one, and a
    /// fault on the trade stream must have the same consequence as one on the order-book stream.
    ///
    /// Shared-state note: both helpers are process-wide singletons, so every fact resets the
    /// subscriber list in a finally, otherwise subscribers leak into sibling tests.
    /// </summary>
    public class BasePluginStudyHelperExceptionWiringTests
    {
        /// <summary>
        /// Both handlers are METHODS ON THE STUDY, not lambdas: the helper reports
        /// <c>subscriber.Target</c> as the fault context, and the study only reacts to a fault whose
        /// context is itself. A closure would carry the wrong target and the check would not fire —
        /// which is exactly how a real study subscribes.
        /// </summary>
        private sealed class FaultingProbeStudy : BasePluginStudy
        {
            public override event EventHandler<decimal> OnAlertTriggered
            {
                add { }
                remove { }
            }

            public override string Name { get; set; } = "FaultingProbe";
            public override string Version { get; set; } = "1.0";
            public override string Description { get; set; } = "Probe that throws on incoming data";
            public override string Author { get; set; } = "Test";
            public override ISetting Settings { get; set; }
            public override Action CloseSettingWindow { get; set; } = () => { };
            public override string TileTitle { get; set; } = "Probe";
            public override string TileToolTip { get; set; } = "Probe that throws on incoming data";

            public void OnTradeReceived(Trade trade) => throw new InvalidOperationException("faulting study");

            public void OnOrderBookReceived(OrderBook book) => throw new InvalidOperationException("faulting study");

            protected override void LoadSettings() => Settings = new ProbeStudySetting();

            protected override void SaveSettings() { }

            protected override void InitializeDefaultSettings() => Settings = new ProbeStudySetting();

            public override object GetUISettings() => null;
        }

        private sealed class ProbeStudySetting : ISetting
        {
            public string Symbol { get; set; } = "TEST";
            public Provider Provider { get; set; } = new Provider { ProviderCode = 1, ProviderName = "Test" };
            public AggregationLevel AggregationLevel { get; set; } = AggregationLevel.Ms100;
        }

        /// <summary>The helper raises OnException on the thread pool, so the status change is not
        /// observable on the calling thread the instant UpdateData returns.</summary>
        private static bool WaitForStatus(BasePluginStudy study, ePluginStatus expected, int timeoutMs = 5000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (study.Status == expected)
                    return true;
                Thread.Sleep(10);
            }
            return study.Status == expected;
        }

        // The order-book path, as the control: it proves this probe shape really does reach the
        // handler, so a red on the trade fact below is about the wiring and not about the fixture.
        [Fact]
        public void WhenAStudyThrowsOnAnOrderBookUpdate_TheStudyIsStoppedAndMarkedFailed()
        {
            HelperOrderBook.Instance.Reset();
            using var study = new FaultingProbeStudy();
            try
            {
                HelperOrderBook.Instance.Subscribe(study.OnOrderBookReceived);

                HelperOrderBook.Instance.UpdateData(new OrderBook());

                Assert.True(WaitForStatus(study, ePluginStatus.STOPPED_FAILED),
                    $"The study was left at {study.Status} after faulting on an order-book update.");
            }
            finally
            {
                HelperOrderBook.Instance.Reset();
            }
        }

        // The trade path must behave identically. Without the trade helper's OnException wired into
        // the study, the fault is logged and raised but nothing consumes it: the study keeps its
        // previous status and goes on presenting itself as healthy while receiving no trade data.
        [Fact]
        public void WhenAStudyThrowsOnATrade_TheStudyIsStoppedAndMarkedFailed()
        {
            HelperTrade.Instance.Reset();
            using var study = new FaultingProbeStudy();
            try
            {
                HelperTrade.Instance.Subscribe(study.OnTradeReceived);

                HelperTrade.Instance.UpdateData(new Trade { Size = 100m, Price = 500.25m, Timestamp = DateTime.Now });

                Assert.True(WaitForStatus(study, ePluginStatus.STOPPED_FAILED),
                    $"The study was left at {study.Status} after faulting on a trade.");
            }
            finally
            {
                HelperTrade.Instance.Reset();
            }
        }
    }
}
