using System;
using UnityEngine;

namespace Arena.Core
{
    public enum ZoneShape
    {
        Circle,
        Square
    }

    public enum GameMode
    {
        Bot,
        LanHost,
        LanJoin
    }

    public enum CharacterId
    {
        Vanguard,
        Storm
    }

    public enum AbilitySlot
    {
        Primary = 0,
        Secondary = 1,
        Ultimate = 2
    }

    /// <summary>
    /// Static, data-only description of one ability. Ported 1:1 from the HTML
    /// prototype's CHARACTERS table so balance numbers stay identical while the
    /// presentation layer is rebuilt for 3D / mobile.
    /// </summary>
    [Serializable]
    public struct AbilityDef
    {
        public string Id;
        public string DisplayName;
        [Tooltip("Seconds between casts.")]
        public float Cooldown;
        [TextArea(1, 3)]
        public string Description;

        // --- shared effect numbers, in world units / seconds ---
        public float Damage;
        public float KnockImpulse;
        public float Stun;
        public float Range;

        // --- per-ability extras ---
        [Tooltip("Primary-only: dash impulse applied to the caster.")]
        public float DashImpulse;
        [Tooltip("Secondary-only: blink distance for the Storm blink.")]
        public float BlinkDistance;
        [Tooltip("Secondary-only: i-frames granted after a blink.")]
        public float BlinkInvulnerability;
        [Tooltip("Ultimate-only: lifetime of the Storm vortex hazard.")]
        public float HazardLifetime;
        [Tooltip("Ultimate-only: radius of the vortex hazard pull.")]
        public float HazardRadius;
    }

    [Serializable]
    public class CharacterDef
    {
        public CharacterId Id;
        public string DisplayName;
        public string Role;
        [Tooltip("Emoji stand-in until real art lands.")]
        public string IconEmoji;
        public Color Color;
        public Color ColorLight;
        public AbilityDef[] Abilities;

        public AbilityDef Get(AbilitySlot slot) => Abilities[(int)slot];
    }

    /// <summary>
    /// Single source of truth for tuning. Numbers mirror the HTML prototype
    /// after converting its 1280x720 pixel space into world units at
    /// <see cref="PixelsPerUnitScale"/> (40px == 1u); the field itself keeps
    /// growing past the prototype's 32u x 18u — currently a 52u x 30u arena
    /// so there is real room to slide, chase and flank on the ice.
    /// </summary>
    public static class GameConfig
    {
        /// <summary>Prototype pixels per one world unit.</summary>
        public const float PixelsPerUnitScale = 40f;

        public static float Px(float pixels) => pixels / PixelsPerUnitScale;

        // ---- field / arena (square arena) ----
        public const float ArenaWidth = 32f;
        public const float ArenaHeight = 32f;
        public const float ArenaWallInset = 0.75f;    // 30px

        /// <summary>
        /// Half-extent of the playable square: the arena wall face, less the
        /// wall inset and the fighter's own radius.
        /// </summary>
        public const float PlayLimit =
            ArenaWidth * 0.5f - ArenaWallInset - PlayerRadius;      // 14.6

        /// <summary>
        /// The playable region is the square above with its four corners cut
        /// off: |x| + |z| &lt;= PlayDiagonal. The colosseum's seating is a
        /// <em>circle</em>, and the square's corners would reach past it
        /// (14.6, 14.6) is 20.6 from the centre, i.e. inside the second tier.
        /// Cutting the corners keeps a fighter on the sand for the whole reach
        /// of its movement, which a plain square clamp does not.
        ///
        /// Maximum reach from the centre is PlayDiagonal / sqrt(2) = 16.97, so
        /// even the biggest body stays clear of <see cref="SpectatorInnerRadius"/>.
        /// </summary>
        public const float PlayDiagonal = 24f;                       // |x| + |z|

