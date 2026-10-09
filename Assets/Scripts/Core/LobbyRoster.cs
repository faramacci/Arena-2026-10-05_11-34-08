using System;
using System.Collections.Generic;

namespace Arena.Core
{
    /// <summary>What one lobby slot is doing right now.</summary>
    public enum SlotState
    {
        /// <summary>Not part of this match at all.</summary>
        Closed,
        /// <summary>Held open, waiting for a player to join.</summary>
        Open,
        /// <summary>A computer opponent.</summary>
        Bot,
        /// <summary>A human player.</summary>
        Player
    }

    [Serializable]
    public sealed class LobbySlot
    {
        public SlotState State = SlotState.Closed;
        public CharacterId Character = CharacterId.Vanguard;
        /// <summary>Player name, or "Computer" for a bot.</summary>
        public string Name = "Computer";

        public bool Occupied => State == SlotState.Bot || State == SlotState.Player;
    }

    /// <summary>
    /// The lobby roster, in the shape Warcraft 3 used: one leader owns a fixed
    /// row of slots, fills them with computers or leaves them open, and only the
    /// occupied slots take part in the match. Slot 0 always belongs to the
    /// leader, so nothing here can empty the roster below one human.
    ///
    /// This is deliberately a plain data model with no Unity or networking
    /// dependency: the menu, the future lobby screen and (once it can carry more
    /// than one peer) the LAN protocol all edit and read the same object, and the
    /// rules stay testable without a scene.
    /// </summary>
    public sealed class LobbyRoster
    {
        public const int MaxSlots = 8;
        /// <summary>A match needs the leader plus at least one more fighter.</summary>
        public const int MinParticipants = 2;
        public const string BotName = "Computer";

        private static readonly CharacterId[] AllCharacters =
            (CharacterId[])Enum.GetValues(typeof(CharacterId));

        private readonly List<LobbySlot> slots = new List<LobbySlot>();

        public LobbyRoster(int slotCount, CharacterId leaderCharacter, string leaderName = "Player")
        {
            if (slotCount < MinParticipants) slotCount = MinParticipants;
            if (slotCount > MaxSlots) slotCount = MaxSlots;

            for (int i = 0; i < slotCount; i++)
                slots.Add(new LobbySlot());

            slots[0].State = SlotState.Player;
            slots[0].Name = leaderName;
            slots[0].Character = leaderCharacter;
        }

        public int Count => slots.Count;
        public LobbySlot this[int index] => slots[index];
        /// <summary>Slot 0 is the leader's own seat, on every device.</summary>
        public static int LeaderIndex => 0;

        public int PlayerCount => CountStates(SlotState.Player);
        public int BotCount => CountStates(SlotState.Bot);
        /// <summary>Slots that will actually fight: humans plus computers.</summary>
        public int ParticipantCount => PlayerCount + BotCount;
        public bool CanStart => ParticipantCount >= MinParticipants;

        private int CountStates(SlotState state)
        {
            int total = 0;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].State == state) total++;
            return total;
        }

        public bool IsLeader(int index) => index == LeaderIndex;

        /// <summary>Lowest slot a joining player may take, or -1 when the lobby is full.</summary>
        public int FirstOpenSlot
        {
            get
            {
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i].State == SlotState.Open) return i;
                return -1;
            }
        }

        /// <summary>
        /// The leader's quick setup: slots 1..<paramref name="bots"/> become
        /// computers whose characters cycle from <paramref name="firstCharacter"/>,
        /// and every other slot is closed. Cycling means a six-bot match is not
        /// six copies of the same fighter.
        /// </summary>
        public void SetBotCount(int bots, CharacterId firstCharacter)
        {
            if (bots < 1) bots = 1;
            if (bots > slots.Count - 1) bots = slots.Count - 1;

            for (int i = 1; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.State == SlotState.Player) continue;   // never evict a human
                if (i <= bots)
                {
                    slot.State = SlotState.Bot;
                    slot.Name = BotName;
                    slot.Character = CycleCharacter(firstCharacter, i - 1);
                }
                else
                {
                    slot.State = SlotState.Closed;
                }
            }
        }

        /// <summary>Leader action: the first free slot becomes a bot.</summary>
        public bool AddBot(CharacterId character)
        {
            for (int i = 1; i < slots.Count; i++)
            {
                if (slots[i].Occupied) continue;
                slots[i].State = SlotState.Bot;
                slots[i].Name = BotName;
                slots[i].Character = character;
                return true;
            }
            return false;
        }

        /// <summary>Leader action: the last bot is removed and its slot opened for a player.</summary>
        public bool RemoveBot()
        {
            for (int i = slots.Count - 1; i >= 1; i--)
            {
                if (slots[i].State != SlotState.Bot) continue;
                slots[i].State = SlotState.Open;
                return true;
            }
            return false;
        }

        /// <summary>Leader action: hold this free slot open for a human.</summary>
        public bool Open(int index) => SetState(index, SlotState.Open);

        /// <summary>Leader action: drop this free slot out of the match.</summary>
        public bool Close(int index) => SetState(index, SlotState.Closed);

        private bool SetState(int index, SlotState state)
        {
            if (index <= LeaderIndex || index >= slots.Count) return false;
            if (slots[index].State == SlotState.Player) return false;
            slots[index].State = state;
            return true;
        }

        /// <summary>A player (or the host on their behalf) takes the first open slot.</summary>
        public int Join(string name, CharacterId character)
        {
            int index = FirstOpenSlot;
            if (index < 0) return -1;
            slots[index].State = SlotState.Player;
            slots[index].Name = string.IsNullOrEmpty(name) ? "Player" : name;
            slots[index].Character = character;
            return index;
        }

        /// <summary>Leader action: a player's slot goes back to open. The leader cannot be kicked.</summary>
        public bool Kick(int index)
        {
            if (index <= LeaderIndex || index >= slots.Count) return false;
            if (slots[index].State != SlotState.Player) return false;
            slots[index].State = SlotState.Open;
            slots[index].Name = "Open";
            return true;
        }

        /// <summary>A player leaves under their own steam; same result as a kick.</summary>
        public bool Leave(int index) => Kick(index);

        /// <summary>Any occupied slot can change fighter; the caller decides who may ask.</summary>
        public bool SetCharacter(int index, CharacterId character)
        {
            if (index < 0 || index >= slots.Count) return false;
            if (!slots[index].Occupied) return false;
            slots[index].Character = character;
            return true;
        }

        public bool SetName(int index, string name)
        {
            if (index < 0 || index >= slots.Count) return false;
            if (slots[index].State != SlotState.Player) return false;
            slots[index].Name = name;
            return true;
        }

        /// <summary>Characters in slot order, for the fighters that will actually spawn.</summary>
        public List<CharacterId> OccupiedCharacters()
        {
            var characters = new List<CharacterId>();
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Occupied) characters.Add(slots[i].Character);
            return characters;
        }

        /// <summary>The character <paramref name="offset"/> steps on from <paramref name="start"/>, wrapping.</summary>
        public static CharacterId CycleCharacter(CharacterId start, int offset)
        {
            int first = Array.IndexOf(AllCharacters, start);
            if (first < 0) first = 0;
            int length = AllCharacters.Length;
            int index = ((first + offset) % length + length) % length;
            return AllCharacters[index];
        }
    }
}
