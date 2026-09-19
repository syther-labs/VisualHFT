using System.ComponentModel;

namespace VisualHFT.Studies.LOBImbalance.Model
{
    public enum ImbalanceWeighting
    {
        // Must stay first (value 0): a settings file saved before this setting existed
        // deserializes a missing field to the enum default, which must be today's behaviour.
        [Description("Equal Weight")]
        EqualWeight,

        [Description("Touch Weighted")]
        TouchWeighted
    }
}
