using System;
using System.IO;
using System.Linq;
using System.Text;
using Nivalis;
using Nivalis.GhostSystem.Ai;
using NivalisAIChat.Game;
using UnityEngine;

namespace NivalisAIChat.Dev;

/// <summary>
/// Developer test channel (config [Debug] DevCommands = true): commands written to
/// dev_commands.txt run on the main thread; results are appended to dev_output.txt.
/// </summary>
internal sealed class DevCommands
{
    private readonly string _in, _out, _shots;
    private float _next;

    public DevCommands(string dir)
    {
        _in = Path.Combine(dir, "dev_commands.txt");
        _out = Path.Combine(dir, "dev_output.txt");
        _shots = Path.Combine(dir, "shots");
        Directory.CreateDirectory(_shots);
        Write("dev channel open");
    }

    public void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        if (!File.Exists(_in)) return;
        string[] lines;
        try { lines = File.ReadAllLines(_in); File.WriteAllText(_in, ""); } catch { return; }
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try { Write("> " + line + "\n" + Run(line)); }
            catch (Exception e) { Write("> " + line + "\nERROR " + e); }
        }
    }

    private void Write(string s) =>
        File.AppendAllText(_out, $"[{DateTime.Now:HH:mm:ss}] {s}\n", Encoding.UTF8);

    private string Run(string line)
    {
        int sp = line.IndexOf(' ');
        string cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
        switch (cmd)
        {
            case "census": return Census(arg.Length > 0 ? float.Parse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : 25f);
            case "persons": return Persons();
            case "look":
                {
                    var c = NpcTargeting.LookedAt(ModRuntime.Config.MaxTalkDistance.Value);
                    return c == null ? "nothing" : Describe(c, 0);
                }
            case "open":
                {
                    var c = Nearest(arg.Length > 0 ? float.Parse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : 10f);
                    if (c == null) return "no eligible NPC nearby";
                    ModRuntime.OpenChat(c);
                    return "opened " + Describe(c, 0);
                }
            case "say": ModRuntime.Send(arg); return "sent";
            case "close": ModRuntime.CloseChat("dev"); return "closed";
            case "mock": ModRuntime.SetMock(arg != "off"); return "mock " + (arg != "off");
            case "shot":
                {
                    string name = Path.GetFileName(arg); // a name only: never a path out of the shots folder
                    string path = Path.Combine(_shots, (name.Length > 0 ? name : DateTime.Now.ToString("HHmmss")) + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    return path;
                }
            case "snap":
                {
                    // The LookAtNewPeople picture of whoever you're talking to (or the nearest person), saved to look at.
                    var c = ModRuntime.ChatPartner ?? Nearest(arg.Length > 0 && float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sr) ? sr : 6f);
                    var cam = GameContext.PlayerCamera();
                    if (c == null || cam == null) return "nobody to take a picture of";
                    var (jpeg, error) = Game.Snapshot.Take(c, cam);
                    if (jpeg == null) return "failed: " + error;
                    string path = Path.Combine(_shots, "snap-" + DateTime.Now.ToString("HHmmss") + ".jpg");
                    File.WriteAllBytes(path, jpeg);
                    return $"{path} ({jpeg.Length / 1024} KB) of {Describe(c, 0)}";
                }
            case "state": return State();
            case "chatter": return ModRuntime.DevChatter(arg);
            case "echo": return arg;
            case "hold":
                {
                    if (ModRuntime.ChatOpen) return "refused: a chat is open (would release the NPC you are talking to)";
                    var c = NearestMoving(arg.Length > 0 ? float.Parse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : 12f);
                    var cam = GameContext.PlayerCamera();
                    if (c == null || cam == null) return "no moving NPC nearby";
                    if (NpcHold.IsHeld(c)) return "refused: they're held by chatter (end that first: /chatter stop)"; // audit A8
                    NpcHold.Engage(c, cam.transform, ModRuntime.Config.AnimateNpc.Value);
                    return "holding " + Describe(c, 0);
                }
            case "holdany":
                {
                    if (ModRuntime.ChatOpen) return "refused: a chat is open (would release the NPC you are talking to)";
                    var c = float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? Nearest(r) : arg.Length > 0 ? ByName(arg) : Nearest(8f);
                    var cam = GameContext.PlayerCamera();
                    if (c == null || cam == null) return "no NPC nearby";
                    if (NpcHold.IsHeld(c)) return "refused: they're held by chatter (end that first: /chatter stop)"; // audit A8
                    string before = NpcHold.Describe(c) + " | " + GameContext.Capture(c).NpcActivity;
                    NpcHold.Engage(c, cam.transform, ModRuntime.Config.AnimateNpc.Value);
                    return "holding " + Describe(c, 0) + "\n  before: " + before;
                }
            case "release": if (ModRuntime.ChatOpen) return "refused: a chat is open"; NpcHold.Release(); return "released";
            case "ctx":
                {
                    var c = arg.Length > 0 ? ByName(arg) : Nearest(8f);
                    if (c == null) return "no NPC";
                    var s = GameContext.Capture(c);
                    var v = GameContext.Visual(c);
                    float scale = 0;
                    try { scale = c.Animator != null ? c.Animator.humanScale : 0; } catch { }
                    var canon = Npc.CanonAge.FromDescription(v.Description);
                    return $"{v.GameName} named={v.Named} employee={v.Employee} body={v.Body} home='{v.HomeDistrict}' minor={v.Minor} scale={scale:0.000} canonAge={(canon == null ? "-" : canon.Exact?.ToString() ?? $"{canon.Min}-{canon.Max} ({canon.Cue})")}\n  activity='{s.NpcActivity}'\n  work='{s.NpcWorkplace}' roles='{s.NpcRoles}' forPlayer={s.NpcWorksForPlayer} employer='{s.NpcEmployer}' wage={s.NpcWage}\n  visiting='{s.NpcVisiting}' playerVenue={s.NpcVisitingPlayerVenue} visits={s.NpcVisitsToYourPlace} metPlayer={s.NpcMetPlayer} relationship='{s.NpcRelationship}'\n  location='{s.Location}' player='{s.PlayerName}' venues='{string.Join(", ", s.PlayerVenues)}'\n  description='{v.Description}'";
                }
            case "news":
                return string.Join("\n", GameContext.ReadNews().Select(n => $"{n.title} | {Social.WorldMemory.FirstSentences(n.text, 160)}").DefaultIfEmpty("no news manager"));
            case "market":
                return string.Join("\n", GameContext.ReadMarket().Select(n => $"{n.Kind}: {n.Goods} {n.PricePercent:+0;-0;0}% since {n.SinceDays}d → {Social.MarketTalk.Describe(n)}").DefaultIfEmpty("no shortage or surplus right now"));
            case "places":
                return string.Join("\n", GameContext.ReadFoodPlaces().Select(p => $"{p.Name} ({p.Location}){(p.Players ? " [yours]" : "")}: {p.Dishes.Count} dishes, avg {p.AveragePrice:0} Lims; {string.Join(", ", p.Dishes.Take(4).Select(d => d.Dish + " " + d.Price))}").DefaultIfEmpty("no food places found"));
            case "menu":
                return string.Join("\n", GameContext.PlayerMenu().Select(m => $"{m.Venue}: {m.Dish} {m.Price}").DefaultIfEmpty("no menu"));
            case "held":
                {
                    var m = GameContext.HeldMeal();
                    return m == null ? "not holding a meal" : $"{m.Dish} base {m.BasePrice}";
                }
            case "anim":
                {
                    var c = arg.Length > 0 ? ByName(arg) : Nearest(8f);
                    var a = c?.Animator;
                    if (a == null) return "no animator";
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var p in a.parameters) if (p.type == AnimatorControllerParameterType.Trigger) names.Add(p.name);
                    return "triggers: " + string.Join(", ", names);
                }
            case "greet":
                {
                    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var kind = parts.Length > 0 && Enum.TryParse<Social.GreetingRules.Kind>(parts[0], true, out var k) ? k : Social.GreetingRules.Kind.Wave;
                    string who = parts.Length > 1 ? parts[1] : parts.Length == 1 && !Enum.TryParse<Social.GreetingRules.Kind>(parts[0], true, out _) ? parts[0] : "";
                    if (ModRuntime.ChatOpen) return "refused: a chat is open";
                    var c = who.Length > 0 ? ByName(who) : Nearest(15f);
                    return c == null ? "no NPC" : ModRuntime.ForceGreet(c, kind);
                }
            case "speak":
                {
                    if (ModRuntime.ChatOpen) return "refused: a chat is open";
                    var c = Nearest(10f);
                    if (c == null) return "no NPC nearby";
                    ModRuntime.DevSpeak(c, arg.Length > 0 ? arg : "Evening. Rain again, huh?");
                    return "speaking (Windows voice): " + Describe(c, 0);
                }
            case "diag": return NpcHold.Diagnostics();
            case "diaglast": return NpcHold.LastReleased == null ? "none" : NpcHold.Describe(NpcHold.LastReleased);
            case "holdmode":
                foreach (var kv in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = kv.Split('=');
                    bool v = p.Length > 1 && (p[1] == "1" || p[1] == "true");
                    switch (p[0])
                    {
                        case "skipDoUpdate": NpcHold.SkipDoUpdate = v; break;
                        case "skipSetTask": NpcHold.SkipSetTask = v; break;
                        case "idleParams": NpcHold.IdleParamsInDoUpdate = v; break;
                        case "pin": NpcHold.PinPosition = v; break;
                        case "idle": NpcHold.UseIdleAction = v; break;
                    }
                }
                return $"skipDoUpdate={NpcHold.SkipDoUpdate} skipSetTask={NpcHold.SkipSetTask} idleParams={NpcHold.IdleParamsInDoUpdate} pin={NpcHold.PinPosition}";
            case "continue":
                {
                    var menu = UnityEngine.Object.FindObjectOfType<MainMenuUI>();
                    if (menu == null) return "no main menu";
                    menu.ContinueGameRequest();
                    return "continue requested";
                }
            default: return "unknown command. census [r] | persons | look | open [r] | say <text> | close | mock on|off | shot [name] | state | news | menu | held | anim [name] | greet [WaveOver|Wave|Nod|Snub] [name]";
        }
    }

    private static Character? Nearest(float radius)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return null;
        Vector3 p = pc.transform.position;
        Character? best = null;
        float bestD = radius;
        var it = Character.instances.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            if (c == null || !c.isActiveAndEnabled) continue;
            float d = Vector3.Distance(p, c.transform.position);
            if (d < bestD && NpcTargeting.WhyNot(c, ModRuntime.Config.ChatWithNamedNpcs.Value) == null) { best = c; bestD = d; }
        }
        return best;
    }

    private static Character? ByName(string name)
    {
        var it = Character.instances.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            if (c == null || !c.isActiveAndEnabled) continue;
            string n = "";
            try { n = GameContext.PersonOf(c)?.DisplayedName ?? ""; } catch { }
            if (n.Contains(name, StringComparison.OrdinalIgnoreCase) || (c.gameObject.name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    private static Character? NearestMoving(float radius)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return null;
        Vector3 p = pc.transform.position;
        Character? best = null;
        float bestD = radius;
        var it = Character.instances.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            if (c == null || !c.isActiveAndEnabled) continue;
            float speed = 0;
            try { speed = c.Animator != null ? c.Animator.GetFloat(CharacterMovement.speedId) : 0; } catch { }
            if (speed < 0.2f) continue;
            float d = Vector3.Distance(p, c.transform.position);
            if (d < bestD && NpcTargeting.WhyNot(c, false) == null) { best = c; bestD = d; }
        }
        return best;
    }

    private static string Census(float radius)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return "no player";
        Vector3 p = pc.transform.position;
        var sb = new StringBuilder();
        int total = 0, eligible = 0;
        var rows = new System.Collections.Generic.List<(float d, string s)>();
        var it = Character.instances.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            if (c == null) continue;
            total++;
            float d = Vector3.Distance(p, c.transform.position);
            if (d > radius) continue;
            if (NpcTargeting.WhyNot(c, false) == null) eligible++;
            rows.Add((d, Describe(c, d)));
        }
        sb.AppendLine($"characters: {total} total, {rows.Count} within {radius}m, {eligible} eligible nearby");
        foreach (var r in rows.OrderBy(r => r.d)) sb.AppendLine(r.s);
        return sb.ToString();
    }

    private static string Describe(Character c, float d)
    {
        var person = GameContext.PersonOf(c);
        string why = NpcTargeting.WhyNot(c, false) ?? "ELIGIBLE";
        string story = "", articy = "", gname = "";
        try { if (person != null) { story = person.hasStory ? "story" : ""; articy = person.articyTechnicalName ?? ""; gname = person.DisplayedName ?? ""; } } catch { }
        var v = GameContext.Visual(c);
        string act = "";
        try { act = GameContext.Capture(c).NpcActivity; } catch { }
        return $"  {d,5:0.0}m {c.Type,-15} {(c.isActiveAndEnabled ? "on " : "off")} {why,-32} id={GameContext.NpcId(c)} body={v.Body} demo='{v.Demographic}' game='{gname}' {story} articy='{articy}' act='{act}' go='{c.gameObject.name}'";
    }

    private static string Persons()
    {
        var all = Resources.FindObjectsOfTypeAll<Person>();
        int story = 0, articy = 0, hire = 0;
        var sample = new StringBuilder();
        foreach (var p in all)
        {
            bool s = false; string a = "";
            try { s = p.hasStory; a = p.articyTechnicalName ?? ""; if (p.Hireable) hire++; } catch { }
            if (s) story++;
            if (a.Length > 0) articy++;
            if (sample.Length < 3000 && (s || a.Length > 0)) sample.Append($"{p.name}|{(s ? "story" : "")}|{a}; ");
        }
        return $"persons {all.Length}: hasStory {story}, articyName {articy}, hireable {hire}\n{sample}";
    }

    private static string State()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"chatOpen={ModRuntime.ChatOpen} session={(ModRuntime.Session != null ? ModRuntime.Session.Record.Persona.Name : "-")} hold={NpcHold.Active}");
        sb.AppendLine($"timeScale={Time.timeScale} cursorActive={SafeCursor()} clock={TimeOfDayManager.ClockHour:00}:{TimeOfDayManager.ClockMinute:00} day={TimeOfDayManager.GameplayGameDay}");
        var c = ModRuntime.ChatNpc;
        if (c != null)
        {
            var pc = GameContext.PlayerCharacter();
            sb.AppendLine($"npc pos={c.transform.position} rot={c.transform.eulerAngles} distToPlayer={(pc != null ? Vector3.Distance(pc.transform.position, c.transform.position) : -1):0.00}");
            try
            {
                var a = c.Animator;
                sb.AppendLine($"anim speed={a.GetFloat(CharacterMovement.speedId):0.00} turn={a.GetFloat(CharacterMovement.turnId):0.00} enabled={a.enabled}");
            }
            catch (Exception e) { sb.AppendLine("anim: " + e.Message); }
            try { var n = c.Movement.Agent; sb.AppendLine($"nav enabled={n.enabled} stopped={n.isStopped} vel={n.velocity}"); } catch (Exception e) { sb.AppendLine("nav: " + e.Message); }
        }
        var s = ModRuntime.Session;
        if (s != null)
        {
            var st = s.Record.State;
            sb.AppendLine($"mood={st.Mood}/{st.MoodIntensity} affinity={st.Affinity} relationship={st.Relationship:0.##} met={st.TimesMet} nameRevealed={st.NameRevealed} playerName='{st.PlayerName}' lines={s.Record.History.Count}");
        }
        return sb.ToString();
    }

    private static string SafeCursor()
    {
        try { return CursorModeManager.IsCursorActive.ToString(); } catch (Exception e) { return e.Message; }
    }
}
