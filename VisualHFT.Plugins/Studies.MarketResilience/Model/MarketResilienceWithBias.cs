using System;
using VisualHFT.Enums;
using VisualHFT.Studies.MarketResilience.Model;

namespace Studies.MarketResilience.Model
{

    public class MarketResilienceWithBias : MarketResilienceCalculator
    {
        private const double MRB_ON = 0.30;  // only speak when resilience clearly poor
        private const double MRB_OFF = 0.50;  // stop speaking once resilience improves
        private bool _mrbArmed = false;       // hysteresis latch

        public MarketResilienceWithBias(PlugInSettings settings): base(settings)
        {

        }
        protected override eMarketBias? CalculateMRBias()
        {
            // The latch tracks the resilience score, so it is updated on EVERY scored event -
            // including one with no depth outcome, such as a spread that widened and came back.
            // Evaluating it after the depth guard below would leave the arrow armed through a
            // recovery it should have cleared, and both the tooltip and the catalogue promise it
            // clears when resilience returns.
            double mr = (double)CurrentMRScore;

            // Hysteresis: arm when MR ≤ MRB_ON; disarm when MR ≥ MRB_OFF
            if (!_mrbArmed && mr <= MRB_ON) _mrbArmed = true;
            if (_mrbArmed && mr >= MRB_OFF) { _mrbArmed = false; return eMarketBias.Neutral; }

            // Direction comes from the depth outcome only. No depth event, or one that has not
            // concluded, says nothing about direction.
            if (ShockDepth == null || ShockDepth.Value == eLOBSIDE.NONE) return null;
            if (RecoveredDepth == null && !DepthWindowClosed) return null;

            if (!_mrbArmed) return null; // don’t speak when resilience is middling/good

            // Only the depleted side(s) count. A side that was consumed and never redeployed inside
            // the window is where the market is likely to move; a side that came back says nothing.
            eLOBSIDE failed = ShockDepth.Value & ~DepthSidesRecovered;

            if (failed == eLOBSIDE.BID) return eMarketBias.Bearish;
            if (failed == eLOBSIDE.ASK) return eMarketBias.Bullish;

            // Every depleted side redeployed, or both failed together ⇒ no directional claim
            return eMarketBias.Neutral;
        }
    }
}
