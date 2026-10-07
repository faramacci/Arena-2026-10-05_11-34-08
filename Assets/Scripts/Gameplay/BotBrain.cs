using UnityEngine;
using Arena.Core;

namespace Arena.Gameplay
{
    /// <summary>
    /// The opponent's decision brain: stay inside the zone, keep a useful
    /// approach distance, strafe, and fire at most one spell per decision tick.
    /// It only reads fighter state and reports through the fighters it is given;
    /// the actual cast goes back through <see cref="MatchSim.Cast"/> so cooldown,
    /// stun and damage rules stay in one place.
    /// </summary>
    internal static class BotBrain
    {
        public static void Tick(FighterSim bot, FighterSim opponent, float dt,
            ZoneField zone, System.Func<FighterSim, int, bool> cast)
        {
            if (bot.Stun > 0f)
            {
                bot.DesiredDir = Vector3.zero;
                return;
            }

            Vector3 toTarget = opponent.Position - bot.Position;
            toTarget.y = 0f;
            float d = toTarget.magnitude;

            bot.DecisionTimer -= dt;
            if (bot.DecisionTimer > 0f) return;

            bot.DecisionTimer = Random.Range(GameConfig.BotDecisionMin, GameConfig.BotDecisionMax);

            if (zone.IsOutside(bot.Position))
            {
                // Safety first: run back toward the middle of the arena.
                bot.DesiredDir = (-bot.Position).normalized;
            }
            else if (d > 7f)
            {
                bot.DesiredDir = toTarget / d;
            }
            else if (d <= 0.0001f)
            {
                bot.DesiredDir = Vector3.right;
            }
            else if (d < 2f)
            {
                // Too close — give the charge some room.
                bot.DesiredDir = -(toTarget / d);
            }
            else
            {
                bot.StrafeSign = Random.value < 0.5f ? 1f : -1f;
                Vector3 side = Vector3.Cross(Vector3.up, toTarget / d) * bot.StrafeSign;
                bot.DesiredDir = ((toTarget / d) * 0.6f + side * 0.8f).normalized;
            }

            TryCast(bot, d, toTarget, cast);
        }

        private static void TryCast(FighterSim bot, float d, Vector3 toTarget,
            System.Func<FighterSim, int, bool> cast)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                if (bot.Cooldowns[slot] > 0f) continue;

                AbilityDef ab = bot.Def.Get((AbilitySlot)slot);
                bool inRange;
                switch (ab.Id)
                {
                    case "charge":
                        inRange = d > 2.5f && d < 13f;
                        break;
                    case "blink":
                        inRange = d > 6f && d < 16f;
                        break;
                    default:
                        inRange = ab.Range > 0f ? d <= ab.Range * 0.95f : d <= 12f;
                        break;
                }

                if (!inRange) continue;
                if (Random.value > GameConfig.BotCastChance) continue; // hold the trigger

                if (toTarget.sqrMagnitude > 0.0001f)
                    bot.Forward = toTarget.normalized;
                cast(bot, slot);
                return; // one spell per decision tick
            }
        }
    }
}
