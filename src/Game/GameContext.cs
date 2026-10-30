using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Economy;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.Locale;
using Nivalis.Weather;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat.Game;

/// <summary>Reads the live game into plain snapshots. Main thread only.</summary>
internal static class GameContext
{
    private static Dictionary<int, (string name, string desc)>? _locations;

    /// <summary>
    /// The game's manager if it already exists. Never use Singleton&lt;T&gt;.Instance from a mod: it creates the
    /// manager on demand, and a PlayerManager spawned on the title screen breaks level loading.
    /// </summary>
    public static T? Existing<T>() where T : MonoBehaviour
    {
        try
        {
            var i = Singleton<T>._instance;
            return i != null && !i.WasCollected ? i : null;
        }
        catch { return null; }
    }

    public static PlayerManager.Player? LocalPlayer()
    {
        try
        {
            var pm = Existing<PlayerManager>();
            return pm == null ? null : pm.LocalPlayer;
        }
        catch { return null; }
    }

    public static PlayerCharacter? PlayerCharacter()
    {
        try { return LocalPlayer()?.Character; } catch { return null; }
    }

    private static float _youWalk = -1f, _youSprint = -1f, _youSpeedsAt = -100f;
    private static bool _youSpeedsSaid;

    /// <summary>
    /// FB-86: your walking and sprint speeds (m/s) from your own controller (defaultMoveSpeed and sprintSpeed, times its
    /// speed scale when that's ordinary), read again every few seconds, and whether you're sprinting now. -1 when they
    /// can't be read (the follower then uses sensible ones: FollowGait.Speeds). Logged once, a probe of the real numbers.
    /// </summary>
    public static (float walk, float sprint, bool sprinting) PlayerSpeeds()
    {
        bool sprinting = false;
        try
        {
            var ctl = PlayerCharacter()?.Controller;
            if (ctl == null) return (_youWalk, _youSprint, false);
            try { sprinting = ctl._isSprinting; } catch { }
            float now = Time.unscaledTime;
            if (now - _youSpeedsAt >= 3f)
            {
                _youSpeedsAt = now;
                float walk = ctl.defaultMoveSpeed, sprint = ctl.sprintSpeed, scale = 1f, max = -1f;
                try { scale = ctl._moveSpeedScale; } catch { }
                try { max = ctl.MaxSpeed; } catch { }
                float k = scale is >= 0.3f and <= 2f ? scale : 1f;
                _youWalk = walk * k;
                _youSprint = sprint * k;
                if (!_youSpeedsSaid)
                {
                    _youSpeedsSaid = true;
                    Log.Info($"Following (probe): your walk {walk:0.00} m/s, sprint {sprint:0.00} m/s, speed scale {scale:0.00}, max {max:0.00}; " +
                             $"the people walking near you use {TypicalWalkParam():0.00} on the animator.");
                }
            }
        }
        catch (Exception e) { Log.Debug("your speeds: " + e.Message); }
        return (_youWalk, _youSprint, sprinting);
    }

    public static Camera? PlayerCamera()
    {
        try
        {
            var pc = PlayerCharacter();
            var cam = pc != null && pc.Controller != null ? pc.Controller.Camera : null;
            return cam != null ? cam : Camera.main;
        }
        catch { return Camera.main; }
    }

    /// <summary>The in-game hour (0–23), read on its own (a <see cref="Capture"/> reads much more); -1 if it can't be read.</summary>
    public static int Hour()
    {
        try { return TimeOfDayManager.ClockHour; }
        catch { return -1; }
    }

    /// <summary>
    /// Time passing as you talk (MinutesPerMessage): the game's own clock moved on by <paramref name="minutes"/> game minutes
    /// (TimeOfDayManager.AddTime, in game seconds; it works while the chat's pause holds the clock). False if it couldn't be.
    /// </summary>
    public static bool AddGameMinutes(int minutes)
    {
        if (minutes <= 0) return false;
        try
        {
            var tod = Existing<TimeOfDayManager>();
            if (tod == null) return false;
            tod.AddTime(Math.Min(minutes, 60) * 60f);
            return true;
        }
        catch (Exception e) { Log.Debug("clock: " + e.Message); return false; }
    }

    /// <summary>The in-game minute (0–59), read on its own; 0 if it can't be read.</summary>
    public static int Minute()
    {
        try { return TimeOfDayManager.ClockMinute; }
        catch { return 0; }
    }

    public static GameSnapshot Capture(Character? npc)
    {
        var s = new GameSnapshot();
        try
        {
            s.GameDay = Day(); // FB-78: the day turns at midnight
            s.Hour = TimeOfDayManager.ClockHour;
            s.Minute = TimeOfDayManager.ClockMinute;
            s.DayOfWeek = TimeOfDayManager.CurrentDayOfWeek.ToString();
        }
        catch (Exception e) { Log.Debug("time: " + e.Message); }
        try { s.Weather = DescribeWeather(); } catch (Exception e) { Log.Debug("weather: " + e.Message); }
        try
        {
            if (CurrentLocation(npc) is { } loc) { s.Location = loc.name; s.LocationDescription = loc.desc; }
        }
        catch (Exception e) { Log.Debug("location: " + e.Message); }
        try
        {
            var p = LocalPlayer();
            if (p != null)
            {
                // The protagonist is unnamed: the game calls them "You". Treat that as no name.
                string pn = (p.Name ?? "").Trim();
                s.PlayerName = pn.Equals("You", StringComparison.OrdinalIgnoreCase) || pn.Equals("Player", StringComparison.OrdinalIgnoreCase) ? "" : pn;
                s.PlayerRank = p.Rank;
                var venues = new Il2CppSystem.Collections.Generic.List<Venue>();
                p.GetOwnedVenues(venues);
                for (int i = 0; i < venues.Count; i++)
                {
                    string n = VenueName(venues[i]);
                    if (n.Length > 0 && !s.PlayerVenues.Contains(n)) s.PlayerVenues.Add(n);
                }
            }
        }
        catch (Exception e) { Log.Debug("player: " + e.Message); }
        if (npc != null)
        {
            try { s.NpcPlace = VenueAt(npc.transform.position); } catch (Exception e) { Log.Debug("their place: " + e.Message); } // CF-18
            try { s.NpcActivity = DescribeActivity(npc); } catch (Exception e) { Log.Debug("activity: " + e.Message); }
            try { FillWork(npc, s); } catch (Exception e) { Log.Debug("work: " + e.Message); }
            try { s.NpcAtWork = AtWork(npc); } catch { }
            try { s.NpcVisitsToYourPlace = Visits?.Count(NpcId(npc)) ?? 0; } catch { }
        }
        return s;
    }

    /// <summary>How often each person has eaten at the player's places (set by the mod at start).</summary>
    public static Social.VisitBook? Visits;

    /// <summary>Sitting down (a seat, a bench, a table) according to the game.</summary>
    public static bool IsSeated(Character c)
    {
        try { return c.Agent != null && c.Agent.IsSeated; } catch { return false; }
    }

    /// <summary>On the clock right now (serving, cooking, cleaning, taking orders...): residents' chatter leaves them alone.</summary>
    public static bool IsBusyWorking(Character c)
    {
        string a;
        try { a = DescribeActivity(c); } catch { return true; }
        return IsWorkText(a);
    }

    /// <summary>An activity, in words, that's work (<see cref="Social.VenueRoles.IsWorkText"/>, the words this always used).</summary>
    public static bool IsWorkText(string? activity) => Social.VenueRoles.IsWorkText(activity);

    private static float _runParam = -1f, _runParamAt = -100f;
    private static bool _runSaid;

