using UnityEditor;
using UnityEngine;

namespace Arena.Tools
{
    /// <summary>
    /// Keeps the open scene clean across Play Mode sessions.
    ///
    /// Enter Play Mode Options ship with Reload Domain and Reload Scene both off, so
    /// Unity does not rebuild the scene when Play starts or stops: everything the game
    /// built is still there afterwards, and whatever the match was doing when it was
    /// stopped stays on the sand. That is how a fan of thrown knives and a shield end up
    /// lying in the arena of the *next* session, and why they are still visible in the
    /// Scene view with the game stopped.
    ///
    /// The sweep is the same one the bootstrap runs at boot
    /// (GameBootstrap.ClearLeftoverSceneObjects): only objects this game builds are touched, and the
    /// scene's own camera, light and volume are never in that list.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeLeftovers
    {
        /// <summary>Scratch roots the editor's staging scenes use; the game owns none of these.</summary>
        private static readonly string[] ScratchRoots = { "FxStageRoot", "FxTrailRoot", "PropLeakRoot" };

        static PlayModeLeftovers()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            int gone = Sweep();
            if (gone > 0)
                Debug.Log("[Bootstrap] Play Mode stopped: cleared " + gone +
                          " object(s) it had built, so the scene is as it was.");
        }

        [MenuItem("Tools/Arena/Clear Play-Mode Leftovers")]
        public static void ClearFromMenu()
        {
            if (Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Arena",
                    "Stop Play Mode first: the sweep would delete the world the running "
                    + "match is played in.", "OK");
                return;
            }

            int gone = Sweep();
            Debug.Log(gone > 0
                ? "[Bootstrap] cleared " + gone + " leftover object(s) from the open scene."
                : "[Bootstrap] the open scene has no leftovers - nothing to clear.");
        }

        private static int Sweep() => GameBootstrap.ClearLeftoverSceneObjects(ScratchRoots);
    }
}
