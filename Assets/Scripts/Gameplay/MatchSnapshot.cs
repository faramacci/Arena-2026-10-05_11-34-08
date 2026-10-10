using System;
using UnityEngine;

namespace Arena.Gameplay
{
    [Serializable]
    public sealed class FighterSnapshot
    {
        public Vector3 position;
        public Vector3 forward;
        public float health;
        public bool dead;
        public float[] cooldowns;
        /// <summary>Shield still standing, and the amount it started at.</summary>
        public float shield;
        public float shieldMax;
        /// <summary>Seconds left on the shield, so the guest can expire it too.</summary>
        public float shieldTimer;
        /// <summary>Seconds left of a grip/speed buff, so the guest shows the aura.</summary>
        public float haste;
        public float hasteMax;
        /// <summary>
        /// Top speed and pull that buff runs at. They travel with the buff instead
        /// of being read back off <see cref="Core.GameConfig"/> on the guest, so
        /// retuning an ability can no longer hand the two machines different
        /// movement while every snapshot still validates.
        /// </summary>
        public float hasteSpeed;
        public float hasteGrip;

        public static FighterSnapshot Capture(FighterSim fighter) => new FighterSnapshot
        {
            position = fighter.Position, forward = fighter.Forward, health = fighter.Health,
            dead = fighter.Dead, cooldowns = (float[])fighter.Cooldowns.Clone(),
            shield = fighter.Shield, shieldMax = fighter.ShieldMax,
            shieldTimer = fighter.ShieldTimer,
            haste = fighter.Haste, hasteMax = fighter.HasteMax,
            hasteSpeed = fighter.HasteSpeed, hasteGrip = fighter.HasteGrip
        };

        public void Apply(FighterSim fighter)
        {
            if (health < fighter.Health) fighter.View.PlayHitFlash();
            fighter.Position = position; fighter.Forward = forward;
            fighter.Health = health; fighter.Dead = dead;
            Array.Copy(cooldowns, fighter.Cooldowns, 3);
            fighter.Shield = shield; fighter.ShieldMax = shieldMax;
            fighter.ShieldTimer = shieldTimer;
            fighter.Haste = haste; fighter.HasteMax = hasteMax;
            fighter.HasteSpeed = hasteSpeed; fighter.HasteGrip = hasteGrip;
            fighter.View.transform.position = position;
            fighter.View.SetFacing(forward);
            fighter.View.SetHealth(health, Core.GameConfig.MaxHealth);
            fighter.View.SetShield(shield, shieldMax);
            fighter.View.SetHaste(haste, hasteMax);
        }
    }

    [Serializable]
    public sealed class EffectSnapshot
    {
        public int primitive;
        public Vector3 position;
        public Vector3 scale;
        public Color color;
        /// <summary>
        /// The hand prop this effect is, by <c>PropLibrary</c>'s wire id, or 0 for a
        /// plain primitive. A thrown shield is not a slab at any scale, so the guest
        /// draws the same prop asset when it can (see <c>PropLibrary</c>).
        /// </summary>
        public int prop;
        /// <summary>
        /// Snapshot slot of the fighter the effect belongs to: 0 is the local player,
        /// 1 the mirrored opponent. Unused unless <see cref="prop"/> is set, where it
        /// says which hand to empty.
        /// </summary>
        public int owner;
    }

    [Serializable]
    public sealed class MatchSnapshot
    {
        public FighterSnapshot host;
        public FighterSnapshot guest;
        public EffectSnapshot[] effects;
    }
}
