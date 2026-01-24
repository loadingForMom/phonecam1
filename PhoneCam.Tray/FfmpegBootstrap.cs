using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PhoneCam.Tray;

internal static class FfmpegBootstrap
{
    // Win10+ (на Win11 точно есть)
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr AddDllDirectory([MarshalAs(UnmanagedType.LPWStr)] string newDirectory);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    public static void LoadOrThrow()
    {
        var baseDir = AppContext.BaseDirectory;

        // где реально могут лежать ffmpeg dll
        var candidates = new[]
        {
            Path.Combine(baseDir, "ffmpeg"),                      // если решите хранить рядом как "ffmpeg\*.dll"
            Path.Combine(baseDir, "runtimes", "win-x64", "native"),
            Path.Combine(baseDir, "runtimes", "win7-x64", "native"),
        };

        var nativeDir = candidates.FirstOrDefault(Directory.Exists)
                        ?? throw new DirectoryNotFoundException(
                            "FFmpeg native folder not found. Expected one of:\n" + string.Join("\n", candidates));

        // Говорим загрузчику DLL: используй стандартные директории + нашу
        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (AddDllDirectory(nativeDir) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Для FFmpeg.AutoGen (динамические биндинги)
        ffmpeg.RootPath = nativeDir;

        // Триггерим резолв символов сразу, чтобы упасть тут с понятной ошибкой, а не где-то позже
        _ = ffmpeg.avcodec_version();
    }
}