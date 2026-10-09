using UnityEngine;
using Arena.Core;

namespace Arena.Gameplay
{
    /// <summary>
    /// Stateless combat maths for one match: body-vs-body contact, the dash
    /// contact window, damage application and the ground-plane sweeps / wall
    /// clipping that spells need. Every method works on the fighters passed in
    /// and holds no state, so the exact same rules run on host, guest and in the
    /// regression tests.
    /// </summary>
    internal static class CombatResolver
    {
        /// <summary>Separates two overlapping fighters and bounces them apart.</summary>
        public static void ResolveBodyContact(FighterSim a, FighterSim b)
        {
            Vector3 delta = b.Position - a.Position;
            delta.y = 0f;
            float minDist = GameConfig.PlayerRadius * 2f;
            float dist = delta.magnitude;
            if (dist >= minDist) return;

            Vector3 n = dist > 0.0001f ? delta / dist : Vector3.right;
            float overlap = minDist - dist;
            a.Position -= n * (overlap * 0.5f);
            b.Position += n * (overlap * 0.5f);

            float rel = Vector3.Dot(a.Velocity - b.Velocity, n);
            if (rel > 0f)
            {
                float imp = rel * (1f + GameConfig.BodyRestitution) * 0.5f;
                a.Velocity -= n * imp;
                b.Velocity += n * imp;
            }
        }

        /// <summary>Contact window for the dash: whoever the charger touches goes flying.</summary>
        public static void ResolveCharge(FighterSim caster, FighterSim target)
        {
            if (caster.ChargeTimer <= 0f || target.Dead) return;
            if (!SweptContact(caster.PreviousPosition, caster.Position,
                target.PreviousPosition, target.Position,
                GameConfig.PlayerRadius * 2f + 0.35f, out float contact)) return;

            caster.ChargeTimer = 0f;
            Vector3 dir = Vector3.Lerp(target.PreviousPosition, target.Position, contact) -
                Vector3.Lerp(caster.PreviousPosition, caster.Position, contact);
            if (dir.sqrMagnitude < 0.0001f) dir = caster.Forward;
            ApplyHit(target, caster.ChargeAbility, dir);
        }

        /// <summary>Range-gated hit: a zero range means the spell always reaches.</summary>
        public static void TryHit(FighterSim caster, FighterSim target, AbilityDef ab,
            Vector3 dir, float range)
        {
            if (range <= 0f || FlatDistance(caster.Position, target.Position) <= range)
                ApplyHit(target, ab, dir);
        }

        /// <summary>
        /// Corridor hit for the Earthrift: the target has to sit inside the band
        /// that starts at <paramref name="origin"/> and runs <paramref name="length"/>
        /// along <paramref name="dir"/>, within <paramref name="halfWidth"/> of its
        /// centre line. The launch follows the rift, so the victim is thrown down
        /// the crack instead of radially away from the caster. The origin is passed
        /// in rather than read from the caster because the corridor lands after its
        /// warning, by which time the caster may have moved.
        /// </summary>
        public static bool TryLineHit(Vector3 origin, FighterSim target, AbilityDef ab,
            Vector3 dir, float length, float halfWidth)
        {
            if (target.Dead || target.Invulnerable > 0f) return false;
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 0.0001f) return false;
            flat.Normalize();

            Vector3 offset = target.Position - origin;
            offset.y = 0f;
            float along = Vector3.Dot(offset, flat);
            if (along < -GameConfig.PlayerRadius ||
                along > length + GameConfig.PlayerRadius) return false;

            // Cross product with the centre line gives the perpendicular offset.
            float across = Mathf.Abs(offset.x * flat.z - offset.z * flat.x);
            if (across > halfWidth + GameConfig.PlayerRadius) return false;

            ApplyHit(target, ab, flat);
            return true;
        }

