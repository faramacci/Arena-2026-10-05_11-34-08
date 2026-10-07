using System.Collections.Generic;
using UnityEngine;
using Arena.Core;
using Arena.Presentation;

namespace Arena.Gameplay
{
    /// <summary>
    /// Owns every transient spell effect for one match: the spawned orbs, vortex
    /// hazards and expanding rings, plus the replica effects the guest rebuilds
    /// from host snapshots. <see cref="Clear"/> destroys all of them when the
    /// match ends; the fighter views are destroyed by the caller.
    /// </summary>
    internal sealed class FxSpawner
    {
        private readonly List<Projectile> projectiles = new List<Projectile>();
        private readonly List<VortexHazard> vortices = new List<VortexHazard>();
        private readonly List<RingFx> rings = new List<RingFx>();
        private readonly List<GameObject> replicatedEffects = new List<GameObject>();
        private GameObject fxRoot;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static Shader fxShader;
        private static Material fxMaterialTemplate;

        private GameObject FxRoot
        {
            get
            {
                if (fxRoot == null)
                {
                    fxRoot = new GameObject("MatchFx");
                }
                return fxRoot;
            }
        }

        // ------------------------------------------------------------- spawning

        public void SpawnProjectile(FighterSim caster, AbilityDef ab, Vector3 dir)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "Orb";
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = caster.Position + Vector3.up * 1.1f + dir * 0.9f;
            go.transform.localScale = Vector3.one * 0.75f;
            OwnedMaterial.Assign(go, MakeUnlit(caster.Def.ColorLight));

            projectiles.Add(new Projectile
            {
                Go = go,
                Velocity = dir * GameConfig.OrbSpeed,
                Ab = ab,
                Owner = caster,
                Life = 1.5f
            });
        }

        public void SpawnVortex(FighterSim caster, AbilityDef ab)
        {
            Vector3 centre = caster.Position;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "Vortex";
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = centre + Vector3.up * 0.06f;
            go.transform.localScale = new Vector3(ab.HazardRadius * 2f, 0.05f, ab.HazardRadius * 2f);
            OwnedMaterial.Assign(go, MakeUnlit(caster.Def.Color));

            vortices.Add(new VortexHazard
            {
                Go = go,
                Centre = centre,
                Radius = ab.HazardRadius,
                Life = ab.HazardLifetime,
                TotalLife = ab.HazardLifetime,
                Ab = ab,
                Owner = caster
            });
        }

        public void SpawnRing(Vector3 centre, float radius, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "RingFx";
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = centre + Vector3.up * 0.05f;
            go.transform.localScale = new Vector3(0.3f, 0.05f, 0.3f);
            OwnedMaterial.Assign(go, MakeUnlit(color));

            rings.Add(new RingFx { Go = go, MaxRadius = radius });
        }

        // --------------------------------------------------------------- ticking

        public void TickProjectiles(float dt, FighterSim local, FighterSim foe)
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                Projectile p = projectiles[i];
                Vector3 start = p.Go.transform.position;
                float travelTime = Mathf.Min(dt, Mathf.Max(0f, p.Life));
                Vector3 end = start + p.Velocity * travelTime;
                // Stop at the inner wall face, accounting for the orb radius.
                float wallFraction = CombatResolver.WallFraction(start, end, 0.375f);
                end = Vector3.Lerp(start, end, wallFraction);
                float targetFraction = travelTime / dt * wallFraction;
                FighterSim target = p.Owner == local ? foe : local;
                Vector3 targetEnd = Vector3.Lerp(target.PreviousPosition,
                    target.Position, targetFraction);
                if (wallFraction > 0f && !target.Dead && CombatResolver.SweptContact(start, end,
                    target.PreviousPosition, targetEnd,
                    GameConfig.PlayerRadius + 0.375f, out float contact))
                {
                    p.Go.transform.position = Vector3.Lerp(start, end, contact);
                    CombatResolver.ApplyHit(target, p.Ab, p.Velocity);
                    p.Life = 0f;
                }
                else
                {
                    p.Go.transform.position = end;
                    p.Life -= dt;
                    if (wallFraction < 1f) p.Life = 0f;
                }