    /// <summary>
    /// FB-62: the animator's speed value for running: the highest of up to 12 NPCs moving fast (over 2.5 m/s), cached for a
    /// minute; until one is seen, 2.2 × the walk's. Logged once (a probe: the game's running value).
    /// </summary>
    public static float TypicalRunParam(float walkParam)
    {
        float now = Time.unscaledTime;
        if (_runParam > 0f && now - _runParamAt < 60f) return _runParam;
        if (_runParam <= 0f && now - _runParamAt < 5f) return walkParam * 2.2f; // the 2026-10-20 audit: nobody running yet, looked again every 5 s
        _runParamAt = now;
        float best = -1f;
        try
        {
            int n = 0;
            foreach (var c in Character.instances)
            {
                if (n >= 12) break; // the 2026-10-20 audit: was continue, which walked everyone
                if (c == null) continue;
                float speed = 0f;
                try { speed = c.Movement != null ? c.Movement.speed : 0f; } catch { }
                if (speed < 2.5f) continue;
                n++;
                try { if (c.Animator != null) best = Mathf.Max(best, c.Animator.GetFloat(CharacterMovement.speedId)); } catch { }
            }
        }
        catch { }
        if (best > walkParam * 1.2f)
        {
            if (!_runSaid) { _runSaid = true; Log.Info($"Following (probe): the game's running value is {best:0.00} (walking {walkParam:0.00})."); }
            _runParam = best;
            return best;
        }
        return _runParam > 0f ? _runParam : walkParam * 2.2f;
    }

    private static float _walkParam = -1f;
    private static float _walkParamAt = -100f;

    /// <summary>
    /// The animator's speed value people walk at, read from whoever is walking nearby (re-checked now and then);
    /// 1 if nobody is walking to learn from.
    /// </summary>
    public static float TypicalWalkParam()
    {
        if (_walkParam > 0 && Time.unscaledTime - _walkParamAt < 60f) return _walkParam;
        var seen = new List<float>();
        try
        {
            var it = Character.instances?.GetEnumerator();
            while (it != null && it.MoveNext() && seen.Count < 12)
            {
                var c = it.Current;
                try
                {
                    if (c == null || !c.isActiveAndEnabled || c.Movement == null || c.Movement.speed < 0.6f || c.Animator == null) continue;
                    float v = c.Animator.GetFloat(CharacterMovement.speedId);
                    if (v > 0.2f && v < 4f) seen.Add(v);
                }
                catch { }
            }
        }
        catch { }
        _walkParamAt = Time.unscaledTime;
        if (seen.Count > 0) { seen.Sort(); _walkParam = seen[seen.Count / 2]; }
        else if (_walkParam <= 0) _walkParam = 1f;
        return _walkParam;
    }

