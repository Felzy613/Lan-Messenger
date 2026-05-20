using System.Runtime.InteropServices;

namespace LanMessenger.Core.Services;

public static class CryptoRuntimeDiagnostics
{
    private static int _logged;

    public static void LogOnce()
    {
        if (Interlocked.Exchange(ref _logged, 1) == 1) return;

        LanLogger.Info("CryptoRuntime",
            $"os={RuntimeInformation.OSDescription.Trim()} arch={RuntimeInformation.ProcessArchitecture} " +
            $"framework={RuntimeInformation.FrameworkDescription.Trim()} baseDir={AppContext.BaseDirectory}");

        LogNativeDll("libsodium.dll");
        LogNativeDll("vcruntime140.dll");
        LogNativeDll("vcruntime140_1.dll");
        LogNativeDll("msvcp140.dll");
    }

    private static void LogNativeDll(string fileName)
    {
        var appLocalPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(appLocalPath))
        {
            var info = new FileInfo(appLocalPath);
            LanLogger.Info("CryptoRuntime", $"{fileName}: app-local size={info.Length} modifiedUtc={info.LastWriteTimeUtc:u}");
        }
        else
        {
            LanLogger.Warn("CryptoRuntime", $"{fileName}: missing from app directory");
        }

        if (!OperatingSystem.IsWindows()) return;

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeLibrary.Load(fileName);
            LanLogger.Info("CryptoRuntime", $"{fileName}: NativeLibrary.Load succeeded");
        }
        catch (Exception ex)
        {
            LanLogger.Error("CryptoRuntime", $"{fileName}: NativeLibrary.Load failed", ex);
        }
        finally
        {
            if (handle != IntPtr.Zero)
                NativeLibrary.Free(handle);
        }
    }
}
