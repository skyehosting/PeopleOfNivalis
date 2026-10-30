namespace NivalisAIChat.Npc;

/// <summary>
/// FB-45: the features a player can switch off in the menu's Features tab, as the code reads them (kept in step with
/// [Features] in the settings file by ModRuntime). Off means skipped: the feature's starts, its writes, its part of every
/// prompt, and its pieces of the window; what it has saved is kept, so switching it back on carries on where it was.
/// The other features in that tab have had their own switches for a long time (Chatter, OrderTalk, NpcsGreetYou, Gossip...).
/// No Unity here.
/// </summary>
public static class Features
{
    /// <summary>People's moods: rolled each meeting, changed by what's said, in prompts, voices' tone, the window, patience and greetings.</summary>
    public static volatile bool Moods = true;
    /// <summary>How each person feels about you: the bar, prompts, waves and snubs, friend discounts, patience, "friend".</summary>
    public static volatile bool Relationships = true;
    /// <summary>Residents' phone calls (Dynamic and Advanced), and a phone going off at a table.</summary>
    public static volatile bool PhoneCalls = true;
    /// <summary>Residents thinking out loud.</summary>
    public static volatile bool Muttering = true;
    /// <summary>People sharing a table at a food place talking as their table.</summary>
    public static volatile bool TableConversations = true;
    /// <summary>People near a conversation remembering what they overheard.</summary>
    public static volatile bool Overhearing = true;
    /// <summary>FB-50: story characters keep the story conversations you've had with them, see them in the chat and act on them.</summary>
    public static volatile bool StoryMemory = true;
    /// <summary>FB-50: story characters talk the way they do in their story (a speech sheet written from the lines you've seen).</summary>
    public static volatile bool StoryVoice = true;
    /// <summary>FB-58: the player's staff: how they feel about the job, its pay and hours, and what happened at work.</summary>
    public static volatile bool WorkLife = true;
    /// <summary>FB-62: people follow you when asked (and willing).</summary>
    public static volatile bool Following = true;
    /// <summary>FB-63: dating, dates, living together, residents' couples and jealousy.</summary>
    public static volatile bool Romance = true;
    /// <summary>FB-65: plans and reminders made in a chat, shown when due.</summary>
    public static volatile bool Reminders = true;
    /// <summary>FB-66: talking to someone in a group brings the whole group into the chat.</summary>
    public static volatile bool GroupChat = true;

    /// <summary>
    /// The inner-life round: people carry how they feel, what's on their mind and what they saw from one moment to the next
    /// (per save), and it shows in their chats, mutters, residents' talk, turn cards and phrase ledger.
    /// </summary>
    public static volatile bool InnerLife = true;

    /// <summary>Roleplay: now and then, under a line, what they visibly do as they say it ("She rolls her eyes."), chosen by the AI.</summary>
    public static volatile bool Roleplay = true;

    /// <summary>The phone: numbers given in chats, texts with the people who gave them (and from them).</summary>
    public static volatile bool Phone = true;
    /// <summary>FB-71: Nightline, the city's social app on the phone (needs the phone).</summary>
    public static volatile bool Nightline = true;
    /// <summary>FB-76: world chat, a line to the people in front of you (no chat window).</summary>
    public static volatile bool WorldChat = true;

    /// <summary>FB-64: talking with your microphone in a chat (off by default: it needs a speech-to-text service).</summary>
    public static volatile bool VoiceInput;

    /// <summary>A mood as the rest of the code should see it: none while moods are off.</summary>
    public static string Mood(string? mood) => Moods ? mood ?? "" : "";

    /// <summary>The standing to act on: neutral (50, like a stranger's) while relationships are off.</summary>
    public static double Standing(double relationship) => Relationships ? relationship : Npc.Relationships.Neutral;

    /// <summary>The old -100..100 feeling to act on: 0 while relationships are off.</summary>
    public static int Feeling(int affinity) => Relationships ? affinity : 0;

    /// <summary>All on (tests).</summary>
    public static void Reset() => Moods = Relationships = PhoneCalls = Muttering = TableConversations = Overhearing = StoryMemory = StoryVoice = WorkLife
        = Following = Romance = Reminders = GroupChat = InnerLife = Roleplay = Phone = Nightline = WorldChat = true;
    // VoiceInput stays as it is (off unless a test turns it on).
}
