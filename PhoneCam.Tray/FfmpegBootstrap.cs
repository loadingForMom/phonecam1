using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace PhoneCam.Tray;

internal static class FfmpegBootstrap
{
    public static void Init()
    {
        var ffmpegDir = EnsureFfmpeg().GetAwaiter().GetResult();

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

    private static async Task<string> EnsureFfmpeg()
    {
        var baseDir = AppContext.BaseDirectory;
        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneCam",
            "ffmpeg");

        Directory.CreateDirectory(localDir);

        if (HasDlls(localDir)) return localDir;

        var bundledDir = Path.Combine(baseDir, "ffmpeg");
        if (HasDlls(bundledDir)) return bundledDir;

        await DownloadFfmpegAsync(localDir);
        if (!HasDlls(localDir))
            throw new DirectoryNotFoundException("FFmpeg download failed or missing DLLs.");

        return localDir;
    }

    private static bool HasDlls(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return Directory.GetFiles(dir, "avcodec-*.dll").Length > 0;
    }

    private static async Task DownloadFfmpegAsync(string targetDir)
    {
        var url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        var zipPath = Path.Combine(targetDir, "ffmpeg.zip");

        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(zipPath, bytes);

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.Contains("/bin/")) continue;
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var outPath = Path.Combine(targetDir, entry.Name);
            entry.ExtractToFile(outPath, true);
        }

        File.Delete(zipPath);
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
