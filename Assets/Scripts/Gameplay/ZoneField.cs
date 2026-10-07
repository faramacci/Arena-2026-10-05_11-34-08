using UnityEngine;
using Arena.Core;

namespace Arena.Gameplay
{
    /// <summary>
    /// The shrinking play area for one match: whether a point is outside it and
    /// the damage pressure that pushes fighters back in. Both shapes the menu
    /// offers (circle and square) live here so the sim and the HUD read the same
    /// boundary.
    /// </summary>
    internal sealed class ZoneField
    {
        public ZoneField(ZoneShape shape)
        {
            Shape = shape;
        }

        public ZoneShape Shape { get; }

        public bool IsOutside(Vector3 p)
        {
            if (Shape == ZoneShape.Circle)
            {
                float r = GameConfig.ZoneCircleRadius;
                return new Vector2(p.x, p.z).sqrMagnitude > r * r;
            }
            return Mathf.Abs(p.x) > GameConfig.ZoneHalfWidth ||
                   Mathf.Abs(p.z) > GameConfig.ZoneHalfHeight;
        }

        /// <summary>Marks the fighter dead if its health runs out to the zone.</summary>
        public void ApplyDamage(FighterSim f, float dt)
        {
            if (f.Dead || !IsOutside(f.Position)) return;

            f.Health = Mathf.Max(0f, f.Health - GameConfig.ZoneDamagePerSecond * dt);
            if (f.View != null) f.View.SetHealth(f.Health, GameConfig.MaxHealth);
            if (f.Health <= 0f) f.Dead = true;
        }
    }
}
