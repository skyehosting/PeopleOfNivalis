using System.Collections.Generic;

namespace NivalisAIChat.Game;

/// <summary>
/// Plain copy of the world state at the moment of talking. Captured on Unity's main thread,
/// then handed to the (thread-pool) AI code, which must never touch IL2CPP objects.
/// </summary>
public sealed class MenuEntry
{
    public string Venue = "";
    public string Dish = "";
    public int Price;
}

public sealed class GameSnapshot
{
    public int GameDay;
    public string DayOfWeek = "";
    public int Hour = 12;
    public int Minute;
    public string Weather = "";
    /// <summary>The phone: their number (made for them the first time), whether you have it, and your last texts with them.</summary>
    public string MyNumber = "";
    public bool HasYourNumber;
    public List<string> RecentTexts = new();
    public string Location = "";
    /// <summary>CF-18: the venue they're at right now, by where their body is (the game's own venue areas); "" none near.</summary>
    public string NpcPlace = "";
    public string LocationDescription = "";
    public string PlayerName = "";
    public int PlayerRank;
    public List<string> PlayerVenues = new();
    /// <summary>What the NPC was doing when approached, in plain words.</summary>
    public string NpcActivity = "";
    /// <summary>Where they work according to the game (venue name), their roles, and who they work for.</summary>
    public string NpcWorkplace = "";
    public string NpcRoles = "";
    public bool NpcWorksForPlayer;
    /// <summary>FB-65: the plans and reminders made with this person (they know what you agreed).</summary>
    public List<string> Plans = new();
    /// <summary>
    /// FB-62/63: [WHAT YOU'D DO]: what they would do if asked (come along, date, move in), decided by the code before they
    /// answer, so their words and what happens agree.
    /// </summary>
    public List<string> WouldDo = new();
    /// <summary>FB-85: out with the player (a date, or a night at theirs): a conversation running out ends the talk, not the evening.</summary>
    public bool OutWithPlayer;
    /// <summary>FB-67: lines other mods (plugins) add about the world, for this person's prompt.</summary>
    public List<string> OtherMods = new();
    /// <summary>CF-11: at work right now (a staff task, a working activity, or their shift): a chat can't make them serve anyone.</summary>
    public bool NpcAtWork;
    public string NpcEmployer = "";
    public int NpcWage;
    /// <summary>FB-57: the shop or stall they run, as the game has it right now; null if they don't run one.</summary>
    public Npc.ShopFacts? Shop;
    /// <summary>FB-58: one of the player's staff: how the game says they feel about the job, their pay and hours; null otherwise.</summary>
    public Npc.WorkReading? Job;
    /// <summary>Venue they're visiting as a customer, and whether the player owns it.</summary>
    public string NpcVisiting = "";
    public bool NpcVisitingPlayerVenue;
    /// <summary>The game's own relationship levels with the player (named characters).</summary>
    public string NpcRelationship = "";
    /// <summary>The game says the player has met this person in the story (or they're on the player's staff).</summary>
    public bool NpcMetPlayer;
    /// <summary>Days this person has eaten at one of the player's places (counted by the mod, at most once a day).</summary>
    public int NpcVisitsToYourPlace;
    /// <summary>The player's own gender identity from the config (NPCs only see how it comes across).</summary>
    public string PlayerGender = "";
    /// <summary>Talk around this district (gossip about the player), recent headlines, and the player's menus.</summary>
    public List<string> Rumors = new();
    public List<string> Headlines = new();
    public List<MenuEntry> Menu = new();
    /// <summary>Shortages / surpluses at the market this person knows about, and whether it's on their mind today.</summary>
    public List<string> Market = new();
    public bool MarketOnMind;
    /// <summary>There is a shortage or surplus right now, but this person hasn't heard.</summary>
    public bool MarketUnknown;
    /// <summary>Real places in town this person likes to eat, as they know them; or they rarely eat out.</summary>
    public List<string> FoodPlaces = new();
    public bool EatsAtHome;
    /// <summary>FB-35: the group they've talked with (others' first names), how often, and last time's talk; empty if none.</summary>
    public string YourGroup = "", YourGroupTalk = "";
    /// <summary>How this meeting began when they reacted to the player first (waved them over, snubbed them...), and the mood they were in.</summary>
    public string Approach = "";
    /// <summary>Who they were talking with when you walked up (they broke off to talk to you), or "".</summary>
    public string Interrupted = "";
    public string ApproachMood = "";
    /// <summary>
    /// The inner-life round: [INSIDE YOU RIGHT NOW] (Mind.InnerCard: what's on their mind, their body, their love life,
    /// people they know nearby, what they saw), and how they feel underneath carried into this meeting (empty: rolled as always).
    /// </summary>
    public List<string> Inner = new();
    public string CarriedMood = "", CarriedIntensity = "", CarriedReason = "";

    public string Clock => $"{Hour:00}:{Minute:00}";

    public string PartOfDay => Hour switch
    {
        < 5 => "the dead of night",
        < 8 => "early morning",
        < 12 => "morning",
        < 14 => "midday",
        < 18 => "afternoon",
        < 21 => "evening",
        _ => "night",
    };
}
