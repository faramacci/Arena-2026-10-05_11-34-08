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
        public static FighterSnapshot Capture(FighterSim fighter) => new FighterSnapshot
        {
            position = fighter.Position, forward = fighter.Forward, health = fighter.Health,
            dead = fighter.Dead, cooldowns = (float[])fighter.Cooldowns.Clone()
        };
        public void Apply(FighterSim fighter)
        {
            if (health < fighter.Health) fighter.View.PlayHitFlash();
            fighter.Position = position; fighter.Forward = forward;
            fighter.Health = health; fighter.Dead = dead;
            Array.Copy(cooldowns, fighter.Cooldowns, 3);
            fighter.View.transform.position = position;
            fighter.View.SetFacing(forward);
            fighter.View.SetHealth(health, Core.GameConfig.MaxHealth);
        }
    }

    [Serializable]
    public sealed class EffectSnapshot
    {
        public int primitive;
        public Vector3 position;
        public Vector3 scale;
        public Color color;
    }

    [Serializable]
    public sealed class MatchSnapshot
    {
        public FighterSnapshot host;
        public FighterSnapshot guest;
        public EffectSnapshot[] effects;
    }
}
