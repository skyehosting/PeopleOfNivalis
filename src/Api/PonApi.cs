using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Api;

/// <summary>What a plugin is told about someone: a read-only snapshot (never their whole record, never anyone under 18).</summary>
public sealed class NpcInfo
{
    /// <summary>People of Nivalis's id for them (stable for a save: "person_&lt;game guid&gt;").</summary>
    public string Id { get; init; } = "";
    /// <summary>Their name as the player knows it ("Stranger" until they've said it).</summary>
    public string Name { get; init; } = "";
    /// <summary>Where they stand with the player, 0-100 (50 = a stranger's).</summary>
    public double Relationship { get; init; } = 50;
    public string Mood { get; init; } = "";
    /// <summary>"" (none), "dating" or "living".</summary>
    public string Romance { get; init; } = "";
    public int TimesMet { get; init; }
    public string Occupation { get; init; } = "";
}

/// <summary>What a context provider is asked: who the chat is with, and where.</summary>
public sealed class ContextRequest
{
    public NpcInfo Npc { get; init; } = new();
    public string Location { get; init; } = "";
    public int GameDay { get; init; }
    public string Clock { get; init; } = "";
}

/// <summary>A plugin in BepInEx\plugins\PeopleOfNivalis\plugins: People of Nivalis loads it and calls <see cref="Load"/> once.</summary>
public interface IPeopleOfNivalisPlugin
{
    /// <summary>Short, unique, no spaces ("weather-moods"). Used in the log and to switch it off (DisabledPlugins).</summary>
    string Id { get; }
    string Name { get; }
    string Version { get; }
    /// <summary>Register what you need here (events, context, reply tags, filters). An exception here switches the plugin off.</summary>
    void Load();
    /// <summary>The mod is stopping (or the plugin was switched off): let go of anything you hold.</summary>
    void Unload();
}

/// <summary>
/// FB-67 (the author: "exposing our hooks ... so other users can develop their own mod dll's that would either override
/// some of our systems, hook into and enhance"): People of Nivalis's public API, version <see cref="Version"/>. Plugins
/// (a DLL in the plugins folder, or any BepInEx plugin with [BepInDependency("PeopleOfNivalis")]) register:
/// <list type="bullet">
/// <item>events: a chat opened or closed, the player's words, a line said, the relationship moving, a reminder due;</item>
/// <item>context: one short line about the world, added to the chat prompt ([FROM OTHER MODS]);</item>
/// <item>reply tags: [[tag: value]] the AI may add to a reply (with your instruction for when), handed to your handler;</item>
/// <item>filters: a line before it's shown and voiced (rewrite it, or "" to keep it as it is).</item>
/// </list>
/// Every call into a plugin is guarded: an exception is logged, and three switch the plugin off. Nothing here ever carries
/// an API key or anyone under 18. Docs: docs/PLUGINS.md.
/// </summary>
public static class PonApi
{
    public const int Version = 1;
    /// <summary>The most characters a context line may have (longer is cut).</summary>
    public const int MaxContextChars = 300;
    /// <summary>The most context lines in one prompt (the first registered first).</summary>
    public const int MaxContextLines = 4;
    private const int ErrorsBeforeOff = 3;

    // ------------------------------------------------------------------ events (subscribe with On(pluginId, ...))

    public static event Action<NpcInfo>? ChatOpened;
    public static event Action<NpcInfo>? ChatClosed;
    /// <summary>The player sent words to someone.</summary>
    public static event Action<NpcInfo, string>? PlayerSaid;
    /// <summary>Someone said a line to the player (after any filter).</summary>
    public static event Action<NpcInfo, string>? NpcSaid;
    /// <summary>Where they stand with the player moved (before, after; 0-100).</summary>
    public static event Action<NpcInfo, double, double>? RelationshipChanged;
    /// <summary>A plan or reminder came due (its words).</summary>
    public static event Action<string>? ReminderDue;

    // ------------------------------------------------------------------ registration

    private sealed class Owner
    {
        public string Id = "";
        public int Errors;
        public bool Off;
    }

