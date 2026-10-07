using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Arena.EditorTools
{
    /// <summary>
    /// One-click device loop for the Android build: build the APK, install it on
    /// the connected device with adb, then dump a Unity-filtered logcat to a file
    /// for bug reports. Every step degrades gracefully — if adb or a device is
    /// missing the build is still produced and the problem is reported in the
    /// Console instead of throwing.
    ///
    /// Menu: <b>Tools &gt; Arena &gt; Build and Test on Device</b>.
    /// </summary>
    public static class BuildAndTest
    {
        private const string ApkPath = "build/outputs/Arena.apk";
        private const string LogcatPath = "build/outputs/logcat-unity.txt";
        private const string OutputDir = "build/outputs";
        private const int InstallTimeoutMs = 180000; // 3 minutes
        private const int LogcatTimeoutMs = 60000;   // 1 minute

        private static readonly string[] Scenes = { "Assets/Scenes/SampleScene.unity" };

        [MenuItem("Tools/Arena/Build and Test on Device")]
        public static void BuildInstallAndLog()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[BuildAndTest] Exit Play Mode before building.");
                return;
            }

            string apk = BuildApk();
            if (apk == null) return; // build failed; the reason is already in the Console

            if (!TryGetConnectedDevice(out string devices))
            {
                Debug.LogWarning(
                    "[BuildAndTest] adb reported no connected device, so install and logcat were skipped.\n" +
                    devices);
                return;
            }

            if (!RunAdb($"install -r \"{apk}\"", out string installOutput, InstallTimeoutMs))
            {
                Debug.LogError("[BuildAndTest] adb install failed:\n" + installOutput);
                return;
            }
            Debug.Log("[BuildAndTest] APK installed on the connected device:\n" + installOutput);

            CaptureLogcat();
        }

        [MenuItem("Tools/Arena/Capture Device Log (Unity)")]
        public static void CaptureLogcat()
        {
            // Do not launch adb at all unless a device answers: invoking logcat
            // with no target just blocks the editor on a process that never ends.
            if (!TryGetConnectedDevice(out string devices))
            {
                Debug.LogWarning("[BuildAndTest] No connected device, skipping logcat.\n" + devices);
                return;
            }

            if (!RunAdb("logcat -d -s Unity", out string log, LogcatTimeoutMs))
            {
                Debug.LogWarning("[BuildAndTest] adb logcat failed (is a device connected?):\n" + log);
                return;
            }

            Directory.CreateDirectory(OutputDir);
            File.WriteAllText(LogcatPath, log);
            Debug.Log($"[BuildAndTest] Saved Unity logcat to {LogcatPath} ({log.Length} characters).");
        }

        // ------------------------------------------------------------------ build

        private static string BuildApk()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Debug.LogError("[BuildAndTest] Wait for Unity to finish compiling/importing, then try again.");
                return null;
            }

            Directory.CreateDirectory(OutputDir);
            var options = new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None // release APK: Development Build off
            };

            try
            {
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError(
                        $"[BuildAndTest] Build {summary.result} ({summary.totalErrors} error(s)). " +
                        "See the Console for details.");
                    return null;
                }
                Debug.Log(
                    $"[BuildAndTest] Built {summary.outputPath} ({summary.totalSize} bytes, " +
                    $"{summary.totalTime}).");
                return summary.outputPath;
            }
            catch (Exception ex)
            {
                // Most commonly the Android module is not installed for this editor.
                Debug.LogError("[BuildAndTest] Build threw: " + ex);
                return null;
            }
        }

        // -------------------------------------------------------------------- adb

        private static bool TryGetConnectedDevice(out string listing)
        {
            if (!RunAdb("devices", out listing, 30000))
            {
                listing = "Could not run adb. Set ANDROID_HOME/ANDROID_SDK_ROOT or add platform-tools to PATH.\n" + listing;
                return false;
            }

            foreach (string line in listing.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.EndsWith("\tdevice") || trimmed.EndsWith(" device")) return true;
            }
            return false;
        }

        /// <summary>Runs an adb subcommand and returns true on exit code 0.</summary>
        private static bool RunAdb(string arguments, out string output, int timeoutMs)
        {
            string adb = ResolveAdb();
            output = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo(adb, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        output = $"Failed to start '{adb} {arguments}'.";
                        return false;
                    }

                    // Drain both pipes asynchronously: a large logcat dump can fill
                    // one buffer while we block reading the other (classic deadlock).
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { /* best effort */ }
                        output = $"adb {arguments} timed out after {timeoutMs} ms.";
                        return false;
                    }

                    output = (stdoutTask.Result + "\n" + stderrTask.Result).Trim();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = $"Could not run '{adb} {arguments}': {ex.Message}";
                return false;
            }
        }

        /// <summary>Prefers the adb bundled with Unity's configured Android SDK, else PATH.</summary>
        private static string ResolveAdb()
        {
            string exe = Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb";
            foreach (string root in SdkRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, "platform-tools", exe);
                if (File.Exists(candidate)) return candidate;
            }
            return "adb"; // fall back to whatever is on PATH
        }

        private static IEnumerable<string> SdkRoots()
        {
            yield return Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
            yield return Environment.GetEnvironmentVariable("ANDROID_HOME");
            yield return UnityAndroidSdkRoot();
        }

        /// <summary>
        /// Reads the SDK path Unity's Android extension has configured. Done by
        /// reflection so this file still compiles if that module is not installed.
        /// </summary>
        private static string UnityAndroidSdkRoot()
        {
            try
            {
                Type type = Type.GetType(
                    "UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions");
                PropertyInfo property = type?.GetProperty(
                    "sdkRootPath", BindingFlags.Public | BindingFlags.Static);
                return property?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
