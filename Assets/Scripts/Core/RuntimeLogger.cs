using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>
    /// Captures every Unity console message (Log, Warning, Error, Exception) to a
    /// text file on the device, so a build can be tested **without a USB cable**:
    /// install the APK, play, then pull the log file back over a chat app and hand
    /// it to the agent.
    ///
    /// Behaviour:
    /// - Off in the editor, on in builds (override with
    ///   <see cref="EnsureRunning"/> for the safety test).
    /// - Buffers messages in memory and flushes every 30 s or once 100 are queued.
    /// - Keeps the newest <see cref="MaxFiles"/> log files and deletes the rest.
    ///
    /// The exact file path is shown on screen in builds (see
    /// <c>GameBootstrap.BuildLogPathLabel</c>).
    /// </summary>
    public sealed class RuntimeLogger : MonoBehaviour
    {
        /// <summary>Folder name under whichever root is writable.</summary>
        public const string FolderName = "arena-logs";

        /// <summary>How many of the newest log files to keep.</summary>
        public const int MaxFiles = 5;

        /// <summary>Seconds between automatic flushes.</summary>
        public const float FlushIntervalSeconds = 30f;

        /// <summary>Flush early once this many messages are buffered.</summary>
        public const int FlushAtCount = 100;

        private static RuntimeLogger instance;

        private readonly List<string> buffer = new List<string>();
        private float sinceFlush;
        private bool flushing;

        /// <summary>Absolute directory the logs are written to, or null before start.</summary>
        public static string LogDirectory { get; private set; }

        /// <summary>Absolute path of this session's log file, or null if unavailable.</summary>
        public static string CurrentFilePath { get; private set; }

        /// <summary>True while the logger is installed and capturing.</summary>
        public static bool IsRunning => instance != null;

        /// <summary>Messages waiting to be written to disk.</summary>
        public int BufferedCount => buffer.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Editor stays quiet; builds log automatically.
            if (Application.isEditor) return;
            EnsureRunning(false);
        }

        /// <summary>
        /// Creates the logger if it is not running. In the editor it does nothing
        /// unless <paramref name="forceInEditor"/> is true, which exists only so
        /// the safety test can exercise it without building.
        /// </summary>
        public static RuntimeLogger EnsureRunning(bool forceInEditor)
        {
            if (instance != null) return instance;
            if (Application.isEditor && !forceInEditor) return null;

            var go = new GameObject("RuntimeLogger");
            // DontDestroyOnLoad is play-mode only; in the editor safety test the
            // logger lives just for the test and is torn down by Shutdown().
            if (Application.isPlaying) DontDestroyOnLoad(go);
            var logger = go.AddComponent<RuntimeLogger>();
            // Unity only calls Awake on AddComponent while the player is running,
            // so the editor safety test needs the explicit kick below; in a build
            // Awake has already run and Initialize() returns immediately.
            logger.Initialize();
            return logger;
        }

        /// <summary>Stops capturing and destroys the logger.</summary>
        public static void Shutdown()
        {
            if (instance == null) return;
            var go = instance.gameObject;
            instance.Detach();
            instance = null;
            DestroyObject(go);
        }

        /// <summary>Destroy works in play mode; the editor safety test needs the immediate variant.</summary>
        private static void DestroyObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        private void Awake() => Initialize();

        private void Initialize()
        {
            if (instance == this) return;
            if (instance != null)
            {
                // A second logger is redundant; drop the newcomer.
                DestroyObject(gameObject);
                return;
            }
            instance = this;

            try
            {
                LogDirectory = ResolveLogDirectory();
                Directory.CreateDirectory(LogDirectory);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm");
                CurrentFilePath = Path.Combine(LogDirectory, "arena-runtime-log-" + stamp + ".txt");
                File.WriteAllText(CurrentFilePath, Header());
                PruneOldFiles(LogDirectory);
            }
            catch (Exception ex)
            {
                CurrentFilePath = null;
                Debug.LogWarning("[RuntimeLogger] Could not open the log file: " + ex.Message);
            }

            Application.logMessageReceived += OnLog;
            Debug.Log("[RuntimeLogger] Writing runtime log to " +
                      (CurrentFilePath ?? "(unavailable)"));
        }

        private void OnDestroy() => Detach();

        private void Detach() => Application.logMessageReceived -= OnLog;

        private void Update()
        {
            sinceFlush += Time.unscaledDeltaTime;
            if (sinceFlush >= FlushIntervalSeconds || buffer.Count >= FlushAtCount)
                Flush();
        }

        // Android suspends without OnApplicationQuit, so flush on pause too.
        private void OnApplicationPause(bool paused)
        {
            if (paused) Flush();
        }

        private void OnApplicationQuit() => Flush();

        private void OnLog(string message, string stackTrace, LogType type)
        {
            buffer.Add(Format(message, stackTrace, type));
            if (buffer.Count >= FlushAtCount) Flush();
        }

        /// <summary>Appends the buffered messages to the log file. Safe to call any time.</summary>
        public void Flush()
        {
            sinceFlush = 0f;
            if (string.IsNullOrEmpty(CurrentFilePath) || buffer.Count == 0 || flushing) return;

            flushing = true;
            try
            {
                var text = new StringBuilder();
                for (int i = 0; i < buffer.Count; i++) text.AppendLine(buffer[i]);
                buffer.Clear();
                File.AppendAllText(CurrentFilePath, text.ToString());
            }
            catch (Exception ex)
            {
                buffer.Clear();
                Debug.LogWarning("[RuntimeLogger] Flush failed: " + ex.Message);
            }
            finally
            {
                flushing = false;
            }
        }

        private static string Format(string message, string stackTrace, LogType type)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("][").Append(type).Append("] ").Append(message);
            if (!string.IsNullOrEmpty(stackTrace)) sb.Append('\n').Append(stackTrace);
            return sb.ToString();
        }

        private static string Header()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Arena runtime log");
            sb.AppendLine("Started : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Version : " + Application.version);
            sb.AppendLine("Unity   : " + Application.unityVersion);
            sb.AppendLine("Platform: " + Application.platform);
            sb.AppendLine("Device  : " + SystemInfo.deviceModel + " / " + SystemInfo.operatingSystem);
            sb.AppendLine(new string('-', 60));
            return sb.ToString();
        }

        /// <summary>
        /// Picks a folder the phone's file manager (and a chat app) can actually
        /// read. On Android 11+ <c>Android/data</c> is hidden from other apps, but
        /// <c>Android/media/&lt;package&gt;</c> stays readable, so that is preferred;
        /// <see cref="Application.persistentDataPath"/> is the fallback.
        /// </summary>
        private static string ResolveLogDirectory()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                string media = Path.Combine("/storage/emulated/0/Android/media",
                    Application.identifier, FolderName);
                Directory.CreateDirectory(media);
                return media;
            }
            catch
            {
                // Fall through to persistentDataPath below.
            }
#endif
            string fallback = Path.Combine(Application.persistentDataPath, FolderName);
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>Keeps only the newest <see cref="MaxFiles"/> log files.</summary>
        private static void PruneOldFiles(string directory)
        {
            try
            {
                var files = new List<FileInfo>(
                    new DirectoryInfo(directory).GetFiles("arena-runtime-log-*.txt"));
                if (files.Count <= MaxFiles) return;

                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = MaxFiles; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { /* best effort */ }
                }
            }
            catch
            {
                // Pruning is best effort; never let it break logging.
            }
        }
    }
}