        /// <summary>
        /// Inner face of the lowest seating tier — the first row of the
        /// spectator bowl. Shared by the simulation bound above and
        /// <see cref="Arena.Presentation.ArenaBuilder"/>, so the sand and the
        /// seating can never drift into each other.
        /// </summary>
        public const float SpectatorInnerRadius = 18f;

        /// <summary>
        /// Projects a point onto the playable octagon: clamp to the square,
        /// then walk the cut corner back along its diagonal.
        /// </summary>
        public static Vector3 ClampToPlay(Vector3 p)
        {
            p.x = Mathf.Clamp(p.x, -PlayLimit, PlayLimit);
            p.z = Mathf.Clamp(p.z, -PlayLimit, PlayLimit);

            float over = Mathf.Abs(p.x) + Mathf.Abs(p.z) - PlayDiagonal;
            if (over > 0f)
            {
                float cut = over * 0.5f;
                p.x -= Mathf.Sign(p.x) * cut;
                p.z -= Mathf.Sign(p.z) * cut;
            }

            p.y = 0f;
            return p;
        }

        /// <summary>Furthest a fighter's body can sit from the arena centre.</summary>
        public static float PlayReach =>
            PlayDiagonal * 0.70710678f + PlayerRadius;

        // ---- zone (scaled with the square field) ----
        public const float ZoneCircleRadius = 11.5f;
        public const float ZoneHalfWidth = 11f;
        public const float ZoneHalfHeight = 11f;
        /// <summary>Deliberately gentle: the zone pressures, it does not delete you.</summary>
        public const float ZoneDamagePerSecond = 6f;

        // ---- player body ----
        public const float PlayerRadius = 0.65f;      // 26px
        public const float MaxHealth = 100f;

        // ---- movement (ice physics) ----
        /// <summary>Per-frame friction factor, applied as pow(f, dt*60).</summary>
        public const float Friction = 0.98f;
        /// <summary>Acceleration while input is held (16u/s^2 == 640px/s^2).</summary>
        public const float Acceleration = 16f;
        /// <summary>Extra drag while charging, from the prototype's 0.90 factor.</summary>
        public const float ChargeFriction = 0.90f;
        /// <summary>Wall restitution.</summary>
        public const float WallBounce = -0.55f;
        /// <summary>Player-vs-player restitution.</summary>
        public const float BodyRestitution = 0.85f;
        /// <summary>Separation speed that triggers the metallic clank.</summary>
        public const float ClankSpeedThreshold = 10.5f; // 420px/s

        // ---- simulation ----
        public const float FixedTimestep = 1f / 60f;
        public const int MaxCatchUpSteps = 5;

        // ---- combat feel (used by MatchSim) ----
        /// <summary>Thunder-orb travel speed, world units per second.</summary>
        public const float OrbSpeed = 18f;
        /// <summary>Seconds a charge can still connect after the dash starts.</summary>
        public const float ChargeHitWindow = 0.5f;
        /// <summary>How hard the vortex drags a fighter toward its centre.</summary>
        public const float VortexPullSpeed = 14f;
        /// <summary>Lifetime of the expanding ring left by an area spell.</summary>
        public const float RingFxSeconds = 0.28f;

        // ---- bot brain ----
        public const float BotDecisionMin = 0.35f;
        public const float BotDecisionMax = 0.70f;
        /// <summary>Chance the bot actually fires when a spell is in range.</summary>
        public const float BotCastChance = 0.65f;

        // ---- countdown ----
        public const float CountdownSeconds = 3f;
        public const float MatchEndDelay = 1f;

        // ---- camera (Dota2 / LoL style fixed overhead rig) ----
        public const float CameraPitch = 58f;         // degrees below horizon
        public const float CameraYaw = 0f;
        public const float CameraDistance = 26f;
        public const bool CameraOrthographic = true;
        /// <summary>Wide enough to frame the whole 32u x 32u square field with comfortable margin.</summary>
        public const float CameraOrthoSize = 17f;