    private sealed class Tag
    {
        public Owner Owner = null!;
        public string Name = "", Instruction = "";
        public Action<NpcInfo, string> Handler = null!;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Owner> Owners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(Owner owner, Func<ContextRequest, string?> provider)> Contexts = new();
    private static readonly List<Tag> Tags = new();
    private static readonly List<(Owner owner, Func<NpcInfo, string, string?> filter)> LineFilters = new();
    private static readonly List<(Owner owner, Delegate handler)> Subscriptions = new();

    /// <summary>Where the API's own messages go (the mod's log; set by the mod).</summary>
    public static Action<string> LogInfo { get; set; } = _ => { };
    public static Action<string> LogWarn { get; set; } = _ => { };

    /// <summary>A line in the mod's log, marked with the plugin's id.</summary>
    public static void Log(string pluginId, string message) => LogInfo($"[{Safe(pluginId)}] {message}");

    /// <summary>One short line about the world for the chat prompt (null or "" = nothing this time). Never called for anyone under 18.</summary>
    public static void RegisterContext(string pluginId, Func<ContextRequest, string?> provider)
    {
        if (provider == null) return;
        lock (Lock) Contexts.Add((OwnerOf(pluginId), provider));
    }

    /// <summary>
    /// A reply tag: the AI is told it may add [[<paramref name="tag"/>: value]] to a reply when <paramref name="instruction"/>
    /// says; the value goes to <paramref name="handler"/> (on the game's thread, after the line is shown). Letters a-z and _
    /// only (2-24); the mod's own tags can't be taken.
    /// </summary>
    public static bool RegisterReplyTag(string pluginId, string tag, string instruction, Action<NpcInfo, string> handler)
    {
        string t = (tag ?? "").Trim().ToLowerInvariant();
        if (t.Length is < 2 or > 24 || !t.All(ch => ch is >= 'a' and <= 'z' || ch == '_') || Reserved.Contains(t) || handler == null) return false;
        lock (Lock)
        {
            if (Tags.Any(x => x.Name == t)) return false;
            Tags.Add(new Tag { Owner = OwnerOf(pluginId), Name = t, Instruction = Cut(instruction, 200).Replace('[', '(').Replace(']', ')'), Handler = handler });
        }
        return true;
    }

    /// <summary>A filter for each line said to the player: return new words, or null/"" to keep them.</summary>
    public static void RegisterLineFilter(string pluginId, Func<NpcInfo, string, string?> filter)
    {
        if (filter == null) return;
        lock (Lock) LineFilters.Add((OwnerOf(pluginId), filter));
    }

    /// <summary>Subscribe to an event so it's let go with the plugin: PonApi.On("id", () => PonApi.ChatOpened += h, () => PonApi.ChatOpened -= h).</summary>
    public static void On(string pluginId, Action subscribe, Action unsubscribe)
    {
        var o = OwnerOf(pluginId);
        try { subscribe(); lock (Lock) Subscriptions.Add((o, unsubscribe)); }
        catch (Exception e) { Fault(o, "subscribing", e); }
    }

    /// <summary>The mod's own reply tags and words a plugin can't register.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "mood", "affinity", "leave", "name", "told", "favour", "favor", "fact", "gesture", "follow", "romance", "date", "move_in", "movein",
        "reminder", "plan", "rating", "intensity", "remember", "action", "do", // roleplay's [[action: ...]]
        // Review F-PON-2: every tag ReplyParser reads itself.
        "learned_name", "player_name", "gave_name", "name_revealed", "wants_food", "recall", "shared_identity", "self", "end",
        "end_conversation", "runs_place", "runs_a_place",
    };

    // ------------------------------------------------------------------ what the mod calls

    /// <summary>Lines for the chat prompt's [FROM OTHER MODS] (each cut to <see cref="MaxContextChars"/>, at most <see cref="MaxContextLines"/>).</summary>
    public static List<string> ContextFor(ContextRequest request)
    {
        List<(Owner owner, Func<ContextRequest, string?> provider)> all;
        lock (Lock) all = Contexts.Where(c => !c.owner.Off).ToList();
        var lines = new List<string>();
        foreach (var (owner, provider) in all)
        {
            if (lines.Count >= MaxContextLines) break;
            try
            {
                string? line = provider(request);
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(Cut(line, MaxContextChars));
                Fine(owner);
            }
            catch (Exception e) { Fault(owner, "giving context", e); }
        }
        return lines;
    }

