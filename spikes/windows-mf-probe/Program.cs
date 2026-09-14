using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

// WS0 spike for the LAN Messenger remote-desktop work. Throwaway diagnostic.
//
// Answers the questions the plan flagged as unknown and that cannot be answered
// from a Mac:
//
//   1. Which H.264 encoder MFTs exist here (hardware / software)?
//   2. Does ICodecAPI QueryInterface succeed on them? Vortice does NOT project
//      that interface — confirmed by reflection — and it is what carries
//      CODECAPI_AVLowLatencyMode and rate control, so this decides whether the
//      shipping code needs a hand-rolled ICodecAPI or a CsWin32-generated one.
//   3. Does DXGI Desktop Duplication acquire on this display adapter?
//   4. Can we encode NV12 frames to a playable Annex-B .h264 file?
//
// Every stage reports rather than throws: a stack trace from stage 1 would tell
// us nothing about stages 2 through 4, and the whole point is the report.
//
// Attribute GUIDs are written out literally rather than taken from Vortice's
// key classes. That is deliberate — it removes any dependency on how Vortice
// happens to name them, and each one is greppable against the Windows headers.

internal static class Program
{
    private static int _problems;

    private static int Main(string[] args)
    {
        Console.WriteLine("LAN Messenger — WS0 Windows media probe");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"OS           : {Environment.OSVersion}");
        Console.WriteLine($"Process arch : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"OS arch      : {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($".NET         : {RuntimeInformation.FrameworkDescription}");
        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
            Console.WriteLine("NOTE         : running emulated (process arch != OS arch)");

        Stage("1. Media Foundation startup", () =>
        {
            MediaFactory.MFStartup(false).CheckError();
            Console.WriteLine("  MFStartup OK");
        });
        Stage("2. H.264 encoder MFTs + ICodecAPI reachability", EnumerateEncoders);
        Stage("3. DXGI adapters, outputs, Desktop Duplication", ProbeDesktopDuplication);
        Stage("4. Encode NV12 -> out.h264", () => EncodeSample(args));

        // The interop test that matters: a stream produced by VideoToolbox on
        // the Mac, decoded here by Media Foundation. Encoding and decoding on
        // the same platform proves nothing about the pair.
        var decodePath = args.FirstOrDefault(a => a.StartsWith("--decode="))?[9..];
        if (decodePath is not null)
            Stage($"5. Decode {Path.GetFileName(decodePath)} (cross-platform)",
                  () => DecodeSample(decodePath));

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(_problems == 0
            ? "All stages ran. Read the detail — 'ran' is not 'good'."
            : $"{_problems} problem(s) reported. Detail above.");
        return 0;   // a report, not a gate
    }

    private static void Stage(string title, Action body)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} " + new string('-', Math.Max(0, 66 - title.Length)));
        try { body(); }
        catch (Exception ex)
        {
            _problems++;
            Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
            if (ex is SharpGenException sg) Console.WriteLine($"     HRESULT 0x{sg.ResultCode.Code:X8}");
        }
    }

    // ---- GUIDs (Windows SDK: mfapi.h, mftransform.h, codecapi.h) ----------

    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MFMediaType_Video          = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264         = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12         = new("3231564e-0000-0010-8000-00aa00389b71");

    private static readonly Guid MF_MT_MAJOR_TYPE           = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE              = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE          = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE       = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE           = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE           = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO   = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

    private static Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid MF_TRANSFORM_ASYNC          = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK   = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    private const uint MFT_ENUM_FLAG_SYNCMFT        = 0x00000001;
    private const uint MFT_ENUM_FLAG_ASYNCMFT       = 0x00000002;
    private const uint MFT_ENUM_FLAG_HARDWARE       = 0x00000004;
    private const uint MFT_ENUM_FLAG_TRANSCODE_ONLY = 0x00000010;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER  = 0x00000040;

    private const int MFVideoInterlace_Progressive = 2;

    // MFTEnumEx hands back a CoTaskMem array of IMFActivate*, so the array has
    // to be walked and freed by hand — Vortice does not wrap that for us.
    private static List<IMFActivate> EnumEncoders(uint flags)
    {
        var outputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MFMediaType_Video,
            GuidSubtype = MFVideoFormat_H264,
        };

        MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, null, outputInfo,
                               out IntPtr array, out uint count);

        var result = new List<IMFActivate>((int)count);
        if (array == IntPtr.Zero) return result;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(array, (int)(i * (uint)IntPtr.Size));
                if (p != IntPtr.Zero) result.Add(new IMFActivate(p));
            }
        }
        finally { Marshal.FreeCoTaskMem(array); }
        return result;
    }

    private static void EnumerateEncoders()
    {
        Report("HARDWARE", MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER);
        Report("SOFTWARE sync", MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER);
        Report("SOFTWARE async", MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER);

        static void Report(string label, uint flags)
        {
            List<IMFActivate> found;
            try { found = EnumEncoders(flags); }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-16} enumeration failed: {ex.GetType().Name} {ex.Message}");
                return;
            }

            Console.WriteLine($"  {label,-16} {found.Count} transform(s)");
            foreach (var activate in found)
            {
                Console.WriteLine($"      - {FriendlyName(activate)}");

                try
                {
                    using var transform = activate.ActivateObject<IMFTransform>();
                    Console.WriteLine($"        async     : {DescribeAsync(transform)}");
                    Guid iid = IID_ICodecAPI;
                    int hr = Marshal.QueryInterface(transform.NativePointer, ref iid, out IntPtr codecApi);
                    if (hr >= 0 && codecApi != IntPtr.Zero)
                    {
                        Console.WriteLine("        ICodecAPI : YES");
                        Marshal.Release(codecApi);
                    }
                    else
                    {
                        Console.WriteLine($"        ICodecAPI : no (hr=0x{hr:X8}) " +
                                          "— low-latency + rate control unavailable on this MFT");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"        activate  : failed — {ex.GetType().Name} {ex.Message}");
                }
                finally { activate.Dispose(); }
            }
        }
    }

    // ---- 3 ---------------------------------------------------------------

    private static void ProbeDesktopDuplication()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                Console.WriteLine($"  adapter {a}: {desc.Description.Trim()} " +
                                  $"(vendor 0x{desc.VendorId:X4}, VRAM {desc.DedicatedVideoMemory / (1024 * 1024)} MB)");

                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        var od = output.Description;
                        Console.WriteLine($"      output {o}: {od.DeviceName} " +
                                          $"{od.DesktopCoordinates.Right - od.DesktopCoordinates.Left}x" +
                                          $"{od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top} " +
                                          $"attached={od.AttachedToDesktop}");
                        TryDuplicate(adapter, output);
                    }
                }
            }
        }
    }

    private static void TryDuplicate(IDXGIAdapter1 adapter, IDXGIOutput output)
    {
        try
        {
            // The device MUST be created on the adapter that owns this output.
            // Creating on adapter 0 regardless is what fails with
            // DXGI_ERROR_UNSUPPORTED on every hybrid-graphics laptop, so the
            // probe deliberately does it the correct way.
            var hr = D3D11.D3D11CreateDevice(
                adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device);
            if (hr.Failure)
            {
                Console.WriteLine($"        D3D11CreateDevice 0x{hr.Code:X8}");
                return;
            }

            if (device is null) { Console.WriteLine("        D3D11CreateDevice returned no device"); return; }

            using (device)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            using (var duplication = output1.DuplicateOutput(device))
            {
                Console.WriteLine("        DuplicateOutput: OK");
                var acquired = duplication.AcquireNextFrame(1000, out var info, out IDXGIResource? resource);
                if (acquired.Success)
                {
                    Console.WriteLine($"        AcquireNextFrame: OK  accumulated={info.AccumulatedFrames} " +
                                      $"pointerShapeBytes={info.PointerShapeBufferSize}");
                    resource?.Dispose();
                    duplication.ReleaseFrame();
                }
                else
                {
                    Console.WriteLine($"        AcquireNextFrame: 0x{acquired.Code:X8} ({DescribeDxgi(acquired.Code)})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        duplication failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static string DescribeDxgi(int code) => (uint)code switch
    {
        0x887A0027 => "WAIT_TIMEOUT — nothing changed on screen; normal, capture is change-driven",
        0x887A0026 => "ACCESS_LOST — desktop switch or mode change; re-acquire",
        0x887A002B => "ACCESS_DENIED — secure desktop; loop with backoff",
        0x887A0004 => "UNSUPPORTED — wrong adapter for this output",
        _          => "see the DXGI error reference",
    };

    // ---- 4 ---------------------------------------------------------------

    private static void EncodeSample(string[] args)
    {
        int frames = 120, width = 1280, height = 720;
        foreach (var a in args)
        {
            if (a.StartsWith("--frames=")) int.TryParse(a[9..], out frames);
            if (a.StartsWith("--width="))  int.TryParse(a[8..], out width);
            if (a.StartsWith("--height=")) int.TryParse(a[9..], out height);
        }
        Console.WriteLine($"  {width}x{height}, {frames} frames, NV12 in / H.264 out");

        // Sync MFTs first: the simple ProcessInput/ProcessOutput loop below only
        // works with those, and the point of this stage is to prove a playable
        // file comes out, not to reimplement the async event pump.
        var candidates = EnumEncoders(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER);
        if (candidates.Count == 0)
            candidates = EnumEncoders(MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SYNCMFT |
                                      MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_TRANSCODE_ONLY |
                                      MFT_ENUM_FLAG_SORTANDFILTER);
        if (candidates.Count == 0)
        {
            Console.WriteLine("  !! No H.264 encoder MFT at all.");
            Console.WriteLine("     On Windows N/KN this means the Media Feature Pack is missing. The");
            Console.WriteLine("     shipping app must detect exactly this at invite time and say so,");
            Console.WriteLine("     rather than failing opaquely once a session is already up.");
            _problems++;
            return;
        }

        using var activate = candidates[0];
        for (int i = 1; i < candidates.Count; i++) candidates[i].Dispose();

        Console.WriteLine($"  using: {FriendlyName(activate)}");

        using var encoder = activate.ActivateObject<IMFTransform>();

        // An async (hardware) MFT refuses ProcessInput with
        // MF_E_TRANSFORM_ASYNC_LOCKED until the caller declares it understands
        // the asynchronous model by setting MF_TRANSFORM_ASYNC_UNLOCK. Unlocking
        // is necessary but NOT sufficient: a real async MFT then has to be driven
        // by METransformNeedInput / METransformHaveOutput events off
        // IMFMediaEventGenerator, not by this synchronous loop. The probe unlocks
        // so the flag's effect is observable, and the encode stage below prefers a
        // sync MFT so it can actually produce a file.
        Unlock(encoder);

        // Output type first — the MF H.264 encoder requires it before the input
        // type, and setting them the other way round fails with a
        // not-obviously-related error.
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outType.Set(MF_MT_AVG_BITRATE, (uint)8_000_000);
            outType.Set(MF_MT_INTERLACE_MODE, (uint)MFVideoInterlace_Progressive);
            outType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            outType.Set(MF_MT_FRAME_RATE, Pack(30, 1));
            outType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            encoder.SetOutputType(0, outType, 0);
            Console.WriteLine("  SetOutputType OK");
        }

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inType.Set(MF_MT_INTERLACE_MODE, (uint)MFVideoInterlace_Progressive);
            inType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            inType.Set(MF_MT_FRAME_RATE, Pack(30, 1));
            inType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            encoder.SetInputType(0, inType, 0);
            Console.WriteLine("  SetInputType OK");
        }

        encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        string outPath = Path.Combine(Directory.GetCurrentDirectory(), "out.h264");
        using var file = File.Create(outPath);

        int nv12Size = width * height * 3 / 2;
        long written = 0, samples = 0;

        for (int i = 0; i < frames; i++)
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12Size);
            unsafe
            {
                buffer.Lock(out IntPtr data, out _, out _);
                var span = new Span<byte>((void*)data, nv12Size);
                // A moving luma ramp. A constant image encodes to almost nothing
                // and would not prove the encoder is doing real work.
                span[..(width * height)].Fill((byte)(16 + (i * 2 % 200)));
                span[(width * height)..].Fill(128);   // neutral chroma
                buffer.Unlock();
                buffer.CurrentLength = nv12Size;
            }

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = i * 10_000_000L / 30;
            sample.SampleDuration = 10_000_000L / 30;

            try { encoder.ProcessInput(0, sample, 0); }
            catch (SharpGenException ex)
            {
                Console.WriteLine($"  ProcessInput failed at frame {i}: 0x{ex.ResultCode.Code:X8}");
                Console.WriteLine("     A bare E_FAIL here on an ASYNC hardware MFT almost always means");
                Console.WriteLine("     MF_TRANSFORM_ASYNC_UNLOCK was never set on its attributes. That");
                Console.WriteLine("     failure is not discoverable from the error — you either know it");
                Console.WriteLine("     or you lose a day to it.");
                _problems++;
                break;
            }

            written += Drain(encoder, file, ref samples);
        }

        encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        encoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        written += Drain(encoder, file, ref samples);
        encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);

        file.Flush();
        Console.WriteLine($"  wrote {samples} sample(s), {written} bytes -> {outPath}");
        if (written == 0) { Console.WriteLine("  !! Nothing encoded."); _problems++; }
        else Console.WriteLine("  Verify with:  ffplay out.h264      (or open in VLC)");
    }

    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MF_MT_FRAME_SIZE_OUT = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// Feeds an Annex-B file to the Media Foundation H.264 decoder.
    ///
    /// Note the asymmetry with the encoder: a decoder wants its INPUT type first
    /// and only offers an output type once it has parsed enough of the stream to
    /// know the picture size, which surfaces as MF_E_TRANSFORM_STREAM_CHANGE
    /// rather than as an error.
    private static void DecodeSample(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  !! not found: {path}"); _problems++; return; }
        byte[] annexB = File.ReadAllBytes(path);
        Console.WriteLine($"  input: {annexB.Length} bytes of Annex-B");

        MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 },
            null, out IntPtr array, out uint count);

        if (array == IntPtr.Zero || count == 0)
        {
            Console.WriteLine("  !! no H.264 decoder MFT");
            _problems++; return;
        }
        using var activate = new IMFActivate(Marshal.ReadIntPtr(array));
        Marshal.FreeCoTaskMem(array);
        Console.WriteLine($"  using: {FriendlyName(activate)}");

        using var decoder = activate.ActivateObject<IMFTransform>();
        Unlock(decoder);

        int decoded = 0; bool outputConfigured = false; int width = 0, height = 0;
        long auIndex = 0;

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            inType.Set(MF_MT_INTERLACE_MODE, (uint)MFVideoInterlace_Progressive);
            decoder.SetInputType(0, inType, 0);
            Console.WriteLine("  SetInputType(H264) OK");
        }

        // The output type must be set BEFORE any ProcessOutput, not discovered
        // through MF_E_TRANSFORM_STREAM_CHANGE: without it the decoder answers
        // every ProcessOutput with MF_E_TRANSFORM_TYPE_NOT_SET (0xC00D6D60) and
        // emits nothing, forever. The size it offers now is a placeholder; the
        // real one arrives as a stream change once it has parsed the SPS.
        if (!NegotiateOutput(decoder, ref width, ref height))
        {
            Console.WriteLine("  !! decoder offered no NV12 output type");
            _problems++; return;
        }
        outputConfigured = true;

        decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        // One sample per access unit. Splitting on IDR/non-IDR slice boundaries
        // is enough for a probe; a real decoder gets whole access units from the
        // media channel anyway.
        var samples = SplitAccessUnits(annexB);
        Console.WriteLine($"  {samples.Count} access unit(s) to feed");

        foreach (var au in samples)
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(au.Length);
            unsafe
            {
                buffer.Lock(out IntPtr data, out _, out _);
                new Span<byte>(au).CopyTo(new Span<byte>((void*)data, au.Length));
                buffer.Unlock();
                buffer.CurrentLength = au.Length;
            }
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            // Without a timestamp the decoder has no timeline and will happily
            // buffer every access unit without ever emitting one.
            sample.SampleTime = auIndex * 10_000_000L / 30;
            sample.SampleDuration = 10_000_000L / 30;
            auIndex++;

            // MF_E_NOTACCEPTING means the decoder is holding output it wants
            // collected before it will take more input. That is the normal MFT
            // contract, not an error: drain and retry the same sample.
            bool accepted = false;
            for (int attempt = 0; attempt < 8 && !accepted; attempt++)
            {
                try { decoder.ProcessInput(0, sample, 0); accepted = true; }
                catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NOTACCEPTING)
                {
                    DrainDecoder(decoder, ref decoded, ref outputConfigured, ref width, ref height);
                }
                catch (SharpGenException ex)
                {
                    Console.WriteLine($"  ProcessInput 0x{ex.ResultCode.Code:X8}");
                    accepted = true;   // stop retrying; report and move on
                }
            }
            DrainDecoder(decoder, ref decoded, ref outputConfigured, ref width, ref height);
        }

        decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        decoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        DrainDecoder(decoder, ref decoded, ref outputConfigured, ref width, ref height);

        Console.WriteLine($"  DECODED {decoded} frame(s) at {width}x{height}");
        if (decoded == 0)
        {
            Console.WriteLine("  !! Media Foundation could not decode the macOS stream.");
            _problems++;
        }
        else
        {
            Console.WriteLine("  Cross-platform decode CONFIRMED: VideoToolbox -> Media Foundation.");
        }
    }

    private static void DrainDecoder(IMFTransform decoder, ref int decoded,
                                     ref bool outputConfigured, ref int width, ref int height)
    {
        while (true)
        {
            var info = decoder.GetOutputStreamInfo(0);
            bool selfAllocating =
                (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples |
                                    OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
            var buffer = new OutputDataBuffer { StreamID = 0 };
            if (!selfAllocating && info.Size > 0)
            {
                var allocated = MediaFactory.MFCreateSample();
                allocated.AddBuffer(MediaFactory.MFCreateMemoryBuffer(info.Size));
                buffer.Sample = allocated;
            }

            var hr = decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);

            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) { buffer.Sample?.Dispose(); return; }
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                buffer.Sample?.Dispose();
                // The decoder has now parsed the SPS and knows the real picture
                // size, so the placeholder type set at startup is replaced here.
                if (!NegotiateOutput(decoder, ref width, ref height)) return;
                outputConfigured = true;
                continue;
            }
            if (hr.Failure)
            {
                if (!_reportedOutputHr)
                {
                    Console.WriteLine($"  ProcessOutput 0x{hr.Code:X8} " +
                                      $"(streamInfo flags=0x{info.Flags:X} size={info.Size})");
                    _reportedOutputHr = true;
                }
                buffer.Sample?.Dispose();
                return;
            }
            if (buffer.Sample is null) return;

            using (buffer.Sample) { decoded++; }
        }
    }

    /// Picks the decoder's NV12 output type and applies it, reporting the frame
    /// size it agreed to.
    private static bool NegotiateOutput(IMFTransform decoder, ref int width, ref int height)
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType candidate;
            try { candidate = decoder.GetOutputAvailableType(0, i); }
            catch { return false; }

            bool isNV12;
            try { isNV12 = candidate.GetGUID(MF_MT_SUBTYPE) == MFVideoFormat_NV12; }
            catch { candidate.Dispose(); continue; }

            if (!isNV12) { candidate.Dispose(); continue; }

            decoder.SetOutputType(0, candidate, 0);
            try
            {
                ulong packed = candidate.GetUInt64(MF_MT_FRAME_SIZE_OUT);
                width = (int)(packed >> 32); height = (int)(packed & 0xFFFFFFFF);
            }
            catch { }
            Console.WriteLine($"  output type set: NV12 {width}x{height}");
            candidate.Dispose();
            return true;
        }
    }

    /// Splits an Annex-B byte stream into access units, one per coded picture.
    ///
    /// Keying on access unit delimiters does not work: VideoToolbox emits none
    /// at all, and parameter sets only appear at IDRs, so an AUD/SPS-based split
    /// collapsed a 60-frame stream into 2 units. The reliable rule is that every
    /// slice (type 1 or 5) is exactly one picture, and any SPS/PPS/SEI/AUD ahead
    /// of it belongs to it.
    private static List<byte[]> SplitAccessUnits(byte[] data)
    {
        var starts = new List<(int Offset, int Code)>();
        int i = 0;
        while (i + 3 <= data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1) { starts.Add((i, 4)); i += 4; continue; }
                if (data[i + 2] == 1) { starts.Add((i, 3)); i += 3; continue; }
            }
            i++;
        }
        if (starts.Count == 0) return [data];

        var units = new List<byte[]>();
        int prefixStart = starts[0].Offset;
        for (int k = 0; k < starts.Count; k++)
        {
            byte type = (byte)(data[starts[k].Offset + starts[k].Code] & 0x1F);
            if (type is not (1 or 5)) continue;          // not a picture yet
            int end = k + 1 < starts.Count ? starts[k + 1].Offset : data.Length;
            units.Add(data[prefixStart..end]);
            prefixStart = end;
        }
        if (prefixStart < data.Length && units.Count == 0) units.Add(data[prefixStart..]);
        return units;
    }

    private static bool _reportedOutputHr;

    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE   = unchecked((int)0xC00D6D61);

    private static long Drain(IMFTransform encoder, Stream file, ref long samples)
    {
        long total = 0;
        while (true)
        {
            var info = encoder.GetOutputStreamInfo(0);
            bool selfAllocating =
                (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples |
                                    OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;

            var buffer = new OutputDataBuffer { StreamID = 0 };
            if (!selfAllocating)
            {
                var allocated = MediaFactory.MFCreateSample();
                allocated.AddBuffer(MediaFactory.MFCreateMemoryBuffer(info.Size));
                buffer.Sample = allocated;
            }

            var hr = encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);

            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) { buffer.Sample?.Dispose(); return total; }
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                Console.WriteLine("  output type changed mid-stream — re-read the sequence header here");
                buffer.Sample?.Dispose();
                return total;
            }
            if (hr.Failure)
            {
                Console.WriteLine($"  ProcessOutput 0x{hr.Code:X8}");
                buffer.Sample?.Dispose();
                return total;
            }
            if (buffer.Sample is null) return total;

            using (var produced = buffer.Sample)
            using (var contiguous = produced.ConvertToContiguousBuffer())
            {
                unsafe
                {
                    contiguous.Lock(out IntPtr data, out _, out int length);
                    file.Write(new ReadOnlySpan<byte>((void*)data, length));
                    total += length;
                    contiguous.Unlock();
                }
            }
            samples++;
        }
    }

    private static string FriendlyName(IMFActivate activate)
    {
        try
        {
            var n = activate.GetString(MFT_FRIENDLY_NAME_Attribute);
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        catch { }
        return "(unnamed)";
    }

    /// Reports whether a transform advertises the asynchronous model, and whether
    /// unlocking it succeeds. Hardware encoders are async; this is the single
    /// most common reason a first Media Foundation encode attempt fails.
    private static string DescribeAsync(IMFTransform transform)
    {
        try
        {
            var attrs = transform.Attributes;
            uint isAsync = 0;
            try { isAsync = attrs.GetUInt32(MF_TRANSFORM_ASYNC); } catch { }
            if (isAsync == 0) return "no (synchronous MFT)";
            var hr = attrs.Set(MF_TRANSFORM_ASYNC_UNLOCK, (uint)1);
            return hr.Success
                ? "YES — needs MF_TRANSFORM_ASYNC_UNLOCK + the event-driven pump (unlock OK)"
                : $"YES — unlock FAILED 0x{hr.Code:X8}";
        }
        catch (Exception ex) { return $"unknown ({ex.GetType().Name})"; }
    }

    private static void Unlock(IMFTransform transform)
    {
        try
        {
            var attrs = transform.Attributes;
            uint isAsync = 0;
            try { isAsync = attrs.GetUInt32(MF_TRANSFORM_ASYNC); } catch { }
            if (isAsync != 0)
            {
                attrs.Set(MF_TRANSFORM_ASYNC_UNLOCK, (uint)1);
                Console.WriteLine("  async MFT: set MF_TRANSFORM_ASYNC_UNLOCK");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  unlock attempt failed: {ex.GetType().Name}"); }
    }

    /// MF packs paired 32-bit values (width/height, numerator/denominator) into
    /// a single 64-bit attribute, high word first.
    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
