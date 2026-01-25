using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

    private static readonly string[] RequiredLibraryPrefixes =
    [
        "avcodec",
        "avformat",
        "avutil",
        "swscale",
        "swresample"
    ];

    private static bool _resolverInstalled;

    public static void LoadOrThrow()
    {
        var baseDir = AppContext.BaseDirectory;

        var nativeDir = ResolveNativeDirectory(baseDir)
            ?? throw new DirectoryNotFoundException("FFmpeg native folder not found.");

        // Говорим загрузчику DLL: используй стандартные директории + нашу
        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (AddDllDirectory(nativeDir) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var libraryMap = ResolveLibraries(nativeDir);
        var missing = libraryMap.Where(kvp => kvp.Value == null).Select(kvp => kvp.Key).ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                "Missing FFmpeg native DLLs: " + string.Join(", ", missing) + "\n" +
                BuildDiagnostics(nativeDir, libraryMap, null));
        }

        foreach (var library in libraryMap.Values.OfType<string>())
        {
            if (!NativeLibrary.TryLoad(library, out _))
            {
                throw new DllNotFoundException(
                    "Failed to load FFmpeg native library: " + library + "\n" +
                    BuildDiagnostics(nativeDir, libraryMap, null));
            }
        }

        InstallResolver(nativeDir, libraryMap);

        // Для FFmpeg.AutoGen (динамические биндинги)
        ffmpeg.RootPath = nativeDir;

        // Триггерим резолв символов сразу, чтобы упасть тут с понятной ошибкой, а не где-то позже
        var version = ffmpeg.avcodec_version();
        _ = BuildDiagnostics(nativeDir, libraryMap, version);
    }

    public static string SelfCheck()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var nativeDir = ResolveNativeDirectory(baseDir, throwIfMissing: false);
            if (nativeDir == null)
                return BuildDiagnostics(null, ResolveLibraries(null), null);

            var libraryMap = ResolveLibraries(nativeDir);
            uint? version = null;

            try
            {
                if (SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
                    AddDllDirectory(nativeDir);

                InstallResolver(nativeDir, libraryMap);

                ffmpeg.RootPath = nativeDir;
                version = ffmpeg.avcodec_version();
            }
            catch
            {
                // Swallow here; diagnostics will include missing DLLs and architecture.
            }

            return BuildDiagnostics(nativeDir, libraryMap, version);
        }
        catch (Exception ex)
        {
            return "FFmpeg self-check failed: " + ex + Environment.NewLine + BuildDiagnostics(null, ResolveLibraries(null), null);
        }
    }

    private static string? ResolveNativeDirectory(string baseDir, bool throwIfMissing = true)
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("PHONCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);

        candidates.AddRange(
        [
            Path.Combine(baseDir, "ffmpeg"),
            Path.Combine(baseDir, "runtimes", "win-x64", "native"),
            Path.Combine(baseDir, "runtimes", "win7-x64", "native")
        ]);

        var nativeDir = candidates.FirstOrDefault(Directory.Exists);
        if (nativeDir != null || !throwIfMissing)
            return nativeDir;

        throw new DirectoryNotFoundException(
            "FFmpeg native folder not found. Expected one of:\n" + string.Join("\n", candidates));
    }

    private static Dictionary<string, string?> ResolveLibraries(string? nativeDir)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in RequiredLibraryPrefixes)
        {
            string? match = null;
            if (!string.IsNullOrWhiteSpace(nativeDir) && Directory.Exists(nativeDir))
            {
                match = Directory.EnumerateFiles(nativeDir, $"{prefix}*.dll")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }

            map[prefix] = match;
        }

        return map;
    }

    private static string BuildDiagnostics(string? nativeDir, Dictionary<string, string?> libraryMap, uint? avcodecVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FFmpeg diagnostics:");
        sb.AppendLine($"  BaseDir: {AppContext.BaseDirectory}");
        sb.AppendLine($"  ProcessArch: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"  Is64BitProcess: {Environment.Is64BitProcess}");
        sb.AppendLine($"  OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"  PHONCAM_FFMPEG_PATH: {Environment.GetEnvironmentVariable("PHONCAM_FFMPEG_PATH") ?? "<unset>"}");
        sb.AppendLine($"  NativeDir: {(string.IsNullOrWhiteSpace(nativeDir) ? "<not found>" : nativeDir)}");
        sb.AppendLine("  Libraries:");
        foreach (var kvp in libraryMap)
        {
            sb.AppendLine($"    {kvp.Key}: {(kvp.Value ?? "<missing>")}");
        }

        sb.AppendLine($"  avcodec_version: {(avcodecVersion.HasValue ? avcodecVersion.Value.ToString() : "<not loaded>")}");
        AppendLoadedModules(sb);
        return sb.ToString();
    }

    private static void InstallResolver(string nativeDir, Dictionary<string, string?> libraryMap)
    {
        if (_resolverInstalled)
            return;

        NativeLibrary.SetDllImportResolver(typeof(ffmpeg).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (string.IsNullOrWhiteSpace(libraryName))
                return IntPtr.Zero;

            var match = libraryMap
                .FirstOrDefault(kvp => libraryName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(match.Value) && File.Exists(match.Value))
            {
                return NativeLibrary.Load(match.Value);
            }

            var fallback = Path.Combine(nativeDir, libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? libraryName
                : libraryName + ".dll");

            return File.Exists(fallback) ? NativeLibrary.Load(fallback) : IntPtr.Zero;
        });

        _resolverInstalled = true;
    }

    private static void AppendLoadedModules(StringBuilder sb)
    {
        try
        {
            sb.AppendLine("  Loaded modules (FFmpeg-related):");
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName ?? string.Empty;
                if (RequiredLibraryPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine($"    {name} → {module.FileName}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Loaded modules: <unavailable> {ex.Message}");
        }
    }
}