    /// <summary>The prompt's instructions for the registered tags ("" with none).</summary>
    public static string TagInstructions()
    {
        List<Tag> tags;
        lock (Lock) tags = Tags.Where(t => !t.Owner.Off).ToList();
        if (tags.Count == 0) return "";
        return "[OTHER MODS' NOTES] You may also add these hidden notes at the end of your reply (never spoken): " +
               string.Join(" ", tags.Select(t => $"[[{t.Name}: value]] {t.Instruction}"));
    }

    public static bool HasTags { get { lock (Lock) return Tags.Any(t => !t.Owner.Off); } }

    public static bool HasContext { get { lock (Lock) return Contexts.Any(c => !c.owner.Off); } }

    /// <summary>The names of the registered tags (for the reply parser).</summary>
    public static HashSet<string> TagNames() { lock (Lock) return Tags.Where(t => !t.Owner.Off).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }

    /// <summary>A reply's plugin tags handed to their handlers.</summary>
    public static void HandleTags(NpcInfo npc, IReadOnlyDictionary<string, string> found)
    {
        if (found.Count == 0) return;
        List<Tag> tags;
        lock (Lock) tags = Tags.Where(t => !t.Owner.Off).ToList();
        foreach (var (name, value) in found)
        {
            var t = tags.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (t == null) continue;
            try { t.Handler(npc, Cut(value, 300)); Fine(t.Owner); }
            catch (Exception e) { Fault(t.Owner, $"handling [[{t.Name}]]", e); }
        }
    }

    /// <summary>A line through every filter, in order (a filter's empty answer keeps it).</summary>
    public static string FilterLine(NpcInfo npc, string line)
    {
        List<(Owner owner, Func<NpcInfo, string, string?> filter)> all;
        lock (Lock) all = LineFilters.Where(f => !f.owner.Off).ToList();
        foreach (var (owner, filter) in all)
        {
            try
            {
                string? changed = filter(npc, line);
                if (!string.IsNullOrWhiteSpace(changed)) line = Cut(changed, 1200);
                Fine(owner);
            }
            catch (Exception e) { Fault(owner, "filtering a line", e); }
        }
        return line;
    }

    public static void RaiseChatOpened(NpcInfo npc) => Raise(ChatOpened, h => h(npc));
    public static void RaiseChatClosed(NpcInfo npc) => Raise(ChatClosed, h => h(npc));
    public static void RaisePlayerSaid(NpcInfo npc, string text) => Raise(PlayerSaid, h => h(npc, text));
    public static void RaiseNpcSaid(NpcInfo npc, string text) => Raise(NpcSaid, h => h(npc, text));
    public static void RaiseRelationship(NpcInfo npc, double before, double after) => Raise(RelationshipChanged, h => h(npc, before, after));
    public static void RaiseReminder(string what) => Raise(ReminderDue, h => h(what));

    /// <summary>Each handler on its own: one that throws doesn't stop the others (and counts against its plugin).</summary>
    private static void Raise<T>(T? multicast, Action<T> call) where T : Delegate
    {
        if (multicast == null) return;
        foreach (var d in multicast.GetInvocationList())
        {
            try { call((T)d); }
            catch (Exception e) { FaultFor(d, e); }
        }
    }

    // ------------------------------------------------------------------ owners and faults

    private static Owner OwnerOf(string pluginId)
    {
        string id = Safe(pluginId);
        lock (Lock)
        {
            if (!Owners.TryGetValue(id, out var o)) Owners[id] = o = new Owner { Id = id };
            return o;
        }
    }

    /// <summary>Which plugin an assembly is (set by the loader for each plugin it loads; a BepInEx plugin isn't in it).</summary>
    private static readonly Dictionary<System.Reflection.Assembly, string> AssemblyOwner = new();

    /// <summary>Called when a plugin switched off should be told (its Unload). Set by the loader.</summary>
    public static Action<string>? SwitchedOff { get; set; }

    internal static void OwnAssembly(System.Reflection.Assembly asm, string pluginId) { lock (Lock) AssemblyOwner[asm] = Safe(pluginId); }