        // Derivatives need Mathf.Sin/Cos, which are not compile-time constants.
        public static readonly float CameraHeight =
            CameraDistance * Mathf.Sin(CameraPitch * Mathf.Deg2Rad);
        public static readonly float CameraForward =
            CameraDistance * Mathf.Cos(CameraPitch * Mathf.Deg2Rad);

        // ---- characters ----
        private static CharacterDef _vanguard;
        private static CharacterDef _storm;

        public static CharacterDef Vanguard
        {
            get
            {
                if (_vanguard == null)
                {
                    _vanguard = new CharacterDef
                    {
                        Id = CharacterId.Vanguard,
                        DisplayName = "Ice Vanguard",
                        Role = "Tank · Controller",
                        IconEmoji = "V",
                        Color = new Color(0.247f, 0.592f, 1f),
                        ColorLight = new Color(0.647f, 0.859f, 1f),
                        Abilities = new[]
                        {
                            new AbilityDef
                            {
                                Id = "charge",
                                DisplayName = "Ice Charge",
                                Description = "Dash and launch the foe",
                                Cooldown = 3.2f,
                                Damage = 9f,
                                KnockImpulse = 30f,      // 1200px/s
                                Stun = 0.35f,
                                Range = 0f,
                                DashImpulse = 37.5f      // 1500px/s
                            },
                            new AbilityDef
                            {
                                Id = "shockwave",
                                DisplayName = "Shockwave",
                                Description = "Blast around you",
                                Cooldown = 5.0f,
                                Damage = 6f,
                                KnockImpulse = 20f,      // 800px/s
                                Stun = 0.18f,
                                Range = 4.375f           // 175px
                            },
                            new AbilityDef
                            {
                                Id = "earthquake",
                                DisplayName = "Earthquake",
                                Description = "Slam that throws everyone",
                                Cooldown = 12f,
                                Damage = 14f,
                                KnockImpulse = 27f,      // 1080px/s
                                Stun = 0.45f,
                                Range = 7.5f             // 300px
                            }
                        }
                    };
                }
                return _vanguard;
            }
        }

        public static CharacterDef Storm
        {
            get
            {
                if (_storm == null)
                {
                    _storm = new CharacterDef
                    {
                        Id = CharacterId.Storm,
                        DisplayName = "Storm Mage",
                        Role = "Ranged Control Mage",
                        IconEmoji = "S",
                        Color = new Color(0.647f, 0.361f, 1f),
                        ColorLight = new Color(0.890f, 0.784f, 1f),
                        Abilities = new[]
                        {
                            new AbilityDef
                            {
                                Id = "thunder_orb",
                                DisplayName = "Thunder Orb",
                                Description = "Hurl a slamming energy orb",
                                Cooldown = 2.4f,
                                Damage = 9f,
                                KnockImpulse = 22.5f,    // 900px/s
                                Stun = 0.2f,
                                Range = 0f
                            },
                            new AbilityDef
                            {
                                Id = "blink",
                                DisplayName = "Blink",
                                Description = "Teleport + brief immunity",
                                Cooldown = 3.6f,
                                Damage = 0f,
                                KnockImpulse = 0f,
                                Stun = 0f,
                                Range = 0f,
                                BlinkDistance = 6f,      // 240px
                                BlinkInvulnerability = 0.35f
                            },
                            new AbilityDef
                            {
                                Id = "vortex",
                                DisplayName = "Vortex",
                                Description = "Pull in, then launch",
                                Cooldown = 11f,
                                Damage = 10f,
                                KnockImpulse = 24.5f,    // 980px/s
                                Stun = 0.3f,
                                Range = 6.5f,            // 260px burst radius
                                HazardLifetime = 1.7f,
                                HazardRadius = 6f        // 240px pull radius
                            }
                        }
                    };
                }
                return _storm;
            }
        }

        public static CharacterDef Get(CharacterId id) =>
            id == CharacterId.Vanguard ? Vanguard : Storm;
    }
}