    /// <summary>
    /// Everyone eating at one of the player's places right now gets a visit noted (once per game day each).
    /// Returns how many new visits were counted.
    /// </summary>
    public static int NoteVisitors()
    {
        var book = Visits;
        var player = LocalPlayer();
        if (book == null || player == null) return 0;
        int day;
        day = Day();
        if (day < 0) return 0;
        int counted = 0;
        var all = Character.instances;
        if (all == null) return 0;
        var it = all.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            try
            {
                if (c == null || !c.isActiveAndEnabled || c.Agent == null) continue;
                var visiting = c.Agent.VisitingLocale;
                if (visiting == null || !player.IsOwningVenue(visiting)) continue;
                if (book.Note(NpcId(c), day)) counted++;
            }
            catch { }
        }
        if (counted > 0) book.SaveSoon(); // written on a pool thread: the main thread never waits on the disk (PERF-9)
        return counted;
    }

    /// <summary>Job, employer, customer visit and the game's relationship levels, straight from the simulation.</summary>
    private static void FillWork(Character c, GameSnapshot s)
    {
        var person = PersonOf(c);
        var player = LocalPlayer();
        var rd = person?.RuntimeData;
        Venue? work = null;
        try { work = rd != null ? rd.WorksAt : null; } catch { }
        if (work == null) try { work = person?.WorksAtVenue; } catch { }
        if (work != null)
        {
            s.NpcWorkplace = PropertyName(work);
            try { s.NpcWorksForPlayer = (rd != null && rd.PlayerHired) || (player != null && player.IsOwningVenue(work)); } catch { }
            if (!s.NpcWorksForPlayer)
                try { var owner = work.Owner; if (owner != null) s.NpcEmployer = owner.DisplayedName ?? ""; } catch { }
            try { s.NpcWage = rd != null ? rd.Wage : 0; } catch { }
            if (person != null && s.NpcWorksForPlayer) s.Job = JobOf(person, work); // FB-58
        }
        if (person != null) s.Shop = ShopCached(person); // FB-57
        try
        {
            var tasks = rd != null ? rd.Tasks : default;
            var roles = new List<string>();
            if ((tasks & VenueTasks.Cooking) != 0) roles.Add("cook");
            if ((tasks & VenueTasks.Serving) != 0) roles.Add("server");
            if ((tasks & VenueTasks.Cleaning) != 0) roles.Add("cleaner");
            if ((tasks & VenueTasks.Managing) != 0) roles.Add("manager");
            s.NpcRoles = string.Join(", ", roles);
        }
        catch { }
        try
        {
            var visiting = c.Agent != null ? c.Agent.VisitingLocale : null;
            if (visiting != null)
            {
                s.NpcVisiting = PropertyName(visiting);
                try { s.NpcVisitingPlayerVenue = player != null && player.IsOwningVenue(visiting); } catch { }
            }
        }
        catch { }
        try
        {
            if (rd != null && person != null && IsNamed(person))
            {
                var r = rd.RelationshipVector;
                if (!r.IsZero())
                    s.NpcRelationship = $"friendship {r.Friend}, romance {r.Romance}, business {r.Business}, rivalry {r.Enemy} (each 0 to {RelationshipVector.MaxLevelsCount})";
            }
        }
        catch { }
        // Whether they know the player at all, by the game's own account: met in the story, a relationship, or staff.
        try { if (rd != null && rd.HasBeenMet) s.NpcMetPlayer = true; } catch { }
        if (s.NpcRelationship.Length > 0 || s.NpcWorksForPlayer) s.NpcMetPlayer = true;
    }

    // ------------------------------------------------------------------ shops (FB-57) and your staff's work (FB-58)

    /// <summary>FB-57: the game shows this person's name over their head (its own name tag).</summary>
    public static bool NameOnScreen(Character c)
    {
        if (_nameTagsDistrusted) return false;
        bool shown;
        try { var nd = c.NameDisplay; shown = nd != null && nd.ShouldDisplayName(); } catch { return false; }
        // Review (F1): what the game's name tag depends on isn't known for sure. If it says yes for nearly everyone it
        // isn't telling who the game names, and ordinary residents would all lose their own names: then, for the rest of
        // the session, only shop keepers are known by the game's name.
        try
        {
            if (NameTagSeen.Count < 200 && NameTagSeen.Add(c.Pointer))
            {
                if (shown) _nameTagsYes++;
                if (NameTagSeen.Count >= 12 && _nameTagsYes * 10 >= NameTagSeen.Count * 8)
                {
                    _nameTagsDistrusted = true;
                    Log.Warn($"The game shows a name over {_nameTagsYes} of {NameTagSeen.Count} people: only shop keepers are known by the game's name this session.");
                    return false;
                }
            }
        }
        catch { }
        return shown;
    }

    private static readonly HashSet<IntPtr> NameTagSeen = new();
    private static int _nameTagsYes;
    private static bool _nameTagsDistrusted;

    /// <summary>A shop's name as people say it (an asset-style one made readable).</summary>
    private static string ShopName(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.IndexOf('_') < 0) return s;
        s = Shops.Humanize(s);
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    private static readonly Dictionary<IntPtr, (int stamp, ShopFacts? shop)> ShopsRead = new();

    /// <summary>A shop as <see cref="ShopOf"/> read it within the last 5 game minutes (every snapshot asks: review F3).</summary>
    private static ShopFacts? ShopCached(Person person)
    {
        int minute = 0;
        try { minute = TimeOfDayManager.ClockMinute; } catch { }
        int stamp = Day() * 288 + Math.Max(0, Hour()) * 12 + minute / 5;
        if (ShopsRead.TryGetValue(person.Pointer, out var hit) && hit.stamp == stamp) return hit.shop;
        bool vendor = false;
        try { vendor = person.VendorDefinition != null; } catch { }
        var shop = vendor ? ShopOf(person) : null;
        if (ShopsRead.Count > 200) ShopsRead.Clear();
        ShopsRead[person.Pointer] = (stamp, shop);
        return shop;
    }

    /// <summary>FB-57: the stall or shop this person runs, as the game has it right now: stock, prices, today's deal; null if none.</summary>
    public static ShopFacts? ShopOf(Person person)
    {
        Vendor? shop = null;
        try { shop = person.VendorDefinition; } catch { }
        if (shop == null) return null;
        var f = new ShopFacts();
        try { f.Name = ShopName(shop.VendorName); } catch { }
        try { f.Trade = shop.Type != null ? (shop.Type.Label ?? "").Trim() : ""; } catch { }
        try { f.Regular = shop.RelationshipNormalized; } catch { }
        Nivalis.InventorySystem.ItemType? deal = null;
        try { var dd = shop.dailyDeal; deal = dd != null ? dd.itemType : null; } catch { }
        EconomyManager.TimedStock? shortage = null, plenty = null;
        var eco = Existing<EconomyManager>();
        int day = Day();
        try
        {
            if (eco != null)
            {
                var s = eco.Shortage; if (s != null && (s.tag != null || s.item != null) && day >= s.firstDay && day <= s.lastDay) shortage = s;
                var a = eco.Abundance; if (a != null && (a.tag != null || a.item != null) && day >= a.firstDay && day <= a.lastDay) plenty = a;
            }
        }
        catch { }
        try
        {
            var items = shop.items;
            if (items != null)
            {
                var en = items.GetEnumerator();
                while (en.MoveNext() && f.Items.Count < 40)
                {
                    try
                    {
                        var vi = en.Current.Value;
                        var it = vi != null ? vi.ItemType : null;
                        if (it == null) continue;
                        var item = new ShopItem { Name = Shops.Humanize(it.Name ?? "") };
                        if (item.Name.Length == 0) continue;
                        try { item.Stock = vi!.CurrentStock; item.MaxStock = vi.MaxStock; } catch { }
                        try { item.Price = shop.GetItemBuyCost(it, Nivalis.InventorySystem.FoodFreshness.Fresh); } catch { }
                        try { if (shortage != null && shortage.IsAppliedTo(it)) item.Market = "short"; else if (plenty != null && plenty.IsAppliedTo(it)) item.Market = "plenty"; } catch { }
                        if (deal != null && deal.Pointer == it.Pointer) f.Deal = item.Name;
                        f.Items.Add(item);
                    }
                    catch { }
                }
            }
        }
        catch (Exception e) { Log.Debug("shop stock: " + e.Message); }
        if (Log.Verbose) Log.Debug($"shop: '{f.Name}' ({f.Trade}), {f.Items.Count} items" +
                  (f.Items.Count > 0 ? $" e.g. {string.Join(", ", f.Items.Take(4).Select(i => $"{i.Name} {i.Price} ({i.Stock}/{i.MaxStock})"))}" : "") +
                  $", deal '{f.Deal}'{(deal != null ? $" ({ReadDealBonus(shop)})" : "")}, regular {f.Regular:0.00}");
        return f;
    }

    private static string ReadDealBonus(Vendor shop)
    {
        try { var dd = shop.dailyDeal; return dd != null ? $"bonus {dd.bonus}" : ""; } catch { return ""; }
    }

    /// <summary>FB-58: one of the player's staff, as the game has them right now: its job satisfaction, pay, hours; null if not staff.</summary>
    public static WorkReading? JobOf(Person person, Venue? work)
    {
        RuntimePersonData? rd = null;
        try { rd = person.RuntimeData; } catch { }
        if (rd == null) return null;
        try { if (!rd.PlayerHired) return null; } catch { return null; }
        var r = new WorkReading { Day = Day(), Hour = Hour() };
        if (work == null) try { work = rd.WorksAt; } catch { }
        if (work != null) r.Place = PropertyName(work);
        try { var sat = rd.WorkSatisfaction; r.Happiness = sat.Happiness.ToString(); r.WageInfluence = sat.WageInfluence; r.SkillInfluence = sat.SkillInfluence; } catch { }
        try { r.Wage = rd.Wage; } catch { }
        try { var b = rd.CalculateBaseWage(); r.BaseMin = Math.Min(b.x, b.y); r.BaseMax = Math.Max(b.x, b.y); } catch { }
        try
        {
            var h = rd.WorkingHours;
            float start = h.x, end = h.y;
            if (Math.Max(start, end) <= 1.01f) { start *= 24f; end *= 24f; } // a share of the day, not hours
            if (start >= 0 && start <= 24 && end >= 0 && end <= 24) { r.ShiftStart = start % 24; r.ShiftEnd = end % 24; }
        }
        catch { }
        try { r.PreferredHours = person.PreferredWorkHours; } catch { }
        // CF-14: the player's other staff at the same place (ids only; their names come from the mod's records).
        try
        {
            var read = work != null && work.RuntimeData != null ? work.RuntimeData.Staff : null;
            if (read != null)
            {
                var staff = new Il2CppSystem.Collections.Generic.List<Person>(read.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Person>>());
                for (int i = 0; i < staff.Count; i++)
                {
                    var other = staff[i];
                    if (other == null || other.Pointer == person.Pointer) continue;
                    // Review XR-3: no name from the game (it could be a minor's): NpcBrain names those it has a record for.
                    r.Coworkers.Add((PersonId(other), ""));
                }
                r.CoworkersRead = true;
            }
        }
        catch { }
        try
        {
            var cc = work != null && work.RuntimeData != null ? work.RuntimeData.CurrentCustomers : null;
            if (cc != null) r.Customers = new Il2CppSystem.Collections.Generic.List<AgentGhost>(cc).Count;
        }
        catch { }
        return r;
    }

    /// <summary>FB-58: the player's staff at every place they own, as (person, where they work).</summary>
    public static List<(Person person, Venue venue)> PlayerStaff()
    {
        var result = new List<(Person, Venue)>();
        var p = LocalPlayer();
        if (p == null) return result;
        var venues = new Il2CppSystem.Collections.Generic.List<Venue>();
        try { p.GetOwnedVenues(venues); } catch { return result; }
        for (int v = 0; v < venues.Count; v++)
        {
            try
            {
                var venue = venues[v];
                var read = venue != null && venue.RuntimeData != null ? venue.RuntimeData.Staff : null;
                if (read == null) continue;
                var staff = new Il2CppSystem.Collections.Generic.List<Person>(read.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Person>>());
                for (int i = 0; i < staff.Count; i++)
                {
                    var person = staff[i];
                    if (person != null && !result.Any(x => x.Item1.Pointer == person.Pointer)) result.Add((person, venue!));
                }
            }
            catch (Exception e) { Log.Debug("your staff: " + e.Message); }
        }
        return result;
    }

    // ------------------------------------------------------------------ news, menus, food in hand, money

    /// <summary>Today's two headlines from the game's own news feed (the Holos).</summary>
    public static List<(string title, string text)> ReadNews()
    {
        var list = new List<(string, string)>();
        var nm = Existing<NewsManager>();
        if (nm == null) return list;
        foreach (var a in new[] { nm.NewsA, nm.NewsB })
        {
            try { if (a != null && !string.IsNullOrWhiteSpace(a.Title)) list.Add((a.Title, a.Text ?? "")); } catch { }
        }
        return list;
    }

    /// <summary>The market's current shortage and surplus (abundance), as the game's economy runs them.</summary>
    public static List<Social.MarketTalk.Note> ReadMarket()
    {
        var notes = new List<Social.MarketTalk.Note>();
        var eco = Existing<EconomyManager>();
        if (eco == null) return notes;
        int day;
        try { day = eco.Day; } catch { return notes; }
        void Add(string kind, Func<EconomyManager.TimedStock> read)
        {
            try
            {
                var t = read();
                if (t == null || (t.tag == null && t.item == null)) return;
                if (day < t.firstDay || day > t.lastDay) return;
                string goods = t.DisplayName ?? "";
                if (goods.Trim().Length == 0) goods = t.item != null ? t.item.Name ?? "" : t.tag != null ? t.tag.DisplayName ?? "" : "";
                if (goods.Trim().Length == 0) return;
                float modifier = t.GetPriceModifier(day);
                notes.Add(new Social.MarketTalk.Note
                {
                    Kind = kind, Goods = goods, PricePercent = (int)Math.Round((modifier - 1f) * 100f), SinceDays = Math.Max(0, day - t.firstDay),
                });
            }
            catch (Exception e) { Log.Debug($"market {kind}: {e.Message}"); }
        }
        Add("shortage", () => eco.Shortage);
        Add("surplus", () => eco.Abundance);
        return notes;
    }

    /// <summary>Every place in town that serves food, with its menu and prices (the player's own places included).</summary>
    public static List<Social.FoodScene.Place> ReadFoodPlaces()
    {
        var list = new List<Social.FoodScene.Place>();
        var vm = Existing<VenueManager>();
        if (vm == null) return list;
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Venue>? venues = null;
        try { venues = vm._venues; } catch { }
        if (venues == null) return list;
        var player = LocalPlayer();
        for (int v = 0; v < venues.Length; v++)
        {
            var venue = venues[v];
            if (venue == null) continue;
            try
            {
                var place = new Social.FoodScene.Place { Name = PropertyName(venue) };
                try { place.Location = venue.Location != null ? venue.Location.DisplayName ?? "" : ""; } catch { }
                try { place.Players = player != null && player.IsOwningVenue(venue); } catch { }
                MealMenu? menu = null;
                try { menu = venue.Menu; } catch { }
                int n = 0;
                try { n = menu != null ? menu.Count : 0; } catch { }
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var item = menu![i];
                        string dish = item?.Meal != null ? item.Meal.Name ?? "" : "";
                        if (dish.Length > 0 && item!.Price > 0 && !place.Dishes.Any(d => d.Dish == dish)) place.Dishes.Add((dish, item.Price));
                    }
                    catch { }
                }
                if (place.Name.Length > 0 && place.Dishes.Count > 0) list.Add(place);
            }
            catch (Exception e) { Log.Debug("food place: " + e.Message); }
        }
        return list;
    }

    /// <summary>Dishes and prices on the menus of every venue the player owns.</summary>
    public static List<MenuEntry> PlayerMenu()
    {
        var result = new List<MenuEntry>();
        var p = LocalPlayer();
        if (p == null) return result;
        var venues = new Il2CppSystem.Collections.Generic.List<Venue>();
        try { p.GetOwnedVenues(venues); } catch { return result; }
        for (int v = 0; v < venues.Count; v++)
        {
            var venue = venues[v];
            string name = PropertyName(venue);
            MealMenu? menu = null;
            try { menu = venue.Menu; } catch { }
            if (menu == null) continue;
            int n = 0;
            try { n = menu.Count; } catch { }
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var item = menu[i];
                    string dish = item?.Meal != null ? item.Meal.Name ?? "" : "";
                    if (dish.Length > 0 && !result.Any(r => r.Venue == name && r.Dish == dish))
                        result.Add(new MenuEntry { Venue = name, Dish = dish, Price = item!.Price });
                }
                catch { }
            }
        }
        return result;
    }

    public sealed class HeldMealInfo
    {
        public MealGhost Ghost = null!;
        public MealView View = null!;
        public string Dish = "";
        public int BasePrice;
    }

    /// <summary>
    /// CF-22 (a tester: "I haven't got the "give" feature to work yet"): what's in your hands, for the log: "" for nothing,
    /// else its name and what it is to the mod (a plated dish GIVE can hand over, or not, and why).
    /// </summary>
    public static string HeldDescription()
    {
        try
        {
            var holder = PlayerCharacter()?.ObjectHolder;
            if (holder == null) return "";
            var held = holder.IsHoldingObject ? holder.HeldObject : null;
            if (held == null) return "";
            string name = (held.name ?? "something").Replace("(Clone)", "").Trim();
            var view = held.GetComponent<MealView>() ?? held.GetComponentInChildren<MealView>();
            if (view == null) return $"'{name}' (not a plated dish: no meal on it)";
            var ghost = view.MyGhost;
            if (ghost == null) return $"'{name}' (a meal with no record in the game yet)";
            if (ghost.Type == null) return $"'{name}' (a meal the game hasn't given a dish)";
            return $"'{name}', a plated {ghost.Type.Name} ({(int)Math.Round(ghost.Type.basePrice)} Lims)";
        }
        catch (Exception e) { return "something (couldn't be read: " + e.GetType().Name + ")"; }
    }

    /// <summary>A plated meal the player is carrying (not a tray), with its dish name.</summary>
    public static HeldMealInfo? HeldMeal()
    {
        try
        {
            var holder = PlayerCharacter()?.ObjectHolder;
            var held = holder != null && holder.IsHoldingObject ? holder.HeldObject : null;
            if (held == null) return null;
            var view = held.GetComponent<MealView>() ?? held.GetComponentInChildren<MealView>();
            var ghost = view != null ? view.MyGhost : null;
            var type = ghost?.Type;
            if (view == null || ghost == null || type == null) return null;
            return new HeldMealInfo { Ghost = ghost, View = view, Dish = type.Name ?? "a meal", BasePrice = (int)Math.Round(type.basePrice) };
        }
        catch { return null; }
    }

    /// <summary>
    /// Takes the meal out of the player's hands and removes it from the world: its serving task is dropped (so staff
    /// don't go looking for it) and its ghost deregistered (so it can't respawn from the save).
    /// </summary>
    public static bool ConsumeHeldMeal(HeldMealInfo meal)
    {
        try
        {
            var go = meal.View.gameObject;
            try { meal.View.DeleteTask(); } catch (Exception e) { Log.Debug("meal task: " + e.Message); }
            PlayerCharacter()?.ObjectHolder?.ReleaseObject();
            GhostManager.DeregisterGhost(meal.Ghost);
            // The ghost system returns the view to its pool on its own; if it's still standing there next frame, hide it.
            ModRuntime.Later(0.2f, () =>
            {
                try { if (go != null && go.activeSelf && meal.View.MyGhost != null && meal.View.MyGhost.Pointer == meal.Ghost.Pointer) go.SetActive(false); }
                catch { }
            });
            return true;
        }
        catch (Exception e) { Log.Warn("Handing over the meal failed: " + e.Message); return false; }
    }

    public static bool PayPlayer(int lims)
    {
        try
        {
            var player = LocalPlayer();
            if (player == null) return false;
            // explicit interface member: reach it through the IShopUser wrapper
            var wallet = new IShopUser(player.Pointer).Money;
            if (wallet == null) return false;
            wallet.ChangeMoneyWithoutReceipt(lims);
            return true;
        }
        catch (Exception e) { Log.Warn("Payment failed: " + e.Message); return false; }
    }

    /// <summary>A place's name as the game shows it ("" when there's none).</summary>
    public static string PlaceName(Venue? v) => v == null ? "" : PropertyName(v);

    /// <summary>The game day now (FB-36), without a whole Capture; -1 if it can't be read.</summary>
    public static int Day()
    {
        int gameplay;
        try { gameplay = TimeOfDayManager.GameplayGameDay; } catch { return -1; }
        int day;
        try { day = Days.Read(gameplay, TimeOfDayManager.TotalGameSeconds, TimeOfDayManager.TotalDaySeconds, TimeOfDayManager.ClockHour); }
        catch { return gameplay; }
        if (Days.Learned && !_daySaid) { _daySaid = true; Log.Info($"Clock: days are counted from midnight (the game's day {gameplay} is day {day} here)."); }
        if (Days.Untrusted && !_dayDoubt) { _dayDoubt = true; Log.Info("Clock: the game's total time didn't move like a clock; its own day number is used."); }
        if (Days.Base is long learned && learned != _dayKept) { _dayKept = learned; try { DayLearned?.Invoke(learned); } catch { } }
        return day;
    }

    // FB-78: the game's GameplayGameDay doesn't turn at midnight (the mod's clock went back almost a day after it).
    private static readonly Mind.DayCount Days = new();
    private static bool _daySaid, _dayDoubt;
    private static long _dayKept = long.MinValue;

    /// <summary>What the day count learned, to keep for a session that starts at night (the runtime writes it down).</summary>
    public static Action<long>? DayLearned;

    /// <summary>A line-up kept from before (used at night until this session learns its own).</summary>
    public static void RememberDayCount(long learned) => Days.Remembered = learned;

    /// <summary>A save is being loaded, or a new game started: the day count is learned again from its own clock.</summary>
    public static void ForgetDayCount(bool newGame = false) { Days.Forget(newGame); _dayKept = long.MinValue; }

    /// <summary>The place their visit is to (where they're eating), or null.</summary>
    public static Venue? VisitPlace(Character c)
    {
        Venue? place = null;
        try { place = c.Agent != null ? c.Agent.VisitingLocale : null; } catch { }
        if (place == null) try { place = c.Agent?.MyGhost?.Venue; } catch { }
        return place;
    }

    /// <summary>
    /// Where they work (FB-36): the place, whether it's the player's (hired by them, or one they own), and their role there
    /// ("cook", "server, cleaner"). Null place when they don't work anywhere the game says.
    /// </summary>
    public static (Venue? place, bool players, string role) WorkplaceOf(Character c)
    {
        var person = PersonOf(c);
        var rd = person?.RuntimeData;
        Venue? work = null;
        try { work = rd != null ? rd.WorksAt : null; } catch { }
        if (work == null) try { work = person?.WorksAtVenue; } catch { }
        if (work == null) return (null, false, "");
        bool players = false;
        try { var player = LocalPlayer(); players = (rd != null && rd.PlayerHired) || (player != null && player.IsOwningVenue(work)); } catch { }
        var roles = new List<string>();
        try
        {
            var tasks = rd != null ? rd.Tasks : default;
            if ((tasks & VenueTasks.Cooking) != 0) roles.Add("cook");
            if ((tasks & VenueTasks.Serving) != 0) roles.Add("server");
            if ((tasks & VenueTasks.Cleaning) != 0) roles.Add("cleaner");
            if ((tasks & VenueTasks.Managing) != 0) roles.Add("manager");
        }
        catch { }
        return (work, players, string.Join(", ", roles));
    }

    /// <summary>One of the player's own places.</summary>
    public static bool PlayersPlace(Venue? v)
    {
        try { var p = LocalPlayer(); return v != null && p != null && p.IsOwningVenue(v); } catch { return false; }
    }

    /// <summary>The game's task for them, by its name ("AIAction_TakeOrder", "AIAction_VisitVenue"...); "" when unknown.</summary>
    public static string GhostTask(Character c)
    {
        try { var t = c.Agent?.MyGhost?.CurrentAction?.Type; return t != null ? (t.name ?? "").Replace("(Clone)", "").Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>The game's own id for them (their ghost); "" when unknown.</summary>
    public static string GhostId(Character c)
    {
        try { return c.Agent?.MyGhost?.Id ?? ""; } catch { return ""; }
    }

    /// <summary>Your own customer ghost's id (PlayerGhost is a CustomerGhost: a waiter's order can be yours); "" when unread.</summary>
    public static string PlayerGhostId(PlayerCharacter? pc)
    {
        try { return pc?.MyGhost?.Id ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// The customers (ghost ids) a worker's current task is for (review H2): the one they're taking an order from, the ones
    /// whose meals they're carrying. Empty when it can't be read, or for any other task.
    /// </summary>
    public static HashSet<string> WaiterTargets(Character c)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var st = c.Agent?.MyGhost?.CurrentActionState;
            if (st == null) return ids;
            var take = st.TryCast<TakeOrderAgentActionType.State>();
            if (take != null)
            {
                try { if (take.Task?.Customer?.Id is { Length: > 0 } cid) ids.Add(cid); } catch { } // the order task's own customer
                if (take.Target?.ReferencedId is { Length: > 0 } t) ids.Add(t);
            }
            var serve = st.TryCast<ServeMealAgentActionType.State>();
            if (serve != null)
            {
                var tasks = serve.Tasks;
                if (tasks != null)
                    for (int i = 0; i < tasks.Count; i++)
                        if (tasks[i]?.Customer?.Id is { Length: > 0 } cid) ids.Add(cid);
                if (serve.Target?.ReferencedId is { Length: > 0 } st2) ids.Add(st2);
            }
        }
        catch { }
        return ids;
    }

    /// <summary>
    /// A customer's step at a food place ("WaitingToOrder", "WaitingForOrder", "Eating", "Leaving"...), and which table
    /// they're at (the game's table id); both "" when they aren't visiting one or it can't be read (FB-18 review H1/M5).
    /// </summary>
    public static (string step, string table) VisitOf(Character c)
    {
        try
        {
            var g = c.Agent?.MyGhost;
            var type = g?.CurrentAction?.Type;
            if (g == null || type == null || type.TryCast<VisitVenueAgentActionType>() == null) return ("", "");
            var vs = g.VisitVenueState;
            if (vs == null) return ("", "");
            string table = "";
            try { table = vs.Table?.ReferencedId ?? ""; } catch { }
            return (vs.Step.ToString(), table);
        }
        catch { return ("", ""); }
    }

    /// <summary>
    /// FB-42: whether the game has this customer's visit to a food place as one to eat at (false: only to sit a while); null
    /// when they aren't visiting one or it can't be read. Only read.
    /// </summary>
    public static bool? WantsToEat(Character c)
    {
        try
        {
            var g = c.Agent?.MyGhost;
            var type = g?.CurrentAction?.Type;
            if (g == null || type == null || type.TryCast<VisitVenueAgentActionType>() == null) return null;
            var vs = g.VisitVenueState;
            return vs == null ? null : vs.Eat;
        }
        catch { return null; }
    }

    /// <summary>
    /// FB-42: a member of staff taking this customer's order goes on to their next task, through the game's own interruption
    /// of their current action (nothing of the customer's visit is touched). Only when that action is taking an order and
    /// it's for this customer. True when it was ended.
    /// </summary>
    public static bool EndTakeOrder(Character waiter, Character customer)
    {
        try
        {
            var g = waiter.Agent?.MyGhost;
            var action = g?.CurrentAction;
            if (g == null || action?.Type == null || action.Type.TryCast<TakeOrderAgentActionType>() == null) return false;
            string cid = GhostId(customer);
            if (cid.Length == 0 || !WaiterTargets(waiter).Contains(cid)) return false;
            // FB-51: the customer's take-order task cancelled in the place's own queue first (the game's own way), or the
            // interrupted task goes back in the queue and a waiter comes straight back to them (every 2-12 s in game).
            CancelOrderTasks(waiter, customer);
            action.Interrupt(g);
            return true;
        }
        catch (Exception e) { Log.Debug("ending an order task: " + e.Message); return false; }
    }

    /// <summary>
    /// FB-51: the place's queued "take this customer's order" tasks cancelled through the game's own
    /// StaffTaskQueue.CancelTakeOrderTasks (the customer's visit itself is left as it is). False when it couldn't be reached.
    /// </summary>
    private static bool _saidCancel;

    public static bool CancelOrderTasks(Character waiter, Character customer)
    {
        try
        {
            var take = waiter.Agent?.MyGhost?.CurrentActionState?.TryCast<TakeOrderAgentActionType.State>();
            var queue = take?.Venue?.CurrentTaskQueue;
            var ghost = customer.Agent?.MyGhost?.TryCast<CustomerGhost>();
            if (queue == null || ghost == null) return false;
            var staff = new Il2CppSystem.Collections.Generic.List<Person>();
            if (PersonOf(waiter) is { } p) staff.Add(p);
            queue.CancelTakeOrderTasks(ghost, staff.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<Person>>());
            if (!_saidCancel) { _saidCancel = true; Log.Info($"venue talk: {customer.name}'s order task cancelled in the place's queue (the game's own way; said once)"); }
            return true;
        }
        catch (Exception e) { Log.Warn("Cancelling a customer's order task failed (the staff may keep coming back): " + e.Message); return false; }
    }

    /// <summary>
    /// CF-11: on shift at their job right now (the game's own working hours for them), whatever their task this moment:
    /// staff between two jobs (the game's ghost walking or idling) are still at work.
    /// </summary>
    public static bool OnShift(Character c)
    {
        try
        {
            var rd = PersonOf(c)?.RuntimeData;
            if (rd == null || rd.WorksAt == null) return false;
            var h = rd.WorkingHours;
            float start = h.x, end = h.y;
            if (Math.Max(start, end) <= 1.01f) { start *= 24f; end *= 24f; } // a share of the day, not hours (as JobOf reads it)
            if (start < 0 || start > 24 || end < 0 || end > 24) return false;
            int hour = Hour();
            return hour >= 0 && Npc.WorkLife.InShift(start % 24, end % 24, hour);
        }
        catch { return false; }
    }

    /// <summary>
    /// CF-11: at work in any sense the mod can see: a staff task from the game, an activity that's work ("taking a customer's
    /// order", "minding the counter"...), or their shift. The game's own interaction with them (an order, a shop) is theirs then.
    /// </summary>
    public static bool AtWork(Character c)
    {
        try
        {
            if (IsWorkerTask(c)) return true;
            // Review CF11-1: their activity says work (at their place); on shift alone could be lunch or the commute. A
            // description that can't be read is "not at work" here (IsBusyWorking's own answer for that is "busy").
            string a;
            try { a = DescribeActivity(c); } catch { return false; }
            return IsWorkText(a);
        }
        catch { return false; }
    }

    /// <summary>
    /// CF-26: why the game has this person in its own service right now ("" = it doesn't): at work (a staff task, work to do,
    /// their shift, staff you hired) or on a visit to a place (a step of it, or a place they're visiting). Their orders and
    /// visits wait on the game's own conversations, so those stay the game's. "can't be read" when it can't (when unsure, the
    /// game's way stands).
    /// </summary>
    public static string ServiceWhy(Character c)
    {
        try
        {
            if (c == null || c.Agent == null) return "";
            if (IsWorkerTask(c) || AtWork(c)) return "at work";
            if (OnShift(c)) return "on shift";
            try { if (PersonOf(c)?.RuntimeData?.PlayerHired == true) return "your staff"; } catch { }
            string step = VisitOf(c).step;
            if (step.Length > 0) return "a customer (" + step + ")";
            var place = c.Agent.VisitingLocale;
            if (place != null) return "visiting " + PlaceName(place);
            return "";
        }
        catch { return "can't be read"; }
    }

    /// <summary>The game's task for them is a worker's (taking orders, serving, cooking, cleaning, idling at work): FB-21.</summary>
    public static bool IsWorkerTask(Character c)
    {
        try
        {
            var t = c.Agent?.MyGhost?.CurrentAction?.Type;
            return t != null && t.TryCast<Nivalis.GhostSystem.Ai.VenueWorkerAgentActionType>() != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// The inner-life round (review game IL-11): a ghost the game is running a food-place visit or a shift for (by its action's
    /// type, as <see cref="VisitOf"/> and <see cref="IsWorkerTask"/> read it, not by name): the mod leaves it to the game.
    /// True when it can't be read (when unsure, leave it alone).
    /// </summary>
    public static bool GhostBusy(AgentGhost? g)
    {
        try
        {
            if (g == null) return true;
            var t = g.CurrentAction?.Type;
            if (t == null) return false;
            return t.TryCast<VisitVenueAgentActionType>() != null || t.TryCast<VenueWorkerAgentActionType>() != null || g.VisitVenueState != null;
        }
        catch { return true; }
    }

    /// <summary>What they're doing, by the game's own name for it ("TalkOrder", "Eat", "IdleStaff"...); "" when unknown.</summary>
    public static string ActionName(Character c)
    {
        try { var cur = c.state != null ? c.state.current : null; return cur != null ? (cur.name ?? "").Replace("(Clone)", "").Trim() : ""; }
        catch { return ""; }
    }

    private static string PropertyName(Venue v)
    {
        try { var n = v.GetName(); if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        return VenueName(v);
    }

    // CF-20: the venues' areas in the loaded scenes, looked up again on a scene change or every half minute.
    private static List<VenueArea>? _venueAreas;
    private static int _venueAreasScene = int.MinValue;
    private static float _venueAreasAt = -100f;

    /// <summary>The game's venue areas in the loaded scenes (cached; never one from a prefab).</summary>
    internal static List<VenueArea> VenueAreas()
    {
        int epoch = NpcHold.SceneEpochNow();
        if (_venueAreas != null && epoch == _venueAreasScene && Time.unscaledTime - _venueAreasAt < 30f) return _venueAreas;
        var list = new List<VenueArea>();
        try
        {
            foreach (var a in UnityEngine.Object.FindObjectsOfType<VenueArea>()) // review R6: the scenes' only, not every asset
                if (a != null && a.gameObject.scene.IsValid() && a.gameObject.activeInHierarchy) list.Add(a);
        }
        catch (Exception e) { Log.Debug("venue areas: " + e.Message); }
        _venueAreas = list; _venueAreasScene = epoch; _venueAreasAt = Time.unscaledTime;
        return list;
    }

    /// <summary>A venue area's place, by the venue's own name ("" when it has none).</summary>
    internal static string AreaName(VenueArea a)
    {
        try { return PlaceName(a.MyGhost?.Property?.TryCast<Venue>()); } catch { return ""; }
    }

    /// <summary>
    /// CF-20 (a player: "meet me at the noodle bar" never happened): every name for where you are: the district, and the venue
    /// you're in or at the door of (the nearest venue area within <paramref name="within"/> m, by the game's own distance).
    /// </summary>
    public static List<string> PlacesHere(float within = 6f)
    {
        var names = new List<string>();
        try { if (CurrentLocation(null) is { } loc && loc.name.Length > 0) names.Add(loc.name); } catch { } // review R6: not a whole snapshot
        try
        {
            var pc = PlayerCharacter();
            if (pc == null) return names;
            string best = VenueAt(pc.transform.position, within);
            if (best.Length > 0 && !names.Contains(best)) names.Add(best);
        }
        catch (Exception e) { Log.Debug("places here: " + e.Message); }
        return names;
    }

    /// <summary>The venue whose area is nearest this spot within <paramref name="within"/> m (the game's own distance), by name; "" none.</summary>
    public static string VenueAt(Vector3 pos, float within = 6f)
    {
        string best = "";
        float bestD = within * within;
        foreach (var a in VenueAreas())
        {
            if (a == null || a.WasCollected) continue;
            float d2;
            try { d2 = a.GetDistanceToSqr(pos); } catch { continue; }
            if (d2 > bestD) continue;
            string n = AreaName(a);
            if (n.Length == 0) continue;
            best = n; bestD = d2;
        }
        return best;
    }

    public static string VenueName(Venue? v)
    {
        if (v == null) return "";
        try { var n = v.EntryName; if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        try { return v.name ?? ""; } catch { return ""; }
    }

    private static (string name, string desc)? LocationFor(int sceneIndex)
    {
        if (sceneIndex < 0) return null;
        if (_locations == null)
        {
            _locations = new Dictionary<int, (string, string)>();
            foreach (var wl in Resources.FindObjectsOfTypeAll<WorldLocation>())
            {
                try
                {
                    int idx = wl.SceneIndex;
                    string name = wl.DisplayName ?? "";
                    if (idx >= 0 && name.Length > 0 && !_locations.ContainsKey(idx))
                        _locations[idx] = (name, wl.DisplayDescription ?? "");
                }
                catch { }
            }
            Log.Debug($"Known locations: {_locations.Count}");
        }
        return _locations.TryGetValue(sceneIndex, out var l) ? l : null;
    }

    public static string LocationName(int sceneIndex) => LocationFor(sceneIndex)?.name ?? "";

    /// <summary>The district the player is in. The player object lives in a persistent scene, so try the NPC's scene and the loaded scenes.</summary>
    private static (string name, string desc)? CurrentLocation(Character? npc)
    {
        var tried = new List<int>();
        if (npc != null) tried.Add(npc.gameObject.scene.buildIndex);
        tried.Add(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            tried.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).buildIndex);
        try { var pc = PlayerCharacter(); if (pc != null) tried.Add(pc.gameObject.scene.buildIndex); } catch { }
        foreach (int idx in tried)
            if (LocationFor(idx) is { } loc) return loc;
        Log.Debug("No location for scenes " + string.Join(",", tried));
        return null;
    }

    /// <summary>The phone's signal: the weather as numbers (blizzard, snow, rain, thunder, fog; 0 to 1), zeros when it can't be read.</summary>
    public static (float blizzard, float snow, float rain, float thunder, float fog) WeatherNumbers()
    {
        try
        {
            WeatherManager? w = null;
            try { w = WeatherManager.__instance_k__BackingField; } catch { }
            if (w == null) return default;
            return (w.globalBlizzard, w.globalSnow, w.globalRainAmount, w.globalThunder, w.globalFogAmount);
        }
        catch { return default; }
    }

    private static string DescribeWeather()
    {
        WeatherManager? w = null;
        try { w = WeatherManager.__instance_k__BackingField; } catch { }
        if (w == null) w = UnityEngine.Object.FindObjectOfType<WeatherManager>();
        if (w == null) return "";
        var parts = new List<string>();
        if (w.globalBlizzard > 0.3f) parts.Add("a blizzard");
        else if (w.globalSnow > 0.5f) parts.Add("heavy snow");
        else if (w.globalSnow > 0.1f) parts.Add("light snow");
        if (w.globalRainAmount > 0.6f) parts.Add("pouring rain");
        else if (w.globalRainAmount > 0.25f) parts.Add("steady rain");
        else if (w.globalRainAmount > 0.05f) parts.Add("drizzle");
        if (w.globalThunder > 0.3f) parts.Add("thunder");
        if (w.globalFogAmount > 0.5f) parts.Add("thick fog");
        if (w.globalDustAmount > 0.5f) parts.Add("smog");
        if (w.globalWindIntensity > 0.7f) parts.Add("strong wind");
        if (parts.Count == 0) parts.Add(w.globalWetness > 0.4f ? "dry for now, streets still wet" : "dry and overcast");
        return string.Join(", ", parts);
    }

    private static string DescribeActivity(Character c)
    {
        string ghost = "", action = "", anim = "";
        try { var t = c.Agent?.MyGhost?.CurrentAction?.Type; if (t != null) ghost = t.name ?? ""; } catch { }
        try { var cur = c.state?.current; if (cur != null) { action = cur.name ?? ""; var ia = cur.TryCast<IdleAction>(); if (ia != null) anim = ia.Animation.ToString(); } } catch { }
        Log.Debug($"activity: ghost='{ghost}' action='{action}' anim='{anim}'");

        string byAnim = anim switch
        {
            "CleanTable" => "wiping down a table",
            "CleanBar" => "wiping down the bar",
            "CleanGlasses" => "polishing glasses",
            "CutFood" => "chopping ingredients",
            "Cooker" or "Pan" or "Fryer" or "Blender" => "cooking",
            "Sweep" => "sweeping up",
            "Repair" => "fixing something",
            "Gardening" => "tending plants",
            "Fishing" => "fishing",
            "Phone" => "fiddling with their phone",
            "Smoke" => "having a smoke",
            "Eat" => "eating",
            "SitBench" or "SitChair" or "SitHighChair" or "SitGround" or "SitPier" => "sitting down",
            "Lean" or "Wall" => "leaning against the wall",
            "Arms" => "standing with their arms crossed",
            "Shop" => "minding the counter",
            "Security" => "keeping watch",
            "Dance" => "dancing",
            "Beg" => "asking passers-by for spare Lims",
            "Mirror" => "checking themselves in a mirror",
            "Delivery" => "carrying a delivery",
            "Bar" => "working behind the bar",
            "Waiter" => "waiting tables",
            "Sleep" or "Sunbed" => "resting",
            "Umbrella" => "standing under an umbrella",
            "LookDown" => "looking down at something",
            _ => "",
        };
        string g = ghost.ToLowerInvariant();
        string byGhost =
            g.Contains("serve") ? "bringing an order out to a customer" :
            g.Contains("takeorder") ? "taking a customer's order" :
            g.Contains("makemeal") ? "cooking an order" :
            g.Contains("process") ? "prepping ingredients" :
            g.Contains("deliver") ? "hauling ingredients" :
            g.Contains("complaint") ? "dealing with a customer's complaint" :
            g.Contains("manage") ? "keeping the place running" :
            g.Contains("cleanupmeal") ? "clearing away dirty dishes" :
            g.Contains("cleanuptrash") ? "taking out the trash" :
            g.Contains("cleanupstorage") ? "tidying the storeroom" :
            g.Contains("clean") || g.Contains("wash") ? "cleaning up" :
            g.Contains("idleatwork") || g.Contains("workplaceidle") ? "on shift, between jobs" : // the game's AIAction_WorkplaceIdle (audit A1)
            g.Contains("visitvenue") ? "out to get something to eat" :
            g.Contains("vending") ? "on the way to a vending machine" :
            g.Contains("wander") ? "wandering the streets" :
            g.Contains("sleep") ? "heading home to sleep" :
            g.Contains("dialogue") ? "waiting around for someone" :
            g.Contains("idle") ? "killing time" : "";

        if (byAnim.Length > 0 && byGhost.Length > 0 && byAnim != byGhost) return $"{byAnim} ({byGhost})";
        if (byAnim.Length > 0) return byAnim;
        if (byGhost.Length > 0)
        {
            bool walking = action.Contains("Move", StringComparison.OrdinalIgnoreCase) || action.Contains("Walk", StringComparison.OrdinalIgnoreCase);
            return walking && !byGhost.Contains("wander") ? byGhost + ", on the move" : byGhost;
        }
        try { if (c.Agent != null && c.Agent.IsSeated) return "sitting down"; } catch { }
        try { if (c.Communication != null && c.Communication.IsConversation) return "chatting with someone nearby"; } catch { }
        float speed = 0;
        try { speed = c.Movement != null ? c.Movement.speed : 0; } catch { }
        return speed > 0.2f ? "walking somewhere" : "standing around";
    }

    // ------------------------------------------------------------------ NPC identity & looks

    public static string NpcId(Character c)
    {
        var person = PersonOf(c);
        if (person != null) return PersonId(person);
        // Background extras are pooled; the same model and outfit is treated as the same person in a district.
        string prefab = (c.gameObject.name ?? "extra").Replace("(Clone)", "").Trim();
        int scene = c.gameObject.scene.buildIndex;
        return $"extra_{scene}_{prefab}";
    }

    /// <summary>The mod's id for a person (their game guid): the same whether found by their character or in a story scene (FB-50).</summary>
    public static string PersonId(Person person)
    {
        string guid = "";
        try { guid = person.Guid; } catch { }
        if (!string.IsNullOrEmpty(guid)) return "person_" + guid;
        string name = "";
        try { name = person.name ?? ""; } catch { }
        return "person_" + (name.Length > 0 ? name : "unnamed");
    }

    public static Person? PersonOf(Character c)
    {
        try
        {
            var agent = c.Agent;
            if (agent == null) return null;
            var p = agent.Person;
            if (p == null) p = agent.initialPerson;
            return p;
        }
        catch { return null; }
    }

    /// <summary>A person with their own story (written dialogue, articy entity) rather than a generated passer-by.</summary>
    public static bool IsNamed(Person p)
    {
        try { if (p.hasStory) return true; } catch { }
        try { return !string.IsNullOrWhiteSpace(p.articyTechnicalName); } catch { return false; }
    }

    public static NpcVisual Visual(Character c)
    {
        var v = new NpcVisual();
        bool bot = false, female = false;
        try { bot = c.Bot; } catch { }
        try { female = c.Female; } catch { }
        v.IsAndroid = bot;
        v.Body = bot ? "android" : female ? "woman" : "man";
        try { v.DressedForCold = c.Cold; } catch { }
        try { v.Demographic = c.DemographicGroup != null ? c.DemographicGroup.Name ?? "" : ""; } catch { }
        var p = PersonOf(c);
        v.Kind = p != null ? "simulated" : "background extra";
        if (p != null)
        {
            try { v.HomeDistrict = LocationName(p.HomeSceneIndex); } catch { }
            try { v.Workplace = VenueName(p.WorksAtVenue); } catch { }
            try { v.GameName = p.DisplayedName ?? ""; } catch { }
            // FB-57: a shop keeper (the game's own stall), and anyone whose name the game shows over their head: known by it.
            try
            {
                var shop = p.VendorDefinition;
                if (shop != null)
                {
                    try { v.ShopName = ShopName(shop.VendorName); } catch { }
                    try { v.Trade = shop.Type != null ? (shop.Type.Label ?? "").Trim() : ""; } catch { }
                }
            }
            catch { }
            v.NameShown = v.GameName.Trim().Length > 0 && (v.ShopName.Length > 0 || v.Trade.Length > 0 || NameOnScreen(c));
            try { v.Employee = p.RuntimeData != null && p.RuntimeData.PlayerHired; } catch { }
            // Story characters and the player's own staff are known by their game name (it is on screen in the game's menus).
            v.Named = (IsNamed(p) || v.Employee) && v.GameName.Length > 0;
            if (v.Named)
                try { v.Description = (p.Description ?? "").Trim(); } catch { }
            if (v.Demographic.Length == 0)
                try { v.Demographic = p.DemographicGroup != null ? p.DemographicGroup.Name ?? "" : ""; } catch { }
            try { if (Minors.StoryChildGuids.Contains(p.Guid ?? "")) v.Minor = true; } catch { }
        }
        v.Minor |= IsChild(c, bot, v.Description);
        v.Model = ModelName(c, p);
        return v;
    }

    /// <summary>
    /// Which character model this is, for <see cref="Looks"/>: the portrait the game shows for them (one per model),
    /// else the model's own name. "" if none can be read.
    /// </summary>
    private static string ModelName(Character c, Person? p)
    {
        string Clean(string? n) => (n ?? "").Replace("(Clone)", "").Trim();
        string name = "";
        try { var s = c.Portrait; if (s != null) name = Clean(s.name); } catch { }
        if (name.Length == 0 && p != null)
            try { var s = p.Portrait; if (s != null) name = Clean(s.name); } catch { }
        if (name.Length == 0)
            try { string own = Clean(c.gameObject.name); if (Looks.Find(own) != null) name = own; } catch { }
        if (name.Length == 0 && p != null)
            try { var a = p.CharacterPrefab; if (a != null) name = Clean(a.gameObject.name); } catch { }
        return name;
    }

    /// <summary>
    /// A child, by what the game shows: a child-sized body (the game's children are 0.75-0.82 of normal height; every
    /// adult model is 0.87 or more; robots don't count) or a record that describes a child.
    /// </summary>
    private static bool IsChild(Character c, bool bot, string description)
    {
        if (CanonAge.FromDescription(description)?.Child == true) return true;
        if (bot) return false;
        try
        {
            var a = c.Animator;
            if (a != null && a.isHuman && a.humanScale > 0.2f && a.humanScale < 0.84f) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Where their head is right now: the head marker, if it's really over their body (within about a metre to the side,
    /// 0.2 to 2.6 m up: standing, sitting or lying); otherwise their body at head height. The game moves or switches off
    /// parts of a body (out of view, mid-move), and a marker left elsewhere put voices and bubbles in the wrong place.
    /// </summary>
    public static Vector3 HeadPoint(Transform? head, Transform? body)
    {
        try
        {
            if (body == null || body.WasCollected) return head != null && !head.WasCollected ? head.position : Vector3.zero;
            var b = body.position;
            if (head != null && !head.WasCollected && head.Pointer != body.Pointer)
            {
                var h = head.position;
                var off = h - b;
                if (off.x * off.x + off.z * off.z <= 1.44f && off.y >= 0.2f && off.y <= 2.6f) return h;
            }
            return b + Vector3.up * 1.62f;
        }
        catch { return Vector3.zero; }
    }

    public static Transform HeadOf(Character c)
    {
        try
        {
            var head = c.Communication != null ? c.Communication.Head : null;
            if (head != null) return head;
        }
        catch { }
        try
        {
            var anim = c.Animator;
            if (anim != null && anim.isHuman)
            {
                var h = anim.GetBoneTransform(HumanBodyBones.Head);
                if (h != null) return h;
            }
        }
        catch { }
        return c.transform;
    }
}