    private static void FaultFor(Delegate d, Exception e)
    {
        // Review F-PON-1: by the assembly the handler came from, the plugin it belongs to: it counts against that plugin, and
        // a plugin switched off has its handlers taken off the event (also ones added without On()).
        var asm = d.Method.DeclaringType?.Assembly;
        string? id = null;
        if (asm != null) lock (Lock) AssemblyOwner.TryGetValue(asm, out id);
        if (id == null) { LogWarn($"A plugin's event handler failed ({asm?.GetName().Name ?? "?"}): {e.GetType().Name}: {Cut(e.Message, 200)}"); return; }
        Fault(OwnerOf(id), "handling an event", e);
    }

    /// <summary>A call that worked: the error count starts again (three in a row switch a plugin off, not three in a session).</summary>
    private static void Fine(Owner o) { if (o.Errors > 0) lock (Lock) o.Errors = 0; }

    /// <summary>Every handler from a plugin's assembly taken off the events (it's off, or unloaded).</summary>
    private static void DropHandlers(string id)
    {
        HashSet<System.Reflection.Assembly> mine;
        lock (Lock) mine = AssemblyOwner.Where(kv => kv.Value == id).Select(kv => kv.Key).ToHashSet();
        if (mine.Count == 0) return;
        bool Mine(Delegate d) => d.Method.DeclaringType?.Assembly is { } a && mine.Contains(a);
        T? Strip<T>(T? ev) where T : Delegate
        {
            if (ev == null) return null;
            Delegate? kept = null;
            foreach (var d in ev.GetInvocationList()) if (!Mine(d)) kept = Delegate.Combine(kept, d);
            return (T?)kept;
        }
        ChatOpened = Strip(ChatOpened); ChatClosed = Strip(ChatClosed); PlayerSaid = Strip(PlayerSaid); NpcSaid = Strip(NpcSaid);
        RelationshipChanged = Strip(RelationshipChanged); ReminderDue = Strip(ReminderDue);
    }

    private static void Fault(Owner o, string doing, Exception e)
    {
        bool off;
        lock (Lock) { o.Errors++; off = o.Errors >= ErrorsBeforeOff && !o.Off; if (off) o.Off = true; }
        LogWarn($"Plugin '{o.Id}' failed {doing}: {e.GetType().Name}: {Cut(e.Message, 200)}{(off ? $" ({ErrorsBeforeOff} errors in a row: it's switched off until the game restarts)" : "")}");
        if (off)
        {
            Unsubscribe(o);
            DropHandlers(o.Id);
            try { SwitchedOff?.Invoke(o.Id); } catch { }
        }
    }

    /// <summary>Everything a plugin registered is let go (switched off, or unloaded).</summary>
    public static void Forget(string pluginId)
    {
        var o = OwnerOf(pluginId);
        lock (Lock)
        {
            o.Off = true;
            Contexts.RemoveAll(c => c.owner == o);
            Tags.RemoveAll(t => t.Owner == o);
            LineFilters.RemoveAll(f => f.owner == o);
        }
        Unsubscribe(o);
        DropHandlers(o.Id);
    }

    private static void Unsubscribe(Owner o)
    {
        List<Delegate> undo;
        lock (Lock) { undo = Subscriptions.Where(s => s.owner == o).Select(s => s.handler).ToList(); Subscriptions.RemoveAll(s => s.owner == o); }
        foreach (var u in undo) try { ((Action)u)(); } catch { }
    }

    public static bool IsOff(string pluginId) { lock (Lock) return Owners.TryGetValue(Safe(pluginId), out var o) && o.Off; }

    /// <summary>Tests: everything cleared.</summary>
    internal static void ResetForTests()
    {
        lock (Lock) { Owners.Clear(); Contexts.Clear(); Tags.Clear(); LineFilters.Clear(); Subscriptions.Clear(); AssemblyOwner.Clear(); }
        ChatOpened = ChatClosed = null; PlayerSaid = NpcSaid = null; RelationshipChanged = null; ReminderDue = null;
    }

    private static string Safe(string? id)
    {
        string s = new string((id ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
        return s.Length == 0 ? "plugin" : s.Length > 40 ? s[..40] : s;
    }

    private static string Cut(string? s, int max)
    {
        string t = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }
}
