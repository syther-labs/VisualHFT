using System;
using System.Collections.Generic;
using System.Threading;
using VisualHFT.Helpers;
using VisualHFT.Model;
using Xunit;

namespace VisualHFT.Commons.Tests
{
    /// <summary>
    /// Dispatch contract for <c>HelperTrade</c>.
    ///
    /// A subscriber on this stream is study-shaped code running on the market connector's
    /// producer thread, at a call site that does not guard itself. Two things follow, and each is
    /// a fact below:
    ///   1. an exception that escapes <c>UpdateData</c> reaches a non-UI thread unhandled, which
    ///      terminates the process with no dialog and no log entry;
    ///   2. an escaping exception also unwinds the dispatch loop, so ONE faulting subscriber
    ///      starves every subscriber after it of trade data.
    ///
    /// A subscriber throwing is a normal operating condition on a hot path, not a reason to kill
    /// the host. Dispatch isolates each subscriber and continues to the next. Isolating must not
    /// mean silencing, so the fault is raised on <c>OnException</c> - the last fact group covers
    /// that, because swallowing would trade a crash for an invisible data outage.
    ///
    /// Shared-state note: <c>HelperTrade</c> is a process-wide singleton, so every fact resets the
    /// subscriber list before and in a finally, otherwise subscribers leak into sibling tests.
    /// </summary>
    public class HelperTradeDispatchIsolationTests
    {
        private static Trade NewTrade() => new Trade { Size = 100m, Price = 500.25m, Timestamp = DateTime.Now };

        [Fact]
        public void UpdateData_WhenASubscriberThrows_DoesNotPropagateToTheProducerThread()
        {
            // The producer here stands in for the market connector's unguarded call site: a throw
            // that reaches it takes the process down in production.
            HelperTrade.Instance.Reset();
            try
            {
                HelperTrade.Instance.Subscribe(_ => throw new InvalidOperationException("faulting study"));

                Exception escaped = Record.Exception(() => HelperTrade.Instance.UpdateData(NewTrade()));

                Assert.Null(escaped);
            }
            finally
            {
                HelperTrade.Instance.Reset();
            }
        }

        [Fact]
        public void UpdateData_WhenAnEarlySubscriberThrows_StillDispatchesToTheRemainingSubscribers()
        {
            HelperTrade.Instance.Reset();
            try
            {
                var delivered = new List<string>();
                HelperTrade.Instance.Subscribe(_ => delivered.Add("first"));
                HelperTrade.Instance.Subscribe(_ => throw new InvalidOperationException("faulting study"));
                HelperTrade.Instance.Subscribe(_ => delivered.Add("third"));

                HelperTrade.Instance.UpdateData(NewTrade());

                // "third" is the assertion that matters: it is the subscriber the foreach-unwind
                // starves today.
                Assert.Contains("first", delivered);
                Assert.Contains("third", delivered);
            }
            finally
            {
                HelperTrade.Instance.Reset();
            }
        }

        [Fact]
        public void UpdateData_WhenASubscriberThrows_StillSurfacesTheFaultViaOnException()
        {
            // Isolating the fault must not SILENCE it — swallowing would trade a crash for an
            // invisible data outage.
            HelperTrade.Instance.Reset();
            var raised = new ManualResetEventSlim(false);
            Action<VisualHFT.Commons.Model.ErrorEventArgs> handler = _ => raised.Set();
            HelperTrade.Instance.OnException += handler;
            try
            {
                HelperTrade.Instance.Subscribe(_ => throw new InvalidOperationException("faulting study"));

                HelperTrade.Instance.UpdateData(NewTrade());

                Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "OnException was never raised for the faulting subscriber.");
            }
            finally
            {
                HelperTrade.Instance.OnException -= handler;
                HelperTrade.Instance.Reset();
                raised.Dispose();
            }
        }

        [Fact]
        public void UpdateData_WhenEverySubscriberThrows_StillDoesNotPropagate()
        {
            HelperTrade.Instance.Reset();
            try
            {
                HelperTrade.Instance.Subscribe(_ => throw new InvalidOperationException("study A"));
                HelperTrade.Instance.Subscribe(_ => throw new InvalidOperationException("study B"));

                Exception escaped = Record.Exception(() => HelperTrade.Instance.UpdateData(NewTrade()));

                Assert.Null(escaped);
            }
            finally
            {
                HelperTrade.Instance.Reset();
            }
        }
    }
}
