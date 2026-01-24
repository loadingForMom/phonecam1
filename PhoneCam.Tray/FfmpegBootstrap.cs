using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PhoneCam.Tray;

internal static class FfmpegBootstrap
{
    public static void Init()
    {
        var baseDir = AppContext.BaseDirectory;
        var ffmpegDir = Path.Combine(baseDir, "ffmpeg");

        if (!Directory.Exists(ffmpegDir))
            throw new DirectoryNotFoundException(
                $"FFmpeg folder not found: {ffmpegDir}\n" +
                "Create it and put avcodec/avutil/avformat/swscale dlls inside.");

        // Важно: грузим в правильном порядке зависимостей
        LoadDll(ffmpegDir, "avutil");
        LoadDll(ffmpegDir, "swresample"); // если есть
        LoadDll(ffmpegDir, "swscale");
        LoadDll(ffmpegDir, "avcodec");
        LoadDll(ffmpegDir, "avformat");
        LoadDll(ffmpegDir, "avdevice"); // если есть

        ffmpeg.RootPath = ffmpegDir;

        _ = ffmpeg.av_version_info(); // sanity check
    }

    private static void LoadDll(string dir, string nameNoExt)
    {
        var dllName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{nameNoExt}-61.dll"   // частый вариант
            : $"lib{nameNoExt}.so";

        // Если у тебя другая версия (например avcodec-60.dll), просто переименуй
        // или добавь ещё один вариант ниже.
        var path = Path.Combine(dir, dllName);

        if (!File.Exists(path))
        {
            // fallback: пытаемся найти любой подходящий avcodec-*.dll
            var candidates = Directory.GetFiles(dir, $"{nameNoExt}-*.dll");
            if (candidates.Length > 0) path = candidates[0];
        }

        if (File.Exists(path))
        {
            NativeLibrary.Load(path);
        }
    }
}