                if (p.Life <= 0f)
                {
                    Object.Destroy(p.Go);
                    projectiles.RemoveAt(i);
                }
            }
        }

        public void TickVortices(float dt, FighterSim local, FighterSim foe)
        {
            for (int i = vortices.Count - 1; i >= 0; i--)
            {
                VortexHazard v = vortices[i];
                v.Life -= dt;

                // Visual: the ring breathes while it charges its burst.
                if (v.Go != null)
                {
                    float pulse = 1f + 0.06f * Mathf.Sin(Time.time * 14f);
                    float s = v.Radius * 2f * pulse * Mathf.Lerp(0.55f, 1f,
                        1f - Mathf.Clamp01(v.Life / v.TotalLife));
                    v.Go.transform.localScale = new Vector3(s, 0.05f, s);
                }

                FighterSim target = v.Owner == local ? foe : local;
                if (!target.Dead && target.Invulnerable <= 0f)
                {
                    Vector3 toCentre = v.Centre - target.Position;
                    toCentre.y = 0f;
                    float d = toCentre.magnitude;
                    if (d > 0.01f && d <= v.Radius)
                        target.Velocity += (toCentre / d) * (GameConfig.VortexPullSpeed * dt);
                }

                if (v.Life > 0f) continue;

                // Burst: whoever is still inside eats the launch.
                if (!target.Dead)
                {
                    Vector3 away = target.Position - v.Centre;
                    away.y = 0f;
                    if (away.sqrMagnitude <= v.Radius * v.Radius)
                        CombatResolver.ApplyHit(target, v.Ab, away.sqrMagnitude > 0.0001f ? away : Vector3.forward);
                }

                SpawnRing(v.Centre, v.Radius, v.Owner.Def.ColorLight);
                Object.Destroy(v.Go);
                vortices.RemoveAt(i);
            }
        }

        public void TickRings(float dt)
        {
            float duration = GameConfig.RingFxSeconds;
            for (int i = rings.Count - 1; i >= 0; i--)
            {
                RingFx r = rings[i];
                r.T += dt;
                float k = Mathf.Clamp01(r.T / duration);
                // A unit cylinder is 1 across, so diameter == 2 * radius.
                float diameter = Mathf.Lerp(0.4f, r.MaxRadius * 2f, k);
                if (r.Go != null)
                    r.Go.transform.localScale = new Vector3(diameter, 0.05f, diameter);

                if (r.T < duration) continue;
                Object.Destroy(r.Go);
                rings.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------- replication

        public void CaptureEffects(List<EffectSnapshot> effects)
        {
            void Add(GameObject go, PrimitiveType type, Color color)
            {
                effects.Add(new EffectSnapshot { primitive = (int)type,
                    position = go.transform.position, scale = go.transform.localScale, color = color });
            }
            foreach (var p in projectiles) Add(p.Go, PrimitiveType.Sphere, p.Owner.Def.ColorLight);
            foreach (var v in vortices) Add(v.Go, PrimitiveType.Cylinder, v.Owner.Def.Color);
            foreach (var r in rings)
                Add(r.Go, PrimitiveType.Cylinder, r.Go.GetComponent<Renderer>().sharedMaterial.GetColor(BaseColorId));
        }

        /// <summary>Rebuilds the guest's replica hierarchy to match a snapshot's effects.</summary>
        public void SyncReplicated(EffectSnapshot[] effects)
        {
            int count = effects.Length;
            for (int i = replicatedEffects.Count - 1; i >= count; i--)
            {
                Object.Destroy(replicatedEffects[i]); replicatedEffects.RemoveAt(i);
            }
            for (int i = 0; i < count; i++)
            {
                var effect = effects[i];
                string name = "Replica_" + effect.primitive;
                if (i < replicatedEffects.Count && replicatedEffects[i].name != name)
                { Object.Destroy(replicatedEffects[i]); replicatedEffects[i] = null; }
                GameObject go = i < replicatedEffects.Count ? replicatedEffects[i] : null;
                if (go == null)
                {
                    go = GameObject.CreatePrimitive((PrimitiveType)effect.primitive);
                    Object.Destroy(go.GetComponent<Collider>());
                    go.name = name; go.transform.SetParent(FxRoot.transform, false);
                    OwnedMaterial.Assign(go, MakeUnlit(effect.color));
                    if (i < replicatedEffects.Count) replicatedEffects[i] = go;
                    else replicatedEffects.Add(go);
                }
                go.transform.position = effect.position;
                go.transform.localScale = effect.scale;
                go.GetComponent<Renderer>().sharedMaterial.SetColor(BaseColorId, effect.color);
            }
        }

        /// <summary>Eases the guest's replica effects from the previous snapshot towards the newest.</summary>
        public void EaseReplicated(EffectSnapshot[] previous, EffectSnapshot[] to, float k)
        {
            if (to == null) return;
            for (int i = 0; i < replicatedEffects.Count && i < to.Length; i++)
            {
                GameObject go = replicatedEffects[i];
                if (go == null || to[i] == null) continue;
                Vector3 position = to[i].position;
                Vector3 scale = to[i].scale;
                // Only blend when the slot still holds the same kind of effect;
                // a respawned effect simply appears at its newest transform.
                if (previous != null && i < previous.Length && previous[i] != null &&
                    previous[i].primitive == to[i].primitive)
                {
                    position = Vector3.Lerp(previous[i].position, position, k);
                    scale = Vector3.Lerp(previous[i].scale, scale, k);
                }
                go.transform.position = position;
                go.transform.localScale = scale;
            }
        }

        /// <summary>Destroys every transient effect this spawner created.</summary>
        public void Clear()
        {
            foreach (var p in projectiles) if (p.Go != null) Object.Destroy(p.Go);
            foreach (var v in vortices) if (v.Go != null) Object.Destroy(v.Go);
            foreach (var r in rings) if (r.Go != null) Object.Destroy(r.Go);
            foreach (var go in replicatedEffects) if (go != null) Object.Destroy(go);
            replicatedEffects.Clear();
            projectiles.Clear();
            vortices.Clear();
            rings.Clear();
            if (fxRoot != null) Object.Destroy(fxRoot);
            fxRoot = null;
        }

        // ------------------------------------------------------------ effect data

        private sealed class Projectile
        {
            public GameObject Go;
            public Vector3 Velocity;
            public AbilityDef Ab;
            public FighterSim Owner;
            public float Life;
        }

        private sealed class VortexHazard
        {
            public GameObject Go;
            public Vector3 Centre;
            public float Radius;
            public float Life;
            public float TotalLife;
            public AbilityDef Ab;
            public FighterSim Owner;
        }

        private sealed class RingFx
        {
            public GameObject Go;
            public float T;
            public float MaxRadius;
        }

        private static Material MakeUnlit(Color c)
        {
            Material m = null;
            if (fxMaterialTemplate == null)
            {
                fxMaterialTemplate = Resources.Load<Material>("Arena/ArenaUnlit");
            }

            if (fxMaterialTemplate != null)
            {
                m = new Material(fxMaterialTemplate) { name = "SpellFx" };
            }
            else
            {
                if (fxShader == null)
                {
                    fxShader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (fxShader == null) fxShader = Shader.Find("Unlit/Color");
                }
                if (fxShader != null)
                {
                    m = new Material(fxShader) { name = "SpellFx" };
                }
            }

            if (m != null)
            {
                if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
                else m.color = c;
            }
            return m;
        }
    }
}
