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
        Campaign,
        LanHost,
        LanJoin
    }

    public enum CharacterId
    {
        Vanguard,
        Storm,
        Assassin
    }

    public enum AbilitySlot
    {
        Primary = 0,
        Secondary = 1,
        Ultimate = 2
    }

    /// <summary>Silhouette a thrown ability spawns as.</summary>
    public enum ProjectileShape
    {
        Orb,
        Blade,
        /// <summary>Short, light throwing knife.</summary>
        Dagger,
        /// <summary>The Vanguard's aegis itself: a spinning shield slab.</summary>
        Shield
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
        [Tooltip("Projectile-only: how long the thrown weapon lives, in seconds.")]
        public float ProjectileLife;
        [Tooltip("Projectile-only: silhouette the thrown weapon spawns as.")]
        public ProjectileShape ProjectileShape;
        [Tooltip("Projectile-only: travel speed in u/s; 0 falls back to the shared OrbSpeed.")]
        public float ProjectileSpeed;
        [Tooltip("Projectile-only: metres the throw flies before it turns back home; 0 = one-way.")]
        public float ProjectileRange;
        [Tooltip("Volley-only: how many blades the fan throws at once.")]
        public int FanCount;
        [Tooltip("Volley-only: full arc of the fan, in degrees.")]
        public float FanSpread;
        [Tooltip("Buff-only: seconds the caster keeps the buff.")]
        public float BuffDuration;
        [Tooltip("Buff-only: top speed the buff holds the caster at, in u/s.")]
        public float BuffSpeed;
        [Tooltip("Buff-only: how hard the buff pulls velocity onto the stick, per second. " +
                 "High enough and the ice is gone.")]
        public float BuffGrip;
        [Tooltip("Aegis-only: damage the shield soaks before it breaks.")]
        public float ShieldAmount;
        [Tooltip("Aegis-only: seconds the shield lasts.")]
        public float ShieldDuration;
        [Tooltip("Rift-only: width of the ground rift corridor.")]
        public float RiftWidth;
        [Tooltip("Seconds of ground warning a line spell gives before it lands; 0 = instant.")]
        public float Telegraph;
        [Tooltip("Line-spell-only: how far the strike travels; 0 falls back to Range.")]
        public float LineLength;
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
    /// Single source of truth for tuning. Movement numbers mirror the HTML
    /// prototype after converting its 1280x720 pixel space into world units at
    /// <see cref="PixelsPerUnitScale"/> (40px == 1u); the field itself has since
    /// grown well past the prototype's 32u x 18u so a crowded match has room to
    /// chase and flank. Everything that follows from the field size is derived
    /// from <see cref="ArenaWidth"/>, so resizing the arena stays a one-number
    /// change and the octagon, the zone and the spectator bowl cannot drift
    /// apart.
    /// </summary>
    public static class GameConfig
    {
        /// <summary>Prototype pixels per one world unit.</summary>
        public const float PixelsPerUnitScale = 40f;

        public static float Px(float pixels) => pixels / PixelsPerUnitScale;

        // ---- field / arena (square arena) ----
        /// <summary>
        /// Side of the playable square. Sized for a crowded match rather than a
        /// duel: the camera rig follows the fighter (see
        /// <see cref="CameraOrthoSize"/>), so the whole field never has to be
        /// readable at once.
        /// </summary>
        public const float ArenaWidth = 64f;
        public const float ArenaHeight = 64f;
        public const float ArenaWallInset = 0.75f;    // 30px

        /// <summary>
        /// Half-extent of the playable square: the arena wall face, less the
        /// wall inset and the fighter's own radius.
        /// </summary>
        public const float PlayLimit =
            ArenaWidth * 0.5f - ArenaWallInset - PlayerRadius;      // 30.6

        /// <summary>
        /// The playable region is the square above with its four corners cut
        /// off: |x| + |z| &lt;= PlayDiagonal. The colosseum's seating is a
        /// <em>circle</em>, and the square's corners would reach past it, so the
        /// cut keeps a fighter on the sand for the whole reach of its movement.
        /// Kept at 0.75 of the field (it was 24 on the original 32u square).
        /// </summary>
        public const float PlayDiagonal = ArenaWidth * 0.75f;        // 48

        /// <summary>
        /// Inner face of the lowest seating tier — the first row of the
        /// spectator bowl, held 3u clear of the wall. Shared by the simulation
        /// bound above and <see cref="Arena.Presentation.ArenaBuilder"/>, so the
        /// sand and the seating can never drift into each other. Keep this at
        /// least <see cref="PlayReach"/> or fighters will stand in the seats.
        /// </summary>
        public const float SpectatorInnerRadius = ArenaWidth * 0.5f + 3f;    // 35

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
        // Fractions of the half-field, kept from the original tuning so the zone
        // still pressures a fighter without deleting them.
        private const float ZoneRadiusFraction = 0.72f;    // 11.5 of 16
        private const float ZoneSquareFraction = 0.6875f;  // 11 of 16

        public const float ZoneCircleRadius = ArenaWidth * 0.5f * ZoneRadiusFraction;
        public const float ZoneHalfWidth = ArenaWidth * 0.5f * ZoneSquareFraction;
        public const float ZoneHalfHeight = ZoneHalfWidth;
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
        /// <summary>Thrown-weapon travel speed, world units per second.</summary>
        public const float OrbSpeed = 18f;
        /// <summary>Seconds a charge can still connect after the dash starts.</summary>
        public const float ChargeHitWindow = 0.5f;
        /// <summary>How hard the vortex drags a fighter toward its centre.</summary>
        public const float VortexPullSpeed = 14f;
        /// <summary>Lifetime of the expanding ring left by an area spell.</summary>
        public const float RingFxSeconds = 0.28f;
        /// <summary>Lifetime of the ground rift left by the Earthrift.</summary>
        public const float RiftFxSeconds = 0.65f;
        /// <summary>How fast a thrown blade rolls about its own travel axis.</summary>
        public const float BladeSpinDegreesPerSecond = 720f;
        /// <summary>Defaults for a grip buff whose ability leaves the numbers at zero.</summary>
        public const float HasteTopSpeed = 18f;        public const float HasteGrip = 12f;
        /// <summary>Longest grip buff any ability hands out: the ceiling a guest accepts off the wire.</summary>
        public const float HasteMaxSeconds = 10f;

        // ---- bot brain ----
        public const float BotDecisionMin = 0.35f;
        public const float BotDecisionMax = 0.70f;
        /// <summary>Chance the bot actually fires when a spell is in range.</summary>
        public const float BotCastChance = 0.65f;

        // ---- countdown ----
        public const float CountdownSeconds = 3f;
        /// <summary>How many escalating stages the campaign runs for.</summary>
        public const int CampaignStages = 4;
        public const float MatchEndDelay = 1f;

        // ---- opening positions ----
        /// <summary>Half the distance between the two fighters at the start.</summary>
        public const float SpawnDistance = 10f;

        // ---- camera (Dota2 / LoL style fixed overhead rig) ----
        public const float CameraPitch = 58f;         // degrees below horizon
        public const float CameraYaw = 0f;
        public const float CameraDistance = 26f;
        public const bool CameraOrthographic = true;
        /// <summary>
        /// Vertical half-extent of the orthographic view. Deliberately does NOT
        /// frame the whole field any more: the rig follows the fighter, so the
        /// camera stays close enough that a fighter reads clearly on a phone —
        /// 11 shows about 22u of the 64u field.
        /// </summary>
        public const float CameraOrthoSize = 11f;

        // Derivatives need Mathf.Sin/Cos, which are not compile-time constants.
        public static readonly float CameraHeight =
            CameraDistance * Mathf.Sin(CameraPitch * Mathf.Deg2Rad);
        public static readonly float CameraForward =
            CameraDistance * Mathf.Cos(CameraPitch * Mathf.Deg2Rad);

        // ---- characters ----
        private static CharacterDef _vanguard;
        private static CharacterDef _storm;
        private static CharacterDef _assassin;

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
                                Id = "shield_throw",
                                DisplayName = "Shield Throw",
                                Description = "Hurl the aegis — out, then home again",
                                Cooldown = 5f,
                                Damage = 14f,
                                KnockImpulse = 9f,
                                Stun = 0.35f,
                                Range = 0f,
                                // The aegis itself is the weapon: it flies out,
                                // turns around and homes back to the hand it left,
                                // so the shield is only off the arm for a moment.
                                ProjectileShape = ProjectileShape.Shield,
                                ProjectileSpeed = 20f,
                                ProjectileRange = 9f,
                                ProjectileLife = 2.2f
                            },
                            new AbilityDef
                            {
                                Id = "frost_aegis",
                                DisplayName = "Frost Aegis",
                                Description = "Shield that soaks damage",
                                Cooldown = 9f,
                                Damage = 0f,
                                KnockImpulse = 0f,
                                Stun = 0f,
                                Range = 0f,
                                ShieldAmount = 35f,
                                ShieldDuration = 4f
                            },
                            new AbilityDef
                            {
                                Id = "earthrift",
                                DisplayName = "Earthrift",
                                Description = "Telegraph a line, then split it open",
                                Cooldown = 13f,
                                Damage = 18f,
                                KnockImpulse = 13f,
                                Stun = 0.5f,
                                Range = 11f,           // rift length
                                RiftWidth = 2.6f,
                                // The ground cracks late enough that a fighter in the
                                // corridor can step out of it.
                                Telegraph = 0.7f
                            }
                        }
                    };
                }
                return _vanguard;
            }
        }

        public static CharacterDef Storm
        {            get
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
                                Range = 0f,
                                ProjectileShape = ProjectileShape.Orb
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
                                Description = "Throw a pulling vortex down a line",
                                Cooldown = 11f,
                                Damage = 10f,
                                KnockImpulse = 24.5f,    // 980px/s
                                Stun = 0.3f,
                                Range = 6.5f,            // 260px burst radius
                                HazardLifetime = 1.7f,   // also the travel time down the line
                                HazardRadius = 6f,       // 240px pull radius
                                LineLength = 12f,        // thrown, not dropped at the caster
                                Telegraph = 0.55f
                            }
                        }
                    };
                }
                return _storm;
            }
        }

        public static CharacterDef Assassin
        {
            get
            {
                if (_assassin == null)
                {
                    _assassin = new CharacterDef
                    {
                        Id = CharacterId.Assassin,
                        DisplayName = "Night Assassin",
                        Role = "Skirmisher · Burst",
                        IconEmoji = "A",
                        // Crimson, deliberately deeper than the HUD's own alert red
                        // (UiFactory.Bad) so a fighter never reads as a health bar.
                        Color = new Color(0.804f, 0.129f, 0.184f),
                        ColorLight = new Color(1f, 0.580f, 0.560f),
                        Abilities = new[]
                        {
                            new AbilityDef
                            {
                                Id = "dagger_throw",
                                DisplayName = "Dagger Throw",
                                Description = "Throw a light, fast dagger",
                                Cooldown = 1.6f,
                                Damage = 10f,
                                KnockImpulse = 4f,
                                Stun = 0.05f,
                                Range = 0f,
                                ProjectileShape = ProjectileShape.Dagger,
                                ProjectileSpeed = 36f,     // twice the orb: a knife
                                ProjectileLife = 0.5f      // ~18u of poke
                            },
                            new AbilityDef
                            {
                                Id = "shadowstep",
                                DisplayName = "Shadowstep",
                                Description = "3s: grip the ground and run faster",
                                Cooldown = 8f,
                                Damage = 0f,
                                KnockImpulse = 0f,
                                Stun = 0f,
                                Range = 0f,
                                // The ice stops mattering: velocity tracks the
                                // stick, so he turns and stops on a coin and
                                // holds a higher top speed than anyone else.
                                BuffDuration = 3f,
                                BuffSpeed = 18f,
                                BuffGrip = 12f
                            },
                            new AbilityDef
                            {
                                Id = "blade_wave",
                                DisplayName = "Blade Wave",
                                Description = "Hurl a fan of blades",
                                Cooldown = 14f,
                                Damage = 10f,
                                KnockImpulse = 7f,
                                Stun = 0.15f,
                                Range = 0f,
                                ProjectileShape = ProjectileShape.Blade,
                                ProjectileSpeed = 22f,
                                ProjectileLife = 0.7f,     // ~15u
                                FanCount = 7,
                                FanSpread = 95f
                            }
                        }
                    };
                }
                return _assassin;
            }
        }

        public static CharacterDef Get(CharacterId id) => id switch
        {
            CharacterId.Assassin => Assassin,
            CharacterId.Storm => Storm,
            _ => Vanguard
        };
    }
}
