using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WallTrek.Services
{
    /// <summary>
    /// Integration with Lively Wallpaper (github.com/rocksdanister/lively) via its command-line
    /// interface. Lively renders video wallpapers, which Windows cannot do natively. When Lively
    /// is running its wallpaper layer covers the static desktop wallpaper, so image wallpapers
    /// must be routed through it too for the change to be visible.
    /// </summary>
    public static class LivelyService
    {
        private const string StorePackageFamilyName = "12030rocksdanister.LivelyWallpaper_97hta09mmv6hy";
        private const string ProcessName = "Lively";

        public static bool IsRunning => Process.GetProcessesByName(ProcessName).Length > 0;

        public static bool IsInstalled => FindLivelyExe() != null;

        public static void SetWallpaper(string filePath)
        {
            var exe = FindLivelyExe() ?? throw new InvalidOperationException("Lively Wallpaper is not installed.");

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"setwp --file \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
        }

        private static string? FindLivelyExe()
        {
            // A running instance is the most reliable source of the exe path and covers both the
            // installer and Microsoft Store versions.
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                        // MainModule can be inaccessible; fall through to the static locations.
                    }
                }
            }

            var installerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Lively Wallpaper", "Lively.exe");
            if (File.Exists(installerPath))
            {
                return installerPath;
            }

            try
            {
                var packageManager = new Windows.Management.Deployment.PackageManager();
                var package = packageManager.FindPackagesForUser(string.Empty, StorePackageFamilyName).FirstOrDefault();
                if (package != null)
                {
                    var storePath = Path.Combine(package.InstalledLocation.Path, "Build", "Lively.exe");
                    if (File.Exists(storePath))
                    {
                        return storePath;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Lively Store package lookup failed: {ex.Message}");
            }

            return null;
        }
    }
}