        /// <summary>
        /// Runs incoming damage through the shield first and returns whatever is
        /// left for health. Every source of damage goes through here — spells and
        /// the zone — so a shield behaves the same wherever the damage came from.
        /// </summary>
        public static float Absorb(FighterSim target, float amount)
        {
            if (amount <= 0f || target.Shield <= 0f) return amount;

            float soaked = Mathf.Min(target.Shield, amount);
            target.Shield -= soaked;
            if (target.Shield <= 0f)
            {
                target.Shield = 0f;
                target.ShieldTimer = 0f;
            }
            return amount - soaked;
        }

        public static void ApplyHit(FighterSim target, AbilityDef ab, Vector3 dir)
        {
            if (target.Dead || target.Invulnerable > 0f) return;

            target.Health = Mathf.Max(0f, target.Health - Absorb(target, ab.Damage));
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            target.Velocity += dir.normalized * ab.KnockImpulse;
            if (ab.Stun > 0f) target.Stun = Mathf.Max(target.Stun, ab.Stun);
            target.ChargeTimer = 0f; // a clean hit cancels their own charge

            if (target.View != null)
            {
                target.View.PlayHitFlash();
                target.View.SetHealth(target.Health, GameConfig.MaxHealth);
                target.View.SetShield(target.Shield, target.ShieldMax);
            }

            if (target.Health <= 0f) target.Dead = true;
        }

        // The four diagonal faces of the playable octagon, as (x, z) signs.
        private static readonly float[] DiagonalX = { 1f, 1f, -1f, -1f };
        private static readonly float[] DiagonalZ = { 1f, -1f, 1f, -1f };

        /// <summary>
        /// Fraction of a segment that travels before it leaves the arena, used to
        /// clip orbs at the wall (and the cut corner faces).
        /// </summary>
        public static float WallFraction(Vector3 start, Vector3 end, float radius)
        {
            float x = GameConfig.ArenaWidth * 0.5f - 0.3f - radius;
            float z = GameConfig.ArenaHeight * 0.5f - 0.3f - radius;
            float diagonal = Mathf.Abs(start.x) + Mathf.Abs(start.z);
            if (Mathf.Abs(start.x) >= x || Mathf.Abs(start.z) >= z ||
                diagonal >= GameConfig.PlayDiagonal) return 0f;
            Vector3 delta = end - start;
            float fraction = 1f;
            if (end.x > x) fraction = Mathf.Min(fraction, (x - start.x) / delta.x);
            if (end.x < -x) fraction = Mathf.Min(fraction, (-x - start.x) / delta.x);
            if (end.z > z) fraction = Mathf.Min(fraction, (z - start.z) / delta.z);
            if (end.z < -z) fraction = Mathf.Min(fraction, (-z - start.z) / delta.z);

            // A shot aimed at a corner leaves through the cut face, not a wall.
            for (int i = 0; i < 4; i++)
            {
                float before = start.x * DiagonalX[i] + start.z * DiagonalZ[i];
                float after = end.x * DiagonalX[i] + end.z * DiagonalZ[i];
                if (after <= GameConfig.PlayDiagonal || after == before) continue;
                fraction = Mathf.Min(fraction,
                    (GameConfig.PlayDiagonal - before) / (after - before));
            }
            return Mathf.Clamp01(fraction);
        }

        /// <summary>Relative-motion circle sweep on the ground plane, earliest time of impact.</summary>
        public static bool SweptContact(Vector3 a0, Vector3 a1,
            Vector3 b0, Vector3 b1, float radius, out float time)
        {
            Vector3 offset = a0 - b0;
            Vector3 motion = (a1 - a0) - (b1 - b0);
            offset.y = motion.y = 0f;
            time = 0f;
            float c = offset.sqrMagnitude - radius * radius;
            if (c <= 0f) return true;
            float a = motion.sqrMagnitude;
            if (a <= 1e-10f) return false;
            float b = Vector3.Dot(offset, motion);
            float discriminant = b * b - a * c;
            if (discriminant < 0f) return false;
            time = (-b - Mathf.Sqrt(discriminant)) / a;
            return time >= 0f && time <= 1f;
        }

        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
