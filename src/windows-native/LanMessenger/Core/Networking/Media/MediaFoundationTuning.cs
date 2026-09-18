using LanMessenger.Core.Services;
using Vortice.MediaFoundation;

namespace LanMessenger.Core.Networking.Media;

// Settings that apply to both halves of the codec pair.
//
// Its own file rather than a helper on one of them: the encoder reaching into
// the decoder for a shared tweak is the kind of coupling that looks harmless
// until one of the two is compiled without the other.

internal static class MediaFoundationTuning
{
    internal static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// Asks a transform to emit as soon as it can rather than when it would
    /// prefer to.
    ///
    /// Both ends of the pair buffer by default, and for good reason in their
    /// usual job: a decoder holds a DPB so B-frames can be reordered, and a CBR
    /// encoder holds frames so its rate controller has a window to work
    /// against. For playback that delay is invisible. For a remote desktop it is
    /// the entire product — the picture is correct and two seconds late, which
    /// is the same as being wrong.
    ///
    /// Best-effort. An MFT that refuses is a slower session, not a broken one,
    /// so this never throws; but it says so, because a silent refusal here looks
    /// exactly like latency nobody can explain.
    internal static void TrySetLowLatency(IMFTransform transform, string which)
    {
        try
        {
            var attributes = transform.Attributes;
            if (attributes is null)
            {
                LanLogger.Remote("encoder_property_unsupported",
                                 reason: $"{which} exposes no attributes for MF_LOW_LATENCY");
                return;
            }
            attributes.Set(MF_LOW_LATENCY, 1u);
            LanLogger.Remote("low_latency_set", reason: which);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("encoder_property_unsupported",
                             reason: $"{which} MF_LOW_LATENCY: {ex.Message}");
        }
    }
}
