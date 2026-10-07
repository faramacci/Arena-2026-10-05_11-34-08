using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Drives the colosseum's ambient life from a single Update: candles and
    /// crystals bob, brazier flames flicker, and the centre sigil turns.
    ///
    /// <see cref="ArenaBuilder"/> hands over every prop once at build time, so
    /// the scene pays for one component and one pass instead of a script per
    /// prop — which matters on the mobile target.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArenaAmbient : MonoBehaviour
    {
        private Transform[] bob;
        private Vector3[] bobHome;
        private float[] bobPhase;
        private Transform[] flame;
        private Vector3[] flameHome;
        private float[] flamePhase;
        private Transform[] spin;

        public void Configure(List<Transform> bobbers, List<Transform> flames, List<Transform> spinners)
        {
            bob = bobbers != null ? bobbers.ToArray() : new Transform[0];
            bobHome = new Vector3[bob.Length];
            bobPhase = new float[bob.Length];
            for (int i = 0; i < bob.Length; i++)
            {
                bobHome[i] = bob[i] != null ? bob[i].localPosition : Vector3.zero;
                bobPhase[i] = Random.value * Mathf.PI * 2f;
            }

            flame = flames != null ? flames.ToArray() : new Transform[0];
            flameHome = new Vector3[flame.Length];
            flamePhase = new float[flame.Length];
            for (int i = 0; i < flame.Length; i++)
            {
                flameHome[i] = flame[i] != null ? flame[i].localPosition : Vector3.zero;
                flamePhase[i] = Random.value * Mathf.PI * 2f;
            }

            spin = spinners != null ? spinners.ToArray() : new Transform[0];
        }

        private void Update()
        {
            float t = Time.time;

            for (int i = 0; i < bob.Length; i++)
            {
                if (bob[i] == null) continue;
                float h = Mathf.Sin(t * 1.1f + bobPhase[i]) * 0.18f;
                bob[i].localPosition = bobHome[i] + new Vector3(0f, h, 0f);
            }

            for (int i = 0; i < flame.Length; i++)
            {
                if (flame[i] == null) continue;
                float k = 1f + Mathf.Sin(t * 9f + flamePhase[i]) * 0.10f
                             + Mathf.Sin(t * 21f + flamePhase[i] * 1.7f) * 0.05f;
                flame[i].localScale = new Vector3(k, 1.5f * k, k);
                flame[i].localPosition = flameHome[i] + new Vector3(0f, (k - 1f) * 0.3f, 0f);
            }

            for (int i = 0; i < spin.Length; i++)
                if (spin[i] != null)
                    spin[i].localRotation = Quaternion.Euler(0f, t * 6f, 0f);
        }
    }
}
