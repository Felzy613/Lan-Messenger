using LanMessenger.Core.Services;
using System.Runtime.InteropServices;

namespace LanMessenger.Core.Networking.Media;

// ICodecAPI, hand-rolled.
//
// This is the one COM interface Vortice.MediaFoundation does not project —
// confirmed by reflection over the package from the Mac before any of this was
// written, and confirmed again on real hardware by the WS0 probe, which found
// QueryInterface succeeding on both Quick Sync encoders. Everything else the
// encoder needs comes from Vortice; this is the single gap, which is why the
// plan's fallback of abandoning Vortice for CsWin32 was never needed.
//
// It matters because it carries the settings that make an encoder usable for
// remote control rather than for video: low-latency mode, the rate-control
// mode, the mean bitrate, the reference-frame count, and the force-keyframe
// that answers a viewer's keyframe_request.
//
// The vtable order below is not negotiable and is not checked by the compiler.
// COM dispatches by slot, so a method declared out of order calls whatever
// actually lives at that slot — with the arguments you supplied. Methods this
// code never calls are still declared, in order, for exactly that reason.

[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);

    [PreserveSig] int GetParameterRange(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] out object valueMin,
        [MarshalAs(UnmanagedType.Struct)] out object valueMax,
        [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);

    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out int valuesCount);

    [PreserveSig] int GetDefaultValue(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] out object value);

    [PreserveSig] int GetValue(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] out object value);

    [PreserveSig] int SetValue(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] ref object value);

    // Declared but unused. Present so the slots above resolve correctly.
    [PreserveSig] int RegisterForEvent(ref Guid api, IntPtr userData);
    [PreserveSig] int UnregisterForEvent(ref Guid api);
    [PreserveSig] int SetAllDefaults();
    [PreserveSig] int SetValueWithNotify(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] ref object value,
        out IntPtr changedParam, out int changedParamCount);
    [PreserveSig] int SetAllDefaultsWithNotify(out IntPtr changedParam, out int changedParamCount);
    [PreserveSig] int GetAllSettings(IntPtr stream);
    [PreserveSig] int SetAllSettings(IntPtr stream);
    [PreserveSig] int SetAllSettingsWithNotify(IntPtr stream,
        out IntPtr changedParam, out int changedParamCount);
}

/// The CODECAPI_* property GUIDs this app sets, written out literally for the
/// same reason the spike writes its attribute GUIDs literally: each one is
/// greppable against the Windows headers and depends on no binding's naming.
public static class CodecApiProperty
{
    /// Ask for the lowest latency the encoder can manage. On screen content this
    /// is the difference between a usable remote desktop and a laggy one.
    public static Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    /// 0 = CBR, 1 = peak-constrained VBR, 2 = unconstrained VBR, 3 = quality.
    public static Guid CommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static Guid CommonMeanBitRate = new("f7222b5a-5d14-4a48-8d68-9e6d7d1e7d4f");
    /// One reference frame. More is better for file compression and worse for
    /// latency, because it holds pictures back looking for a better encode.
    public static Guid VideoMaxNumRefFrame = new("964829ed-94f9-43b4-b74d-ef40944b69a0");
    /// How a viewer's keyframe_request is honoured.
    public static Guid VideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
}

public static class CodecApiExtensions
{
    private static Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    /// Queries an MFT for ICodecAPI, or null when it does not implement it.
    ///
    /// Not every encoder does, and a machine whose encoder does not is still a
    /// machine that can encode — just without the low-latency tuning. That is a
    /// worse session, not a broken one, so this returns null rather than throws.
    public static ICodecAPI? TryGetCodecApi(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return null;
        int hr = Marshal.QueryInterface(unknown, ref IID_ICodecAPI, out IntPtr codecApi);
        if (hr != 0 || codecApi == IntPtr.Zero) return null;
        try
        {
            return (ICodecAPI)Marshal.GetObjectForIUnknown(codecApi);
        }
        catch (Exception ex)
        {
            LanLogger.Remote("error", reason: $"ICodecAPI marshalling failed: {ex.Message}");
            return null;
        }
        finally
        {
            // GetObjectForIUnknown takes its own reference.
            Marshal.Release(codecApi);
        }
    }

    /// Sets one property, reporting rather than throwing.
    ///
    /// Encoder support varies by vendor and driver version, and a property this
    /// particular MFT does not implement is information rather than a failure —
    /// asserting on it turns a working session into a crash on somebody else's
    /// machine. This is the same reasoning as the macOS encoder tolerating
    /// kVTPropertyNotSupportedErr on every VTSessionSetProperty.
    public static bool TrySet(this ICodecAPI codec, Guid property, object value, string label)
    {
        try
        {
            int hr = codec.IsSupported(ref property);
            if (hr != 0)
            {
                LanLogger.Remote("encoder_property_unsupported", reason: label);
                return false;
            }
            object boxed = value;
            hr = codec.SetValue(ref property, ref boxed);
            if (hr != 0)
            {
                LanLogger.Remote("encoder_property_failed", reason: $"{label} (0x{hr:X8})");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LanLogger.Remote("encoder_property_failed", reason: $"{label} threw: {ex.Message}");
            return false;
        }
    }
}
