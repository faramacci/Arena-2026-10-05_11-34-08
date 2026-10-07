using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>
    /// Builds the Android release APK and copies it to a timestamped file under
    /// <c>build/to-send/</c>, ready to be shared to a phone (Telegram, Google
    /// Drive, etc.).
    ///
    /// This tool is deliberately device-free: it never talks to a connected
    /// device and never starts a child process. The APK is the only output.
    ///
    /// Menu: <b>Tools &gt; Arena &gt; Build APK Only</b>.
    /// </summary>
    public static class BuildAPK
    {
        /// <summary>Raw build output (Unity also uses this path for its own builds).</summary>
        private const string BuildPath = "build/outputs/Arena.apk";

        /// <summary>Where the timestamped, share-ready copies land.</summary>
        private const string SendDir = "build/to-send";

        private static readonly string[] Scenes = { "Assets/Scenes/SampleScene.unity" };

        [MenuItem("Tools/Arena/Build APK Only")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[BuildAPK] Exit Play Mode before building.");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Debug.LogError("[BuildAPK] Wait for Unity to finish compiling/importing, then try again.");
                return;
            }

            string apk = BuildApk();
            if (apk == null) return;

            string toSend = CopyForSending(apk);
            Debug.Log($"[BuildAPK] Done. Send this file to your phone:\n{toSend}");
        }

        private static string BuildApk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BuildPath));
            var options = new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = BuildPath,
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
                        $"[BuildAPK] Build {summary.result} ({summary.totalErrors} error(s)). " +
                        "See the Console for details.");
                    return null;
                }

                Debug.Log(
                    $"[BuildAPK] Built {summary.outputPath} " +
                    $"({summary.totalSize} bytes, {summary.totalTime}).");
                return summary.outputPath;
            }
            catch (Exception ex)
            {
                // Most commonly the Android module is not installed for this editor.
                Debug.LogError("[BuildAPK] Build threw: " + ex);
                return null;
            }
        }

        /// <summary>Copies the fresh APK to a timestamped, share-ready file.</summary>
        private static string CopyForSending(string builtApk)
        {
            Directory.CreateDirectory(SendDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm");
            string target = Path.Combine(SendDir, $"Arena-{stamp}.apk");
            File.Copy(builtApk, target, true);

            long size = new FileInfo(target).Length;
            Debug.Log($"[BuildAPK] Copied to {target} ({size} bytes).");
            return Path.GetFullPath(target);
        }
    }
}
