using System;
using System.Diagnostics;
using System.IO;

namespace PhoneCam.VirtualCam.Filter.Util
{
    internal static class FilterLog
    {
        private static readonly object _lock = new object();

        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneCam",
            "virtualcam_filter.log");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
                lock (_lock)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                Debug.WriteLine(line);
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}
