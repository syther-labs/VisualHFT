using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.UserSettings;

namespace VisualHFT.Studies.MarketRatios.Model
{
    public class PlugInSettings : ISetting
    {
        public string Symbol { get; set; }
        public Provider Provider { get; set; }
        public AggregationLevel AggregationLevel { get; set; }

        /// <summary>
        /// Selects the volume form of the ratio — size posted against size executed — instead of
        /// the message-count form. Off by default, so a saved settings file written before this
        /// option existed keeps the behaviour it already had.
        /// </summary>
        public bool UseVolumeForm { get; set; }
    }
}
