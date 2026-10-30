using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Phone;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisAIChat;

/// <summary>
/// The phone (the author's request, 2026-10-19): PhoneKey brings it out (never during a chat, the console or the settings; no chat
/// starts while it's out). People give you their number in a chat when they want to (the AI decides: gaveNumber), and you
/// text them; they answer in their own voice with their memory of you ("text", yours in the Usage Optimizer), and friends
/// and partners text you first now and then ("text first": the AI-greetings lever and the pacer). The signal comes from
/// where you are and the weather; with no bars a text waits. Contacts and texts are kept per save (phone.json); the
/// wallpaper is kept on the PC (phone-wallpaper). Never anyone under 18.
/// </summary>
public static partial class ModRuntime
{
    private static readonly PhoneWindow PhoneUi = new();
    private static PhoneBook? _phone;
    private static bool _phoneOpen;
    private static int _phoneOpenedFrame;
    private static Key _phoneKey = Key.N;
    /// <summary>
    /// The wallpaper's downloads: redirects only by Wallpaper.DownloadAsync's own checked loop (review PH-7b), and every
    /// connection checked after the name is looked up, so a name that leads to this PC or the LAN is refused (PH-7c).
    /// </summary>
    private static readonly HttpClient WallHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var ip = addresses.FirstOrDefault(a => !Wallpaper.LocalAddress(a)) ?? throw new HttpRequestException("that link leads to this PC or the local network");
            if (addresses.Any(Wallpaper.LocalAddress)) throw new HttpRequestException("that link leads to this PC or the local network");
            var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(ip, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(40) };

    internal static bool PhoneOpen => _phoneOpen;

    // ------------------------------------------------------------------ the book's lifecycle (per save, like minds.json)

    private static void OpenPhoneBook(string dataDir)
    {
        try { _phone = new PhoneBook(Path.Combine(dataDir, "phone.json"), Log.Warn) { IsMinor = KnownUnder18 }; }
        catch (Exception e) { Log.Warn("The phone's contacts and texts are unavailable: " + e.Message); _phone = null; }
        ContactInfo.Clear();
        ContactTriedAt.Clear(); // PH-R1
        _avaId = null;
        _avaPortraitTried = false;
        PhonePending.Clear();
        PhoneNoAnswer.Clear();
        SendingUntil.Clear();
        DeliverAt.Clear();
        LeftOnRead.Clear();
        // Texts left sending when the game closed go on their way again.
        try { if (_phone != null) foreach (var id in _phone.StillSending()) SendingUntil[id] = 0f; } catch { }
        OpenLineBook(dataDir); // FB-71: Nightline, per save beside the phone
        if (_phoneOpen) MainThread.Enqueue(RefreshPhone);
    }

    private static void ClosePhoneBook()
    {
        foreach (var p in PhonePending.Values) try { p.Cts.Cancel(); } catch { }
        DeliverInboxQuietly(); // FB-79
        try { _phone?.Close(); } catch (Exception e) { Log.Warn("Saving the phone: " + e.Message); }
        CloseLineBook();
    }

    private static void FlushPhoneBook() { try { _phone?.Flush(); } catch { } FlushLineBook(); }

    // ------------------------------------------------------------------ the key, opening and closing

    private static void ParsePhoneKey()
    {
        string k = (_cfg.PhoneKey.Value ?? "N").Trim();
        if (k.Length == 1 && char.IsDigit(k[0])) k = "Digit" + k;
        _phoneKey = Enum.TryParse<Key>(k, true, out var parsed) && parsed != Key.None && Enum.IsDefined(parsed) ? parsed : Key.N;
        if (_phoneKey == _talkKey || _phoneKey == _consoleKey)
        {
            Log.Warn($"PhoneKey ({_cfg.PhoneKey.Value}) is the talk or console key; using N.");
            _phoneKey = Key.N;
        }
    }

    private static string PhoneKeyName => _phoneKey.ToString().Replace("Digit", "");

    private static bool PhoneKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && Features.Phone && Pressed(kb, ref _phoneKey, Key.N, "PhoneKey");
    }

    private static float _nextUnreadLook;

    /// <summary>Every frame: the phone's window (its cards show while it's away), built once the chat window is.</summary>
    private static void TickPhoneWindow()
    {
        if (!PhoneUi.Built)
        {
            if (!Ui.Built) return;
            PhoneUi.Build(Ui.Fonts);
            WirePhone();
            LoadWallpaperSoon();
        }
        PhoneUi.HoldToasts = ChatOpen || _consoleOpen || _menuOpen;
        PhoneUi.NotifyStyle = _cfg.PhoneNotify.Value is "Card" or "Off" ? _cfg.PhoneNotify.Value : "Pip"; // CF-20
        PhoneUi.NotifyVolume = Math.Clamp(_cfg.PhoneNotifyVolume.Value, 0, 100) / 100f;
        if (Time.unscaledTime >= _nextUnreadLook) { _nextUnreadLook = Time.unscaledTime + 0.5f; PhoneUi.Unread = Features.Phone && _phone != null ? _phone.UnreadTotal() : 0; }
        // Phone pass PH-A7: the window's own frame can't stop the texts (a throw there is logged; the rest still runs).
        try { PhoneUi.Tick(Time.unscaledDeltaTime); } catch (Exception e) { Log.Debug("phone window tick: " + e.Message); }
        if (!Features.Phone) return; // PH-A8: switched off, no signal readings or "Signal's back" either
        TickSignal();
        TickPhoneClock(); // FB-83
        TickTexts();
        TickTextFirst();
        TickNightline(); // FB-71
    }

    /// <summary>In a chat: the phone key (outside the text box) only says it can't come out now.</summary>
    private static void PhoneKeyInChat()
    {
        if (!Features.Phone || Ui.InputFocused || _phoneSaidInChat == _openSerial || !PhoneKeyPressed()) return;
        _phoneSaidInChat = _openSerial;
        Ui.AddLine(LineKind.Notice, "", "Finish the chat first to use your phone.");
    }

    private static int _phoneSaidInChat = -1;

    private static void OpenPhone()
    {
        if (_phoneOpen || ChatOpen || _consoleOpen || _menuOpen || !PhoneUi.Built || _phone == null || !Features.Phone) return;
        _phoneOpen = true;
        _phoneOpenedFrame = Time.frameCount;
        Ui.HideBark();
        Ui.ShowPrompt(null);
        Locks.Engage(false); // you stand still and the cursor is yours; the clock keeps going (it's a phone, not a pause)
        PhoneUi.UserScale = _cfg.PhoneScale.Value;
        PhoneUi.View.FirstTime = _phone.FirstLook();
        EnsureAva();
        LoadContactInfo();
        PhoneUi.Show();
        // Phone pass PH-A1: it opens where it was put away; a conversation on screen is read (it came in while it was away).
        if (PhoneUi.ScreenName == "thread") _phone.MarkRead(PhoneUi.Subject);
        RefreshPhone();
    }

    private static void ClosePhone()
    {
        if (!_phoneOpen) return;
        _phoneOpen = false;
        EndCameraMode(); // FB-80: the live view let go, the camera locked again before the locks go
        CommitUndo();
        PhoneUi.Hide();
        PhoneUi.View.FirstTime = false;
        try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks failed: " + e.Message); }
        Ui.ShowPrompt(null);
    }

    private static float _phonePromptUntil = -1;

    private static void UpdatePhone()
    {
        Locks.KeepCursor();
        if (!Features.Phone || _phone == null) { ClosePhone(); return; }
        var kb = Keyboard.current;
        if (Time.frameCount > _phoneOpenedFrame + 1 && !PhoneUi.Typing && PhoneKeyPressed()) { ClosePhone(); return; }
        // The talk key with the phone out: no chat, a word under the crosshair instead.
        if (kb != null && !PhoneUi.Typing && Pressed(kb, ref _talkKey, Key.T, "TalkKey"))
        {
            Ui.ShowPrompt($"Put your phone away to talk ({PhoneKeyName})");
            _phonePromptUntil = Time.unscaledTime + 2.5f;
        }
        if (_phonePromptUntil > 0 && Time.unscaledTime >= _phonePromptUntil) { _phonePromptUntil = -1; Ui.ShowPrompt(null); }
        try { TickCameraMode(); } catch (Exception e) { Log.Debug("phone camera: " + e.Message); } // FB-80
    }

    private static void WirePhone()
    {
        PhoneUi.CloseRequested = ClosePhone;
        WireCamera(); // FB-80
        StoryDialogue.Voiced = AvaLineVoiced; // Ava's real voice on her mirrored story lines
        PhoneUi.VoiceDir = () => VoiceDir;
        PhoneUi.GameClip = GameVoiceClip; // FB-75: Ava's earlier lines, voiced from the game's own recordings
        PhoneUi.Changed = () =>
        {
            CommitUndo();
            if (PhoneUi.ScreenName == "thread") _phone?.MarkRead(PhoneUi.Subject);
            RefreshPhone();
        };
        PhoneUi.Send = SendText;
        // FB-71 Nightline
        PhoneUi.LinePost = LinePost;
        PhoneUi.LineReact = LineReact;
        PhoneUi.LineComment = LineComment;
        PhoneUi.LineDelete = LineDelete;
        PhoneUi.LineDeleteComment = LineDeleteComment;
        PhoneUi.LineCancel = LineCancel;
        PhoneUi.LineTab = _ => RefreshPhone();
        PhoneUi.Retry = RetryAnswer;
        PhoneUi.Toggle = PhoneToggle;
        PhoneUi.Step = PhoneStep;
        PhoneUi.RemovePlace = RemovePhonePlace;
        PhoneUi.AddHere = () => SetPlaceBars(_here.place, Reception.Full, undo: false);
        PhoneUi.PlacesDefaults = () => WithUndo("Back to the starting list.", "places", _cfg.PhoneWeakPlaces, Reception.DefaultPlaces);
        PhoneUi.CostsDefaults = () => WithUndo("Back to the defaults.", "weather", _cfg.PhoneWeatherCosts, Reception.DefaultCosts);
        PhoneUi.Undo = () => { var u = _undo; _undo = null; _undoCommit = null; u?.Invoke(); PhoneUi.View.Undo = ""; Signal(force: true); RefreshPhone(); };
        PhoneUi.SetWallpaper = SetWallpaperLink;
        PhoneUi.ShowEarlier = () => { _threadWindow += 40; RefreshPhone(); };
        PhoneUi.DeleteText = (id, index, body) =>
        {
            if (_phone == null) return;
            var kept = _phone.TextAt(id, index, body);
            if (kept == null) return;
            CommitUndo();
            // The file is left until the Undo lapses (another change, or leaving the conversation), so Undo brings it all back.
            string removed = _phone.DeleteText(id, index, body);
            var book = _phone;
            if (removed.Length > 0) HoldVoice(removed, true); // in use until the Undo lapses (never pruned meanwhile)
            SetUndo("Message deleted.", "thread",
                undo: () => { HoldVoice(removed, false); book.PutBack(id, index, kept); },
                commit: () => { HoldVoice(removed, false); if (book.TextCount(id) >= 0) { book.DropVoice(removed); DeleteVoiceFile(removed); } });
            RefreshPhone();
        };
        PhoneUi.ClearThread = id =>
        {
            if (_phone == null) return;
            foreach (var f in _phone.ClearThread(id)) DeleteVoiceFile(f);
            // Phone pass PH-A5: an answer already being written doesn't land in the cleared conversation.
            if (PhonePending.TryGetValue(id, out var writing)) try { writing.Cts.Cancel(); } catch { }
            PhoneNoAnswer.TryRemove(id, out _);
            LeftOnRead.Remove(id);
            RefreshPhone();
        };
        PhoneUi.WallpaperDefault = WallpaperToDefault;
    }

    // ------------------------------------------------------------------ what the phone shows

    private sealed class Known
    {
        public string Tag = "", Kind = "known", Their = "their", Model = "";
        public InfoSheet? Sheet;
        public double Bond = 50;
        public int TimesMet;
    }

    /// <summary>How each contact stands with you, read on a pool thread (a glance at their record each).</summary>
    private static readonly ConcurrentDictionary<string, Known> ContactInfo = new();

    /// <summary>Review PH-R1: when each contact was last read for on demand (main thread only).</summary>
    private static readonly Dictionary<string, float> ContactTriedAt = new();

    /// <summary>Whether a contact missing from <see cref="ContactInfo"/> may be read now: once, then every 5 minutes at most.</summary>
    private static bool TryContactNow(string id)
    {
        float now = Time.unscaledTime;
        if (ContactTriedAt.TryGetValue(id, out float at) && now - at < 300f) return false;
        ContactTriedAt[id] = now;
        return true;
    }

    private static void LoadContactInfo(string? only = null)
    {
        var book = _phone;
        if (book == null) return;
        var store = ActiveBrain.Store;
        int generation = _resetGeneration;
        Task.Run(() =>
        {
            foreach (var c in book.Contacts().Where(c => only == null || c.Id == only))
            {
                try
                {
                    var rec = store.Glance(c.Id);
                    if (rec == null || generation != _resetGeneration) continue;
                    string tag = Texting.Tag(rec.State);
                    InfoSheet? sheet = null;
                    try { sheet = InfoSheet.Build(rec, null, c.Name, Prompts.Presentation(rec.Persona.GenderIdentity), ""); } catch { }
                    ContactInfo[c.Id] = new Known
                    {
                        Tag = tag, Kind = tag == "Partner" ? "partner" : tag == "Friend" ? "friend" : "known", Model = rec.Visual.Model ?? "", Sheet = sheet,
                        Bond = Features.Standing(rec.State.Relationship), TimesMet = Math.Max(0, rec.State.TimesMet - 1),
                        Their = Roleplay.Of(rec.Persona.Pronouns) switch { Roleplay.Pronouns p when p == Roleplay.Pronouns.She => "her", Roleplay.Pronouns p when p == Roleplay.Pronouns.He => "his", _ => "their" },
                    };
                }
                catch { }
            }
            if (generation == _resetGeneration) MainThread.Enqueue(() => { if (_phoneOpen) RefreshPhone(); });
        });
    }

    // ------------------------------------------------------------------ portraits (the author: "use character portraits for their avatar")

    /// <summary>Their portrait as the game has it, kept from a chat (by id).</summary>
    private static readonly Dictionary<string, Sprite> Portraits = new();
    /// <summary>Every portrait the game has loaded, by name (looked through once, again a while after a miss).</summary>
    private static Dictionary<string, Sprite>? _portraitsByName;
    private static float _portraitScanAt = -999f;

    private static Sprite? PortraitOf(string id, string model)
    {
        if (Portraits.TryGetValue(id, out var s) && s != null) { try { if (!s.WasCollected) return s; } catch { } Portraits.Remove(id); }
        if (model.Length == 0) return null;
        // FB-78: a model the last look didn't have ("female015") isn't looked for again and again (each look is a hitch).
        bool known = _portraitsByName != null && (_portraitsByName.ContainsKey(model) || _portraitsByName.ContainsKey(model + "_portrait") || PortraitMisses.Contains(model));
        if (_portraitsByName == null || (!known && Time.unscaledTime - _portraitScanAt > 120f))
        {
            _portraitScanAt = Time.unscaledTime;
            var map = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null) continue;
                    string n = (sp.name ?? "").Replace("(Clone)", "").Trim();
                    if (n.Contains("portrait", StringComparison.OrdinalIgnoreCase) && !map.ContainsKey(n)) map[n] = sp;
                }
            }
            catch (Exception e) { Log.Debug("portraits: " + e.Message); }
            _portraitsByName = map;
            PortraitMisses.Clear();
        }
        // FB-78: a model named for its body ("female015") has its portrait as "female015_portrait".
        if (_portraitsByName.TryGetValue(model, out var found) || _portraitsByName.TryGetValue(model + "_portrait", out found))
        {
            // Phone pass PH-A12: a portrait unloaded with its scene isn't handed out; the next look finds it again.
            bool gone = true;
            try { gone = found == null || found.WasCollected; } catch { }
            if (!gone) return found;
            // Review PH-R3: looked through again at once (not at every redraw: at most every 5 s), and asked again.
            if (Time.unscaledTime - _portraitScanAt < 5f) return null;
            _portraitsByName = null;
            return PortraitOf(id, model);
        }
        if (PortraitMisses.Count > 500) PortraitMisses.Clear();
        PortraitMisses.Add(model);
        return null;
    }

    private static readonly HashSet<string> PortraitMisses = new(StringComparer.OrdinalIgnoreCase);

    private static int _weekdayDay = int.MinValue;
    private static string _weekday = "";
    /// <summary>How many texts a conversation draws (the newest); Show earlier adds more.</summary>
    private static int _threadWindow = 40;
    private static string _threadWindowFor = "";

    /// <summary>"21:41" (the snapshot's clock) as the phone says it: "9:41 PM".</summary>
    internal static string TwelveHour(string clock)
    {
        var parts = (clock ?? "").Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return clock ?? "";
        return $"{(h % 12 == 0 ? 12 : h % 12)}:{m:00} {(h < 12 ? "AM" : "PM")}";
    }

    /// <summary>What the status bar and the home clock say: the time, the date, the bars, the carrier and the battery, now.</summary>
    private static (int hour, int minute) StatusNow(PhoneView v)
    {
        int hour = Math.Max(0, GameContext.Hour()), minute = Math.Max(0, GameContext.Minute()), day = GameContext.Day();
        v.Time = TwelveHour($"{hour:00}:{minute:00}");
        v.Clock = $"{(hour % 12 == 0 ? 12 : hour % 12)}:{minute:00}";
        // The weekday: read once a game day (a full snapshot isn't needed for it every redraw).
        if (day != _weekdayDay) { _weekdayDay = day; try { _weekday = GameContext.Capture(null).DayOfWeek; } catch { _weekday = ""; } }
        v.Date = (_weekday.Length > 0 ? _weekday + " · " : "") + (day >= 0 ? $"Day {day}" : "");
        v.Bars = _bars;
        v.Battery = Phone.Battery.Percent(hour, minute);
        v.Carrier = Reception.Carrier(_bars, _inApartment == 2);
        _clockShown = (hour * 60 + minute, _bars, _inApartment == 2);
        return (hour, minute);
    }

    private static (int minute, int bars, bool home) _clockShown = (-1, -1, false);

    /// <summary>FB-83 (the author: "the status bar above wouldn't update every in game minute"): while the phone is out, its
    /// time, bars and battery move each game minute (and a bar's change at once), without redrawing the whole screen.</summary>
    private static void TickPhoneClock()
    {
        if (!_phoneOpen || !PhoneUi.Built) return;
        int now = Math.Max(0, GameContext.Hour()) * 60 + Math.Max(0, GameContext.Minute());
        if (_clockShown == (now, _bars, _inApartment == 2)) return;
        StatusNow(PhoneUi.View);
        PhoneUi.RenderClock();
    }

    private static void RefreshPhone()
    {
        if (!PhoneUi.Built) return;
        var v = PhoneUi.View;
        var book = _phone;
        var (hour, _) = StatusNow(v);
        int day = GameContext.Day();
        var w = GameContext.WeatherNumbers();
        (v.Sky, v.SkyWords) = Reception.Sky(w.blizzard, w.snow, w.rain, w.thunder, w.fog, hour); // FB-74
        v.Key = PhoneKeyName;
        if (book == null) { PhoneUi.Render(); return; }
        MirrorAva();
        v.Unread = book.UnreadTotal();
        v.MyNumber = book.MyNumber;

        var contacts = book.Contacts();
        v.Contacts = contacts.Select(c =>
        {
            var k = ContactInfo.TryGetValue(c.Id, out var known) ? known : new Known();
            var lastText = book.LastText(c.Id);
            return new PhoneView.Person
            {
                Id = c.Id, Name = c.Name, Number = c.Number, Tag = k.Tag, Kind = k.Kind, Bond = k.Bond, TimesMet = k.TimesMet, Their = k.Their,
                Portrait = PortraitOf(c.Id, k.Model),
                About = k.Sheet?.Rows.Select(r => (r.Label, r.Value, r.Known)).ToList() ?? new(),
                AboutSections = k.Sheet?.Sections.Select(x => (x.heading, x.items.ToList())).ToList() ?? new(),
                Got = $"Day {c.GotDay}{(c.GotWhere.Length > 0 ? ", " + c.GotWhere : "")}",
                LastTexted = lastText != null ? $"Day {lastText.GameDay}, {TwelveHour(lastText.Clock)}" : "",
            };
        }).OrderByDescending(p => p.Id == _avaId).ThenByDescending(p => p.Kind == "partner").ThenByDescending(p => p.Bond).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        foreach (var p in v.Contacts.Where(p => p.Id == _avaId))
        {
            p.Number = "In your HUD";
            int talks = _story?.Scenes(p.Id).Count ?? 0;
            p.About = new() { ("Where", "With you, in your HUD", true), ("Story conversations", talks.ToString(), true) };
            p.AboutSections = new();
            p.Got = "from the start";
        }

        v.Conversations = book.Conversations().Select(c =>
        {
            var k = ContactInfo.TryGetValue(c.id, out var known) ? known : new Known();
            return new PhoneView.Conversation
            {
                Id = c.id, Name = c.name, Kind = k.Kind, Unread = c.unread > 0, Portrait = PortraitOf(c.id, k.Model),
                Preview = (c.last.From == PhoneBook.You ? "You: " : "") + c.last.Body.Replace('\n', ' '),
                When = c.last.GameDay == day ? TwelveHour(c.last.Clock) : $"Day {c.last.GameDay}",
            };
        }).ToList();

        if (PhoneUi.ScreenName == "thread") FillThread(v, PhoneUi.Subject, contacts);
        if (PhoneUi.ScreenName == "news") FillNews(v, day);
        FillNightline(v); // FB-71
        FillGallery(v); // FB-80
        FillSettingsView(v);
        PhoneUi.Render();
    }

    private static void FillThread(PhoneView v, string id, List<PhoneBook.Contact> contacts)
    {
        var c = contacts.FirstOrDefault(x => x.Id == id);
        v.ThreadId = id;
        v.ThreadName = c?.Name ?? "";
        v.ThreadNumber = id == _avaId ? "In your HUD" : c?.Number ?? "";
        v.ThreadInitial = (c?.Name ?? "").Trim() is { Length: > 0 } nm ? char.ToUpperInvariant(nm[0]).ToString() : "";
        v.Thread.Clear();
        if (_threadWindowFor != id) { _threadWindowFor = id; _threadWindow = 40; }
        var (texts, firstIndex) = _phone!.Newest(id, _threadWindow);
        v.Earlier = firstIndex;
        long prev = long.MinValue;
        // FB-70: the circles: on your texts they haven't answered (they go once they answer); their initial on the last seen.
        int lastTheirs = texts.FindLastIndex(t => t.From == PhoneBook.Them);
        int lastYours = texts.FindLastIndex(t => t.From == PhoneBook.You);
        int lastSeen = texts.FindLastIndex(t => t.From == PhoneBook.You && t.State == PhoneBook.Read);
        for (int i = 0; i < texts.Count; i++)
        {
            var t = texts[i];
            bool storyStart = t.Story && (i == 0 || !texts[i - 1].Story);
            if (prev == long.MinValue || t.Minute - prev >= 60 || storyStart)
                v.Thread.Add(new PhoneView.Line { From = "stamp", Text = $"Day {t.GameDay} · {TwelveHour(t.Clock)}{(t.Story ? " · from the story" : "")}" });
            prev = t.Minute;
            var line = new PhoneView.Line { From = t.From == PhoneBook.You ? "you" : "them", Text = t.Body, Index = firstIndex + i, Story = t.Story };
            if (t.From == PhoneBook.Them && t.Voice.Length > 0)
            {
                line.Voice = t.Voice; line.VoiceSeconds = t.VoiceSeconds; line.Wave = VoiceNote.ParseWave(t.Wave);
                line.MadeVoice = id == _avaId && t.Voice.StartsWith("note-", StringComparison.Ordinal); // not one of her recordings
            }
            if (t.From == PhoneBook.You && !t.Story) // a mirrored story line has no receipts
            {
                bool answered = i < lastTheirs;
                if (t.State == PhoneBook.Waiting) { line.Failed = true; line.Receipt = "fail"; line.Meta = "Not sent yet · waiting for signal"; }
                else if (!answered)
                {
                    line.Receipt = t.State switch { PhoneBook.Sending => "sending", PhoneBook.Sent => "sent", PhoneBook.Delivered => "delivered", _ => i == lastSeen ? "seen" : "" };
                    // Under your last unanswered text, and under one whose state isn't the next one's (still sending, say).
                    var next = texts.Skip(i + 1).FirstOrDefault(x => x.From == PhoneBook.You);
                    if (i == lastYours || (next != null && next.State != t.State) || i == lastSeen)
                        line.Meta = t.State switch
                        {
                            PhoneBook.Sending => _bars == 1 ? "Sending… (one bar)" : "Sending…",
                            PhoneBook.Sent => "Sent",
                            PhoneBook.Delivered => "Delivered",
                            PhoneBook.Read => t.SeenAt >= 0 ? $"Seen {ClockOf(t.SeenAt)}" : "Seen",
                            _ => "",
                        };
                    if (line.Meta.Length == 0) line.Receipt = "";
                }
            }
            v.Thread.Add(line);
        }
        v.Typing = PhonePending.TryGetValue(id, out var p) && p.Typing;
        v.NoAnswer = PhoneNoAnswer.TryGetValue(id, out var na) ? na.text : "";
        v.CanRetry = PhoneNoAnswer.TryGetValue(id, out var nb) && nb.retry;
        v.NoSignal = _bars <= 0 ? $"No signal here{(_barsWhy.Length > 0 ? $" ({_barsWhy})" : "")}. Texts wait and go out when you have a bar again." : "";
    }

    /// <summary>The News app: the holos' headlines, and what people have been saying around town (never anyone under 18's).</summary>
    private static void FillNews(PhoneView v, int day)
    {
        v.News.Clear();
        v.Talk.Clear();
        if (_world == null) return;
        string When(int d) => d == day ? "Today" : day - d == 1 ? "Yesterday" : $"Day {d}";
        foreach (var (d, title, summary) in _world.Headlines()) v.News.Add((When(d), title, summary));
        foreach (var (_, text, where) in _world.TalkAroundTown(Math.Max(0, day))) v.Talk.Add((text, where));
    }

    private static void FillSettingsView(PhoneView v)
    {
        v.Signal = _cfg.PhoneSignal.Value;
        v.Weather = _cfg.PhoneWeather.Value;
        v.Places = _cfg.PhonePlaces.Value;
        v.PeopleText = _cfg.PeopleTextYou.Value;
        v.RealLife = _cfg.TextsRealLife.Value;
        v.NeverSees = Math.Clamp(_cfg.TextNeverSees.Value, 0, 50);
        v.AnswersLater = Math.Clamp(_cfg.TextAnswersLater.Value, 0, 50);
        v.VoiceNotes = _cfg.TextVoiceNotes.Value;
        v.AvaVoice = _cfg.AvaVoice.Value;
        v.VoiceChance = Math.Clamp(_cfg.VoiceNoteChance.Value, 0, 100);
        var costs = Reception.ParseCosts(_cfg.PhoneWeatherCosts.Value);
        for (int i = 0; i < WeatherCosts.Kinds.Length; i++) v.Costs[i] = costs.Get(WeatherCosts.Kinds[i]);
        v.CostsSummary = $"rain {costs.Rain} · blizzard {costs.Blizzard}";
        var places = Reception.ParsePlaces(_cfg.PhoneWeakPlaces.Value);
        v.PlaceList = places;
        v.Indoors = Math.Clamp(_cfg.PhoneIndoorsCost.Value, 0, 2);
        v.PlacesSummary = $"{places.Count} place{(places.Count == 1 ? "" : "s")}, indoors {v.Indoors}";
        var w = GameContext.WeatherNumbers();
        v.WeatherNow = w.blizzard > 0.3f ? 3 : w.snow > 0.5f ? 2 : w.rain > 0.6f ? 0 : w.thunder > 0.3f ? 1 : w.fog > 0.5f ? 4 : -1;
        v.HerePlace = _here.place;
        var listed = places.FirstOrDefault(p => _here.place.Length > 0 && string.Equals(p.place, _here.place, StringComparison.OrdinalIgnoreCase));
        v.HereListed = listed.place != null;
        v.HereBars = v.HereListed ? listed.bars : Reception.Full;
        v.ReadoutBars = _bars;
        v.Readout = !v.Signal ? "4 · full" : $"{_bars} · {(_inApartment == 2 ? "home" : string.Join(", ", new[] { _here.place, _barsWhy }.Where(x => x.Length > 0).Distinct()))}".TrimEnd(' ', '·');
        v.WallpaperUrl = _cfg.PhoneWallpaper.Value;
        v.WallpaperBusy = _wallBusy;
        v.WallpaperError = _wallError;
        v.WallpaperNote = _wallNote;
        v.WallpaperKept = _wallKept;
        v.Undo = _undo != null ? _undoText : "";
        v.UndoScreen = _undoScreen;
    }

    // ------------------------------------------------------------------ the signal

    private static int _bars = Reception.Full;
    private static string _barsWhy = "";
    private static (string place, string scene) _here = ("", "");
    private static float _nextSignal;
    private static bool _indoors;

    private static void TickSignal()
    {
        if (Time.unscaledTime < _nextSignal) return;
        Signal();
    }

    private static void Signal(bool force = false)
    {
        _nextSignal = Time.unscaledTime + 2f;
        int before = _bars;
        try
        {
            string place = "", scene = "";
            try { place = GameContext.Capture(null).Location; } catch { }
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
            _here = (place, scene);
            // Indoors: a roof over your head (a ray straight up hits something), or someone else's apartment.
            _indoors = _inApartment == 1;
            try
            {
                var pc = GameContext.PlayerCharacter();
                if (!_indoors && pc != null) _indoors = Physics.Raycast(pc.transform.position + Vector3.up * 2.2f, Vector3.up, 30f);
            }
            catch { }
            var w = GameContext.WeatherNumbers();
            var (bars, why) = Reception.Bars(new ReceptionInputs
            {
                Simulate = _cfg.PhoneSignal.Value, WeatherMatters = _cfg.PhoneWeather.Value, PlacesMatter = _cfg.PhonePlaces.Value,
                AtHome = _inApartment == 2, Indoors = _indoors, IndoorsCost = _cfg.PhoneIndoorsCost.Value,
                Location = place, Scene = scene, Blizzard = w.blizzard, Snow = w.snow, Rain = w.rain, Thunder = w.thunder, Fog = w.fog,
                Minute = Math.Max(0, GameNow()), Places = Reception.ParsePlaces(_cfg.PhoneWeakPlaces.Value),
                Costs = Reception.ParseCosts(_cfg.PhoneWeatherCosts.Value),
            });
            _bars = bars;
            _barsWhy = why;
        }
        catch (Exception e) { Log.Debug("phone signal: " + e.Message); }
        if (_bars > 0) SendWaitingLines(); // FB-71: your Nightline lines that waited for a bar
        // A bar again: what waited goes out, and each is answered.
        if (_bars > 0 && _phone != null && _phone.AnyWaiting())
        {
            foreach (var id in _phone.SendWaiting())
            {
                QueueSend(id);
                if (!_phoneOpen) PhoneUi.Toast("Phone", $"Signal's back: sent your text to {Prompts.FirstName(_phone.Get(id)?.Name ?? "them")}.", system: true);
            }
            if (_phoneOpen) RefreshPhone();
        }
        else if (_phoneOpen && (force || before != _bars)) RefreshPhone();
    }

    // ------------------------------------------------------------------ texts: yours, their answers (FB-70: like real life)

    private sealed class Pending
    {
        public readonly CancellationTokenSource Cts = new();
        public volatile bool Typing, Again;
    }

    /// <summary>An answer being written (the AI call, the dots, their texts landing one by one).</summary>
    private static readonly ConcurrentDictionary<string, Pending> PhonePending = new();
    /// <summary>Couldn't get their reply (a fault, never their choice): the line, and whether Try again is offered.</summary>
    private static readonly ConcurrentDictionary<string, (string text, bool retry)> PhoneNoAnswer = new();
    /// <summary>A text of yours on its way (real seconds: the signal decides): when it's sent, then when it reaches them.</summary>
    private static readonly Dictionary<string, float> SendingUntil = new(), DeliverAt = new();
    /// <summary>When they saw your texts and chose not to answer (game minutes): told to them when they do answer.</summary>
    private static readonly Dictionary<string, long> LeftOnRead = new();
    private static readonly LockedRandom PhoneRng = new(); // used from the main thread and pool threads alike (review PH-4)
    private static float _nextPlanTick;

    private static ReplyOdds OddsNow() => new()
    {
        RealLife = _cfg.TextsRealLife.Value,
        NeverSeesPct = Math.Clamp(_cfg.TextNeverSees.Value, 0, 50),
        LaterPct = Math.Clamp(_cfg.TextAnswersLater.Value, 0, 50),
    };

    private static void SendText(string id, string text)
    {
        if (_phone == null || !Features.Phone || KnownUnder18(id)) return;
        var snap = GameContext.Capture(null);
        var sent = _phone.Send(id, text, snap.GameDay, snap.Clock, Math.Max(0, GameNow()), signal: _bars > 0 || id == _avaId); // Ava is in your HUD: no signal needed
        if (sent == null) return;
        PhoneNoAnswer.TryRemove(id, out _);
        if (sent.State == PhoneBook.Sending) QueueSend(id);
        RefreshPhone();
    }

    /// <summary>On its way: a moment with a good signal, longer on one bar, and now and then a weak spot holds it a while.</summary>
    private static void QueueSend(string id)
    {
        if (SendingUntil.ContainsKey(id)) return;
        int bars = id == _avaId ? Reception.Full : Math.Max(1, _bars);
        SendingUntil[id] = Time.unscaledTime + Reception.SendSeconds(bars) + (id == _avaId ? 0 : TextLife.Stall(PhoneRng, bars));
    }

    /// <summary>Every frame: sends going out and arriving; every second, what people do with what they've got.</summary>
    private static void TickTexts()
    {
        var book = _phone;
        // The 2026-10-20 audit: with the phone switched off nothing moves (no sending, no answers written, nothing spent);
        // it all goes on where it was when it's back on.
        if (book == null || !Features.Phone) return;
        float now = Time.unscaledTime;
        bool changed = false;
        if (SendingUntil.Count > 0) // PH-A8: nothing on its way, nothing built each frame
        foreach (var id in SendingUntil.Where(kv => now >= kv.Value && (_bars > 0 || kv.Key == _avaId)).Select(kv => kv.Key).ToList())
            {
                SendingUntil.Remove(id);
                if (book.MarkSent(id)) { DeliverAt[id] = now + 0.7f + (float)PhoneRng.NextDouble() * 0.9f; changed = true; }
            }
        if (DeliverAt.Count > 0)
        foreach (var id in DeliverAt.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList())
        {
            DeliverAt.Remove(id);
            book.MarkDelivered(id);
            PlanFor(id);
            changed = true;
        }
        if (changed && _phoneOpen) RefreshPhone();
        TickInbox(now); // FB-79: texts on their way to you
        if (now < _nextPlanTick) return;
        _nextPlanTick = now + 1f;
        TickPlans(book);
    }

    /// <summary>A minute of the game's clock as the phone says it ("9:41 PM").</summary>
    private static string ClockOf(long minute) => minute < 0 ? "" : TwelveHour($"{Mind.GameClock.HourOf(minute):00}:{minute % 60:00}");

    /// <summary>
    /// Your texts reached them: what they'll do (FB-70). Mid-answer, the answer covers it too. Already going to look: they'll
    /// see it with the rest. Otherwise a roll: they answer, see it and answer later (or never), or never see it, by how they
    /// feel about you, whether they're your partner, and whether you're already texting back and forth.
    /// </summary>
    private static void PlanFor(string id)
    {
        var book = _phone;
        if (book == null || KnownUnder18(id)) return;
        if (PhonePending.TryGetValue(id, out var running)) { running.Again = true; return; }
        long now = Math.Max(0, GameNow());
        var (plan, lookAt, answerAt, lookedAt) = book.PlanOf(id);
        // FB-78: already going to answer (to look, or seen and about to type): this text is answered with the rest.
        if (plan == "answers" && (lookAt >= now || answerAt >= now))
        {
            // Phone pass PH-A3: they've already looked and are about to type: this one is seen now too (never typing before seen).
            if (lookAt < 0) { book.MarkSeen(id, now); if (_phoneOpen) RefreshPhone(); }
            return;
        }
        // She's with you: she sees it at once, and types after a moment, as anyone answering right away does (FB-78).
        if (id == _avaId) { book.SetPlan(id, "answers", now, now + TextLife.AnswerAfter(PhoneRng, OddsNow().RealLife)); return; }
        var texts = book.Texts(id);
        var lastTheirs = texts.LastOrDefault(t => t.From == PhoneBook.Them);
        bool momentum = lastTheirs != null && now - lastTheirs.Minute is >= 0 and <= 30;
        bool textedAgain = plan is "later" or "never";
        if (plan == "later" && lookedAt >= 0) LeftOnRead[id] = lookedAt;
        var known = ContactInfo.TryGetValue(id, out var k) ? k : new Known();
        if (!ContactInfo.ContainsKey(id) && TryContactNow(id)) LoadContactInfo(id); // PH-A6: their bond and kind for the next time (read off-thread)
        var odds = OddsNow();
        var outcome = TextLife.Decide(PhoneRng, odds, known.Bond, known.Kind == "partner", momentum, textedAgain);
        int hour = Math.Max(0, GameContext.Hour()), minute = Math.Max(0, GameContext.Minute());
        bool quick = momentum || (textedAgain && LeftOnRead.ContainsKey(id));
        long look = now + TextLife.SeenAfter(PhoneRng, hour, minute, quick, odds.RealLife);
        switch (outcome)
        {
            case ReplyOutcome.Answers: book.SetPlan(id, "answers", look, look + TextLife.AnswerAfter(PhoneRng, odds.RealLife)); break; // FB-78: 1-10 min after seen
            case ReplyOutcome.AnswersLater: book.SetPlan(id, "later", look, -1); break;
            default: book.SetPlan(id, "never", -1, -1); break;
        }
    }

    /// <summary>They look at their phone when they do; they answer when they choose to.</summary>
    private static void TickPlans(PhoneBook book)
    {
        long now = GameNow();
        if (now < 0) return;
        foreach (var id in book.Planned())
        {
            if (KnownUnder18(id)) { book.SetPlan(id, "", -1, -1); continue; }
            var (plan, lookAt, answerAt, _) = book.PlanOf(id);
            if (lookAt >= 0 && lookAt - now > 3 * 1440) lookAt = now; // the clock went back (an earlier day loaded): they look now
            if (answerAt >= 0 && answerAt - now > 3 * 1440) answerAt = now;
            // Never seen, or seen and never coming back to it: after half a day it's simply left (they can text you again).
            if (plan == "never" || (plan == "later" && lookAt < 0 && answerAt < 0))
            {
                var last = book.LastText(id);
                if (last == null || now - last.Minute > 720 || now < last.Minute - 1440) { book.SetPlan(id, "", -1, -1); LeftOnRead.Remove(id); }
                continue;
            }
            if (lookAt >= 0 && now >= lookAt)
            {
                book.MarkSeen(id, now);
                if (plan == "later")
                {
                    // Seen, and not answered: they remember choosing not to, and may come back to it in their own time.
                    LeftOnRead[id] = now;
                    var known = ContactInfo.TryGetValue(id, out var k) ? k : new Known();
                    long back = TextLife.ComesBack(PhoneRng, known.Bond) ? now + TextLife.LaterAfter(PhoneRng) : -1;
                    book.SetPlan(id, "later", -1, back);
                    RememberLeftOnRead(id);
                }
                else book.SetPlan(id, plan, -1, answerAt);
                if (_phoneOpen) RefreshPhone();
                continue;
            }
            if (answerAt >= 0 && now >= answerAt && !PhonePending.ContainsKey(id))
            {
                var texts = book.TextsForAi(id);
                int lastTheirs = texts.FindLastIndex(t => t.From == PhoneBook.Them);
                var unanswered = texts.Skip(lastTheirs + 1).Where(t => t.From == PhoneBook.You).ToList();
                if (unanswered.Count == 0)
                {
                    // Phone pass PH-A5: nothing of yours left to answer (deleted, or not arrived yet): no answer out of nowhere.
                    book.SetPlan(id, "", -1, -1);
                    LeftOnRead.Remove(id);
                    continue;
                }
                bool hadSeen = LeftOnRead.TryGetValue(id, out long seen);
                bool textedAgain = hadSeen && unanswered.Any(t => t.Minute > seen);
                string note = TextLife.Note(hadSeen ? ReplyOutcome.AnswersLater : ReplyOutcome.Answers,
                    unanswered.FirstOrDefault()?.Minute ?? -1, hadSeen ? seen : -1, now, textedAgain, ClockOf);
                book.SetPlan(id, "", -1, -1);
                LeftOnRead.Remove(id);
                StartAnswer(id, note);
            }
        }
    }

    /// <summary>FB-70: "it should record the event for the npc to remember they chose not to respond right away".</summary>
    private static void RememberLeftOnRead(string id)
    {
        var book = _phone;
        if (book == null) return;
        var texts = book.Texts(id);
        int lastTheirs = texts.FindLastIndex(t => t.From == PhoneBook.Them);
        var yours = texts.Skip(lastTheirs + 1).Where(t => t.From == PhoneBook.You).Select(t => "\"" + ReplyParser.Quotable(t.Body, 120) + "\"").ToList();
        if (yours.Count == 0) return;
        var snap = GameContext.Capture(null);
        string scene = $"[By text, day {snap.GameDay}, {snap.Clock}: they texted you {string.Join(" / ", yours)}. You saw it and chose not to answer yet (busy, or not in the mood).]";
        var brain = ActiveBrain;
        Task.Run(() => brain.RecordScene(id, scene, snap));
    }

    /// <summary>
    /// They answer: now and then the dots show and stop (they put the phone down) before they come back; the dots for
    /// about as long as typing it takes; their texts one after another. One answer at a time; what you add meanwhile is
    /// planned again after it.
    /// </summary>
    private static void StartAnswer(string id, string note)
    {
        if (_phone == null || KnownUnder18(id)) return;
        note = CompanyNote(id, note); // FB-72: whether they could come keep you company now
        var p = new Pending();
        if (!PhonePending.TryAdd(id, p)) return;
        var book = _phone;
        var brain = ActiveBrain;
        int generation = _resetGeneration;
        bool falseStart = _cfg.TextsRealLife.Value && TextLife.FalseStart(PhoneRng, momentum: false);
        bool voiceNotes = _cfg.TextVoiceNotes.Value;
        int voiceChance = _cfg.VoiceNoteChance.Value;
        bool avaVoiceOn = _cfg.AvaVoice.Value;
        long startedAt = Math.Max(0, GameNow()); // what they saw when they began (review PH-5: a text sent meanwhile isn't seen yet)
        Task.Run(async () =>
        {
            try
            {
                if (falseStart)
                {
                    p.Typing = true;
                    MainThread.Enqueue(RefreshIfThread(id));
                    await Task.Delay(TimeSpan.FromSeconds(3 + PhoneRng.NextDouble() * 3), p.Cts.Token).ConfigureAwait(false);
                    p.Typing = false;
                    MainThread.Enqueue(RefreshIfThread(id));
                    await Task.Delay(TimeSpan.FromSeconds(12 + PhoneRng.NextDouble() * 25), p.Cts.Token).ConfigureAwait(false);
                }
                p.Typing = true;
                var snapTask = new TaskCompletionSource<GameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                MainThread.Enqueue(() =>
                {
                    RefreshIfThread(id)();
                    try { var s = GameContext.Capture(null); s.PlayerGender = _cfg.PlayerGender.Value; snapTask.TrySetResult(s); }
                    catch (Exception e) { snapTask.TrySetException(e); }
                });
                var snap = await snapTask.Task.ConfigureAwait(false);
                List<string>? texts;
                string? error;
                bool avaVoice = false, coming = false;
                if (id == _avaId)
                    (texts, avaVoice, error) = await brain.AvaTextAsync(id, "Ava", book.TextsForAi(id), snap, first: false, avaVoiceOn, p.Cts.Token, note).ConfigureAwait(false);
                else (texts, coming, error) = await brain.TextAsync(id, book.TextsForAi(id), snap, first: false, p.Cts.Token, note).ConfigureAwait(false);
                if (generation != _resetGeneration) return;
                if (texts == null || texts.Count == 0)
                {
                    p.Typing = false;
                    string name = Prompts.FirstName(book.Get(id)?.Name ?? "them");
                    bool noKey = error != null && (error.Contains("key", StringComparison.OrdinalIgnoreCase) || error.Contains("401"));
                    // Phone pass PH-A10: why, in a few words ("[AI-LIMIT] ... no credits remaining."), as world chat says it (FB-84).
                    string why = error is { Length: > 0 } ? " " + ProblemCodes.FirstSentence(ProblemCodes.Coded(Redact.Hide(error, _cfg.ApiKey.Value)), 110) : "";
                    PhoneNoAnswer[id] = noKey
                        ? ("Couldn't get a reply: there's no API key yet. Set one in the mod's settings (ApiKey).", false)
                        : ($"Couldn't get {name}'s reply.{(why.Length > 0 ? why : " (the AI didn't answer)")}", true);
                    if (error != null) Log.Info($"The phone couldn't get {name}'s reply: {error}");
                    MainThread.Enqueue(RefreshIfThread(id));
                    return;
                }
                for (int i = 0; i < texts.Count; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(texts[i].Length / 18.0, 1.0, 4.5)), p.Cts.Token).ConfigureAwait(false);
                    string body = texts[i];
                    MainThread.Enqueue(() => TextArrived(id, body, generation, startedAt));
                    if (i < texts.Count - 1) await Task.Delay(TimeSpan.FromSeconds(0.6), p.Cts.Token).ConfigureAwait(false);
                }
                p.Typing = false;
                MainThread.Enqueue(RefreshIfThread(id));
                if (coming) // FB-72: they're on their way to keep you company
                {
                    string who = book.Get(id)?.Name ?? "Someone";
                    MainThread.Enqueue(() => { if (generation == _resetGeneration) QueueVisitor(id, who, "text"); });
                }
                // Now and then, a voice note with their last text (TextVoiceNotes, VoiceNoteChance).
                string said = texts[^1];
                // Ava never takes the voice-notes chance: only her own wish, with AvaVoice on (her new words have no recording).
                if (id == _avaId) { if (avaVoice && avaVoiceOn) await VoiceNoteFor(id, said, generation, ava: true).ConfigureAwait(false); }
                else if (VoiceNote.Attach(PhoneRng, voiceNotes, voiceChance, said)) await VoiceNoteFor(id, said, generation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Warn("A text's answer failed: " + e.Message);
                // Phone pass PH-A4: said, with Try again (the plan was already used), never silent dots.
                string who = book.Get(id)?.Name is { Length: > 0 } n ? Prompts.FirstName(n) + "'s" : "their";
                if (generation == _resetGeneration) PhoneNoAnswer[id] = ($"Couldn't get {who} reply (something went wrong).", true);
            }
            finally
            {
                p.Typing = false;
                PhonePending.TryRemove(new KeyValuePair<string, Pending>(id, p));
                MainThread.Enqueue(RefreshIfThread(id)); // PH-A4: the dots go, whatever happened
                // You texted while they were answering: that's planned now (they're mid-conversation, so quickly).
                if (p.Again && generation == _resetGeneration) MainThread.Enqueue(() => PlanFor(id));
            }
        });
    }

    private static string VoiceDir => Path.Combine(_dataDir, "phone-voice");

    /// <summary>
    /// A voice note of their text: said in their own voice (the voices the mod gives them, "text voice" in the usage record),
    /// through a phone's speaker, kept on the PC as a WAV with its wave worked out from it, and attached to the text. Nothing
    /// when there's no voice (no provider, the voice limit): the text stands on its own.
    /// </summary>
    private static async Task VoiceNoteFor(string id, string text, int generation, bool ava = false)
    {
        try
        {
            VoiceSubject subject;
            if (ava) subject = new VoiceSubject { Id = id, Presentation = "female", Age = 28, Description = "Ava, a calm, warm companion who speaks through a HUD" };
            else
            {
                var rec = ActiveBrain.Store.Glance(id);
                if (rec == null || KnownUnder18(id) || ActiveBrain.IsKnownMinor(rec)) return;
                subject = VoiceSubject.From(rec);
            }
            var voice = await VoiceForAsync(() => subject, text, CancellationToken.None, "text voice").ConfigureAwait(false);
            if (voice.Audio == null || voice.Audio.Samples.Length == 0 || generation != _resetGeneration) return;
            var audio = ava ? voice.Audio : PhoneSpeaker.Filter(voice.Audio); // hers is in your HUD, not down a phone line
            string dir = VoiceDir;
            Directory.CreateDirectory(dir);
            string file = $"note-{Guid.NewGuid():N}.wav";
            File.WriteAllBytes(Path.Combine(dir, file), VoiceNote.ToWav(audio.Samples, audio.SampleRate, audio.Channels));
            var inUse = _phone?.VoiceFiles();
            var pending = HeldVoices();
            VoiceNote.Prune(dir, inUse: inUse == null ? null : n => inUse.Contains(n) || pending.Contains(n, StringComparer.OrdinalIgnoreCase));
            string wave = VoiceNote.WaveText(VoiceNote.Wave(audio.Samples));
            float seconds = audio.Seconds;
            MainThread.Enqueue(() =>
            {
                if (generation != _resetGeneration || _phone == null) return;
                if (_phone.AttachVoice(id, text, file, seconds, wave)) { if (_phoneOpen) RefreshPhone(); return; }
                // FB-79: its text is still on its way: the voice note goes with it (and isn't pruned meanwhile).
                var going = Inbox.LastOrDefault(x => x.Id == id && x.Body == text && x.Voice == null);
                if (going != null) { going.Voice = (file, seconds, wave); lock (PendingVoiceFiles) PendingVoiceFiles.Add(file); }
            });
        }
        catch (Exception e) { Log.Debug("voice note: " + e.Message); }
    }

    /// <summary>Voice files waiting on an Undo: pruning never takes them.</summary>
    private static readonly HashSet<string> PendingVoiceFiles = new(StringComparer.OrdinalIgnoreCase);

    // Locked: a voice note finishing on a pool thread reads it while a delete or its Undo changes it (the full audit, B-6).
    private static void HoldVoice(string file, bool add) { lock (PendingVoiceFiles) { if (add) PendingVoiceFiles.Add(file); else PendingVoiceFiles.Remove(file); } }
    private static List<string> HeldVoices() { lock (PendingVoiceFiles) return PendingVoiceFiles.ToList(); }

    /// <summary>A deleted text's voice note goes from the PC too (only a plain name in the voice notes folder).</summary>
    private static void DeleteVoiceFile(string file)
    {
        if (!VoiceNote.SafeName(file)) return;
        try { File.Delete(Path.Combine(VoiceDir, file)); } catch (Exception e) { Log.Debug("voice note: " + e.Message); }
    }

    /// <summary>Try again (after a fault): they answer now.</summary>
    private static void RetryAnswer(string id)
    {
        PhoneNoAnswer.TryRemove(id, out _);
        StartAnswer(id, "");
        RefreshPhone();
    }

    private static Action RefreshIfThread(string id) => () => { if (_phoneOpen) RefreshPhone(); };

    /// <summary>A text from them lands: in the conversation (read if it's open), or as a card while the phone is away.</summary>
    // FB-79: texts on their way to you, by your signal (none: they wait for a bar). Ava is in your HUD: hers come at once.
    private sealed class Incoming
    {
        public string Id = "", Body = "";
        public int Generation;
        public long ReadUpTo;
        public float At;
        public bool Held;
        public (string file, float seconds, string wave)? Voice;
    }
    private static readonly List<Incoming> Inbox = new();

    /// <summary>PH-R2: a reset drops what was on its way (one person's, or everyone's with null); their voice notes go too.</summary>
    private static void DropInbox(string? id)
    {
        foreach (var x in Inbox.Where(x => id == null || x.Id == id).ToList())
        {
            Inbox.Remove(x);
            if (x.Voice is { } v) { lock (PendingVoiceFiles) PendingVoiceFiles.Remove(v.file); DeleteVoiceFile(v.file); }
        }
    }

    /// <summary>A text for you: on its way (FB-79), then in its thread with its pip.</summary>
    private static void TextArrived(string id, string body, int generation = -1, long readUpTo = long.MaxValue)
    {
        if (_phone == null || KnownUnder18(id) || (generation >= 0 && generation != _resetGeneration)) return;
        if (id == _avaId || !_cfg.PhoneSignal.Value) { DeliverText(id, body, generation, readUpTo); return; }
        float now = Time.unscaledTime;
        float after = _bars > 0 ? Reception.ReceiveSeconds(_bars) + TextLife.Stall(PhoneRng, _bars) : 0f;
        // One after another, in the order they were sent.
        float at = Math.Max(now + after, Inbox.Where(x => x.Id == id).Select(x => x.At + 0.5f).DefaultIfEmpty(0f).Max());
        Inbox.Add(new Incoming { Id = id, Body = body, Generation = generation, ReadUpTo = readUpTo, At = at, Held = _bars <= 0 });
    }

    /// <summary>FB-79: the texts on their way, every frame: with no bar they wait; a bar back, they come in by its speed.</summary>
    private static void TickInbox(float now)
    {
        if (Inbox.Count == 0) return;
        if (_bars <= 0) { foreach (var x in Inbox) x.Held = true; return; }
        float next = now;
        foreach (var x in Inbox.Where(x => x.Held))
        {
            next += Reception.ReceiveSeconds(_bars);
            x.At = next; x.Held = false;
        }
        foreach (var x in Inbox.Where(x => now >= x.At).ToList())
        {
            Inbox.Remove(x);
            DeliverText(x.Id, x.Body, x.Generation, x.ReadUpTo, voice: x.Voice);
        }
    }

    /// <summary>The phone book closing (a stop, another save): what's on its way goes in quietly, never lost.</summary>
    private static void DeliverInboxQuietly()
    {
        // Phone pass PH-A11: into the book being closed, whatever generation they were sent in (a save switch moves it on
        // first): what was already paid for and on its way is never lost. Minors are still kept out (DeliverText).
        foreach (var x in Inbox.ToList()) { try { DeliverText(x.Id, x.Body, -1, x.ReadUpTo, quiet: true, voice: x.Voice); } catch { } }
        Inbox.Clear();
    }

    private static void DeliverText(string id, string body, int generation = -1, long readUpTo = long.MaxValue, bool quiet = false,
                                    (string file, float seconds, string wave)? voice = null)
    {
        if (voice is { } held) lock (PendingVoiceFiles) PendingVoiceFiles.Remove(held.file);
        if (_phone == null || KnownUnder18(id) || (generation >= 0 && generation != _resetGeneration)) return; // review PH-3
        var snap = GameContext.Capture(null);
        bool open = _phoneOpen && PhoneUi.ScreenName == "thread" && PhoneUi.Subject == id;
        if (_phone.Receive(id, body, snap.GameDay, snap.Clock, Math.Max(0, GameNow()), open, readUpTo) == null) return;
        if (voice is { } v) _phone.AttachVoice(id, body, v.file, v.seconds, v.wave); // its voice note came with it (FB-79)
        if (!open && !quiet)
        {
            var k = ContactInfo.TryGetValue(id, out var known) ? known : new Known();
            if (!_phoneOpen && Features.Phone) // an answer already written as the phone was switched off is kept, quietly
            {
                string from = _phone.Get(id)?.Name ?? "";
                if (PhoneUi.NotifyStyle == "Card") PhoneUi.Toast(from, body, k.Kind); // the old card, with the words
                PhoneUi.Notify(from); // CF-20: the pip (who, never what) and the chime
            }
        }
        if (_phoneOpen) RefreshPhone();
    }

    // ------------------------------------------------------------------ texts they send first

    private static float _nextTextFirst = 60f;

    /// <summary>
    /// Advanced mode has no Dynamic rounds to pick a text from: there it's rolled on its own each minute (DynamicTextWeight
    /// still sets how likely, out of 100). In Dynamic mode the rounds pick it by its weight (TryTextFirst).
    /// </summary>
    private static void TickTextFirst()
    {
        if (Time.unscaledTime < _nextTextFirst) return;
        _nextTextFirst = Time.unscaledTime + 60f;
        var s = ChatterSettingsNow();
        if (s.Dynamic || !s.Enabled || s.WeightText <= 0) return;
        if (Social.ChatterPlan.InLastHour(ChatterStarts, Time.unscaledTime) >= s.PerHour) return;
        if (!Social.ChatterPlan.Spaced(ChatterStarts, Time.unscaledTime, s.PerHour, 2)) return; // PH-A9: the pacer's spacing too
        if (UnityEngine.Random.value * 100f > Mathf.Clamp(s.WeightText, 0, 100) * 0.5f) return;
        TryTextFirst(out _);
    }

    /// <summary>
    /// A friend or partner who has your number texts you first (the author: "a new option that can happen, receiving a text
    /// from someone you know"): one of the residents' kinds, picked by DynamicTextWeight, counted as a start (the hourly cap,
    /// the Usage Optimizer, its pacer). Not more often than every TextFirstHours of game time, never between 1 and 8 in the
    /// morning, never someone you're talking to or texting with right now, never anyone under 18.
    /// </summary>
    private static bool TryTextFirst(out string why)
    {
        why = "";
        var book = _phone;
        if (book == null || !Features.Phone || !_cfg.PeopleTextYou.Value || _resetting) { why = "the phone is off"; return false; }
        if (!AiAllowed()) { why = "the AI limit"; return false; }
        long now = GameNow();
        int hour = GameContext.Hour();
        if (now < 0 || hour < 8 && hour >= 1) { why = "it's the middle of the night"; return false; }
        long gap = Math.Max(1, _cfg.TextFirstHours.Value) * 60L;
        if (book.LastTextFirst >= 0 && now - book.LastTextFirst < gap && now >= book.LastTextFirst) { why = "someone texted you a while ago"; return false; }
        string? talking = _session?.Record.Id;
        var candidates = book.Contacts()
            .Where(c => !KnownUnder18(c.Id) && c.Id != talking && !PhonePending.ContainsKey(c.Id) && book.PlanOf(c.Id).plan.Length == 0)
            .Where(c => ContactInfo.TryGetValue(c.Id, out var k) && (k.Kind is "friend" or "partner" || (c.Id == _avaId && (_story?.OwnLines(c.Id).Count ?? 0) >= StoryBook.SheetAfter)))
            .Where(c => { long last = book.LastMinute(c.Id); return last < 0 || now - last >= 180 || now < last; })
            .ToList();
        if (candidates.Count == 0)
        {
            // Phone pass PH-A6: who they are to you is read when the phone first needs it (not only once it's opened);
            // review PH-R1: someone whose file can't be read is tried again only every few minutes.
            var missing = book.Contacts().Where(c => !ContactInfo.ContainsKey(c.Id)).Select(c => c.Id).Where(TryContactNow).ToList();
            if (missing.Count > 0) LoadContactInfo();
            why = "no friend with your number free to text";
            return false;
        }
        var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        var partners = candidates.Where(c => ContactInfo[c.Id].Kind == "partner").ToList();
        if (partners.Count > 0 && UnityEngine.Random.value < 0.5f) pick = partners[UnityEngine.Random.Range(0, partners.Count)];
        book.TextedFirst(now);
        NoteStart("text");
        var brain = ActiveBrain;
        var snap = GameContext.Capture(null);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        int generation = _resetGeneration;
        string id = pick.Id;
        Task.Run(async () =>
        {
            try
            {
                List<string>? texts;
                string? error;
                if (id == _avaId) (texts, _, error) = await brain.AvaTextAsync(id, "Ava", book.TextsForAi(id), snap, first: true, false, CancellationToken.None).ConfigureAwait(false);
                else (texts, _, error) = await brain.TextAsync(id, book.TextsForAi(id), snap, first: true, CancellationToken.None).ConfigureAwait(false);
                if (texts == null || generation != _resetGeneration) { if (error != null) Log.Debug("text first: " + error); return; }
                foreach (var t in texts)
                {
                    if (generation != _resetGeneration) return;
                    string body = t;
                    MainThread.Enqueue(() => TextArrived(id, body, generation));
                    await Task.Delay(1200).ConfigureAwait(false);
                }
            }
            catch (Exception e) { Log.Debug("text first: " + e.Message); }
        });
        return true;
    }

    // ------------------------------------------------------------------ Ava

    /// <summary>Ava's story id, once the game has shown her (StoryBook); null before.</summary>
    private static string? _avaId;
    private static bool _avaPortraitTried;

    /// <summary>
    /// Ava (the author: "your first contact that should always appear is Ava"): your Automata companion, in your HUD. Once
    /// the story has shown her she's in your contacts, first, with her own portrait; texting her needs no signal.
    /// </summary>
    private static void EnsureAva()
    {
        if (_phone == null || _story == null || !Features.StoryMemory) return;
        _avaId ??= _story.IdNamed("Ava");
        if (_avaId == null || KnownUnder18(_avaId)) return;
        if (!_phone.Has(_avaId)) _phone.Add(_avaId, "Ava", Math.Max(0, GameContext.Day()), "", "your HUD");
        ContactInfo[_avaId] = new Known { Tag = "", Kind = "ava", Bond = 50, Their = "her" }; // "In your HUD" says it (no tag as well)
        if (!_avaPortraitTried)
        {
            _avaPortraitTried = true;
            try
            {
                foreach (var person in Resources.FindObjectsOfTypeAll<Nivalis.GhostSystem.Ai.Person>())
                {
                    if (person == null || !string.Equals(person.DisplayedName ?? "", "Ava", StringComparison.OrdinalIgnoreCase)) continue;
                    var sprite = person.overridePortrait ?? person.Portrait;
                    if (sprite != null) { Portraits[_avaId] = sprite; break; }
                }
            }
            catch (Exception e) { Log.Debug("Ava's portrait: " + e.Message); }
        }
    }

    /// <summary>
    /// Your story conversations with her, mirrored into her thread as texts (hers and your picks), only what the game has
    /// shown you (StoryBook): never a line from the game's files you haven't reached.
    /// </summary>
    private static void MirrorAva()
    {
        if (_phone == null || _story == null || _avaId == null || !Features.StoryMemory) return;
        var lines = AvaStoryLines();
        if (lines.Count > 0) _phone.MirrorStory(_avaId, lines);
    }

    /// <summary>Your story conversations with her, oldest first, as the phone mirrors them (hers and your picks).</summary>
    private static List<(bool, string, int, string, long)> AvaStoryLines()
    {
        var lines = new List<(bool, string, int, string, long)>();
        if (_story == null || _avaId == null) return lines;
        foreach (var scene in _story.Scenes(_avaId))
        {
            int h = 0, m = 0;
            var parts = (scene.Clock ?? "").Split(':');
            if (parts.Length >= 2) { int.TryParse(parts[0], out h); int.TryParse(parts[1], out m); }
            long minute = Mind.GameClock.At(Math.Max(0, scene.GameDay), h, m);
            foreach (var l in scene.Lines.Where(l => l.Who is StoryBook.Them or StoryBook.You))
                lines.Add((l.Who == StoryBook.Them, l.Text, scene.GameDay, scene.Clock ?? "", minute));
        }
        return lines;
    }

    /// <summary>
    /// Ava's story line as it's shown, with its recording: kept as a voice note for the mirrored line (her real voice; the
    /// author: "including her actual voice files (when available)"). At most a minute; nothing when it can't be read.
    /// </summary>
    private static void AvaLineVoiced(string id, string name, string text, Func<AudioClip?> clipOf)
    {
        if (!Features.Phone || _phone == null || !string.Equals(name, "Ava", StringComparison.OrdinalIgnoreCase) || _phone.HasStoryVoice(text)) return;
        _avaId ??= id;
        CaptureAvaLine(id, text, clipOf, 0, _resetGeneration);
    }

    /// <summary>
    /// FB-74 (her story lines had no voice): the game loads a line's clip as it shows it, so a clip still loading is looked
    /// at again every quarter second for three seconds, never loaded by us (review PH-8: how the game loads audio is its own).
    /// </summary>
    private static void CaptureAvaLine(string id, string text, Func<AudioClip?> clipOf, int attempt, int generation)
    {
        if (generation != _resetGeneration || _phone == null || _phone.HasStoryVoice(text)) return;
        try
        {
            var clip = clipOf();
            if (clip == null || clip.loadState != AudioDataLoadState.Loaded)
            {
                if (clip != null && clip.loadState == AudioDataLoadState.Failed) { Log.Debug("Ava's voice: the game couldn't load this line's audio."); return; }
                if (attempt < 12) Later(0.25f, () => CaptureAvaLine(id, text, clipOf, attempt + 1, generation));
                else Log.Debug("Ava's voice: this line's audio wasn't loaded in time (" + (clip == null ? "no clip" : clip.loadState.ToString()) + ").");
                return;
            }
            int channels = Math.Max(1, clip.channels), frames = Math.Min(clip.samples, clip.frequency * 20);
            if (frames <= 0) return;
            var data = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(frames * channels);
            if (!clip.GetData(data, 0)) return;
            var samples = new float[frames * channels];
            // One copy of the array's data: an IL2CPP array is 4 pointers (class, monitor, bounds, length), then its elements (as Il2CppInterop's own ArrayStartPointer, which is internal).
            System.Runtime.InteropServices.Marshal.Copy(data.Pointer + 4 * IntPtr.Size, samples, 0, samples.Length);
            int rate = clip.frequency;
            string dir = VoiceDir, book = id;
            var phone = _phone;
            var pending = HeldVoices();
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string file = $"ava-{Guid.NewGuid():N}.wav";
                    File.WriteAllBytes(Path.Combine(dir, file), VoiceNote.ToWav(samples, rate, channels));
                    phone.StoryVoice(book, text, file, samples.Length / (float)(rate * channels), VoiceNote.WaveText(VoiceNote.Wave(samples)));
                    if (!_saidAvaVoice) { _saidAvaVoice = true; Log.Info("Story: Ava's recorded lines are kept on your phone, with her voice."); }
                    var inUse = phone.VoiceFiles();
                    VoiceNote.Prune(dir, 600, "ava-*.wav", n => inUse.Contains(n) || pending.Contains(n, StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception e) { Log.Debug("Ava's voice: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Debug("Ava's voice: " + e.Message); }
    }

    private static bool _saidAvaVoice;

    // ------------------------------------------------------------------ the number, in a chat

    /// <summary>A chat's snapshot: their number (made the first time), whether you have it, and your last texts with them.</summary>
    private static void FillPhone(GameSnapshot snap, string id)
    {
        if (!Features.Phone || _phone == null || KnownUnder18(id)) return;
        snap.MyNumber = _phone.NumberOf(id);
        snap.HasYourNumber = _phone.Has(id);
        snap.RecentTexts.Clear();
        if (snap.HasYourNumber)
            foreach (var t in _phone.Texts(id).TakeLast(6))
                snap.RecentTexts.Add($"Day {t.GameDay}, {TwelveHour(t.Clock)}, {(t.From == PhoneBook.You ? "they texted you" : "you texted them")}: {Ai.ReplyParser.Quotable(t.Body, 160)}");
    }

    /// <summary>They gave you their number (the AI's call): saved, and a line under their reply says so (and how to open the phone).</summary>
    private static void NoteNumber(NpcRecord rec, NpcReply reply)
    {
        if (!reply.GaveNumber || !Features.Phone || _phone == null || KnownUnder18(rec.Id) || _session == null) return;
        bool had = _phone.Has(rec.Id);
        try { var portrait = _npc?.Portrait; if (portrait != null) Portraits[rec.Id] = portrait; } catch { } // their avatar in Contacts
        string name = rec.Persona.Name.Length > 0 ? rec.Persona.Name : SpeakerName(rec);
        var c = _phone.Add(rec.Id, name, _session.Snapshot.GameDay, _session.Snapshot.Clock, _session.Snapshot.Location);
        if (c == null || had) return;
        LoadContactInfo(rec.Id);
        Ui.AddAfterReveal(LineKind.Notice, $"{Prompts.FirstName(name)}'s number is in your Contacts · {PhoneKeyName} opens your phone after the chat");
        Log.Info($"The phone: {Prompts.FirstName(name)} gave you their number.");
    }

    // ------------------------------------------------------------------ settings (the cfg is the store; Undo lasts until the next change)

    private static Action? _undo, _undoCommit;
    private static string _undoText = "", _undoScreen = "";

    private static void CommitUndo()
    {
        var commit = _undoCommit;
        _undo = null; _undoCommit = null;
        PhoneUi.View.Undo = "";
        try { commit?.Invoke(); } catch { }
    }

    private static void SetUndo(string text, string screen, Action undo, Action? commit = null)
    {
        CommitUndo();
        _undo = undo; _undoCommit = commit; _undoText = text; _undoScreen = screen;
    }

    private static void WithUndo(string text, string screen, BepInEx.Configuration.ConfigEntry<string> entry, string value)
    {
        string before = entry.Value;
        entry.Value = value;
        SetUndo(text, screen, () => entry.Value = before);
        Signal(force: true);
        RefreshPhone();
    }

    private static void PhoneToggle(string key, bool on)
    {
        CommitUndo();
        switch (key)
        {
            case "signal": _cfg.PhoneSignal.Value = on; break;
            case "weather": _cfg.PhoneWeather.Value = on; break;
            case "places": _cfg.PhonePlaces.Value = on; break;
            case "texts": _cfg.PeopleTextYou.Value = on; break;
            case "real": _cfg.TextsRealLife.Value = on; break;
            case "voice": _cfg.TextVoiceNotes.Value = on; break;
            case "ava": _cfg.AvaVoice.Value = on; break;
        }
        Signal(force: true);
        RefreshPhone();
    }

    private static void PhoneStep(string key, int delta)
    {
        CommitUndo();
        if (key.StartsWith("cost:", StringComparison.Ordinal))
        {
            var costs = Reception.ParseCosts(_cfg.PhoneWeatherCosts.Value);
            string kind = key[5..];
            costs.Set(kind, costs.Get(kind) + delta);
            _cfg.PhoneWeatherCosts.Value = costs.ToString();
        }
        else if (key == "indoors") _cfg.PhoneIndoorsCost.Value = Math.Clamp(_cfg.PhoneIndoorsCost.Value + delta, 0, 2);
        else if (key == "never") _cfg.TextNeverSees.Value = Math.Clamp(_cfg.TextNeverSees.Value + delta * 5, 0, 50);
        else if (key == "later") _cfg.TextAnswersLater.Value = Math.Clamp(_cfg.TextAnswersLater.Value + delta * 5, 0, 50);
        else if (key == "voicechance") _cfg.VoiceNoteChance.Value = Math.Clamp(_cfg.VoiceNoteChance.Value + delta * 10, 0, 100);
        else if (key == "here" && _here.place.Length > 0)
        {
            var listed = Reception.ParsePlaces(_cfg.PhoneWeakPlaces.Value).FirstOrDefault(p => string.Equals(p.place, _here.place, StringComparison.OrdinalIgnoreCase));
            SetPlaceBars(_here.place, (listed.place != null ? listed.bars : Reception.Full) + delta, undo: false);
            return;
        }
        else if (key.StartsWith("place:", StringComparison.Ordinal))
        {
            string place = key[6..];
            var listed = Reception.ParsePlaces(_cfg.PhoneWeakPlaces.Value).FirstOrDefault(p => p.place == place);
            if (listed.place != null) SetPlaceBars(place, listed.bars + delta, undo: false);
            return;
        }
        Signal(force: true);
        RefreshPhone();
    }

    private static void SetPlaceBars(string place, int bars, bool undo)
    {
        if (place.Length == 0) return;
        var list = Reception.ParsePlaces(_cfg.PhoneWeakPlaces.Value);
        list.Add((place, Math.Clamp(bars, 0, Reception.Full)));
        _cfg.PhoneWeakPlaces.Value = Reception.WritePlaces(list);
        Signal(force: true);
        RefreshPhone();
    }

    private static void RemovePhonePlace(string place)
    {
        var list = Reception.ParsePlaces(_cfg.PhoneWeakPlaces.Value);
        string kept = Reception.WritePlaces(list.Where(p => p.place != place));
        WithUndo($"{place} taken off the list.", "places", _cfg.PhoneWeakPlaces, kept);
    }

    // ------------------------------------------------------------------ the wallpaper (kept on the PC, not per save)

    private static bool _wallBusy, _wallError, _wallKept;
    private static string _wallNote = "";
    private static int _wallSerial;

    private static string WallDir => Path.Combine(_dataDir, "phone-wallpaper");

    private static void LoadWallpaperSoon()
    {
        string dir = WallDir;
        Task.Run(() =>
        {
            var kept = Wallpaper.Load(dir);
            if (kept is { } k) ShowWallpaper(k.data, k.info.Kind, k.info.Url, fresh: false, serial: _wallSerial);
        });
    }

    private static void SetWallpaperLink(string link)
    {
        CommitUndo();
        var (uri, error) = Wallpaper.Check(link);
        if (uri == null) { WallState(error, isError: true); return; }
        int serial = ++_wallSerial;
        _wallBusy = true;
        WallState("Downloading…", isError: false);
        string dir = WallDir;
        Task.Run(async () =>
        {
            long shown = 0;
            var (data, err) = await Wallpaper.DownloadAsync(WallHttp, uri, (read, total) =>
            {
                if (read - shown < 256 * 1024) return;
                shown = read;
                string note = total > 0 ? $"Downloading… {read / 1048576.0:0.0} of {total / 1048576.0:0.0} MB" : $"Downloading… {read / 1048576.0:0.0} MB";
                MainThread.Enqueue(() => { if (serial == _wallSerial) WallState(note, isError: false); });
            }, CancellationToken.None).ConfigureAwait(false);
            if (serial != _wallSerial) return;
            if (data == null) { MainThread.Enqueue(() => { _wallBusy = false; WallState(err, isError: true); }); return; }
            string kind = Wallpaper.Sniff(data);
            if (!Wallpaper.Readable(kind)) { MainThread.Enqueue(() => { _wallBusy = false; WallState(Wallpaper.Unreadable(kind), isError: true); }); return; }
            ShowWallpaper(data, kind, uri.ToString(), fresh: true, serial: serial, dir: dir);
        });
    }

    /// <summary>
    /// Reads the picture (a GIF or BMP decoded here, on a pool thread; PNG and JPG by Unity, on the main thread) and shows
    /// it; a fresh download is kept on the PC first. A picture that can't be read changes nothing.
    /// </summary>
    private static void ShowWallpaper(byte[] data, string kind, string url, bool fresh, int serial, string? dir = null)
    {
        try
        {
            GifDecoder.Image? gif = null;
            (int w, int h, byte[] rgba)? bmp = null;
            if (kind == "gif") gif = GifDecoder.Decode(data, maxSide: 720, maxFrames: 150, bottomUp: true);
            else if (kind == "bmp") bmp = BmpDecoder.Decode(data);
            else if (!Wallpaper.SizeOk(Wallpaper.Dimensions(data, kind)))
            {
                // Review PH-1: Unity would make the whole texture before its size could be checked.
                MainThread.Enqueue(() => { if (serial != _wallSerial) return; _wallBusy = false; WallState($"That picture is too big ({Wallpaper.MaxSide} pixels a side at most). Your wallpaper didn't change.", isError: true); });
                return;
            }
            MainThread.Enqueue(() =>
            {
                if (serial != _wallSerial) return;
                try
                {
                    if (gif != null) PhoneUi.SetGif(gif.Width, gif.Height, gif.Frames.Select(f => (f.Pixels, f.DelayMs)).ToList());
                    else if (bmp is { } b)
                    {
                        var tex = new Texture2D(b.w, b.h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                        tex.LoadRawTextureData(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(b.rgba));
                        tex.Apply(false, false);
                        PhoneUi.SetStill(tex);
                    }
                    else
                    {
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                        if (!ImageConversion.LoadImage(tex, data, false) || tex.width < 2 || tex.width > 8192 || tex.height > 8192)
                        {
                            UnityEngine.Object.Destroy(tex);
                            _wallBusy = false;
                            WallState(Wallpaper.Unreadable(""), isError: true);
                            return;
                        }
                        PhoneUi.SetStill(tex);
                    }
                    _wallBusy = false;
                    _wallKept = true;
                    string what = kind.ToUpperInvariant() + (gif != null && gif.Frames.Count > 1 ? $", {gif.Frames.Count} frames" : "");
                    WallState($"Saved on this PC · {what}", isError: false);
                    if (fresh)
                    {
                        _cfg.PhoneWallpaper.Value = url;
                        PhoneUi.SetLinkText(url);
                        // Review PH-2: kept only once it showed, and only if it's still the latest (not after Default or another link).
                        int keptFor = serial;
                        if (dir != null) Task.Run(() => { try { if (keptFor == _wallSerial) Wallpaper.Keep(dir, data, kind, url); } catch (Exception e) { Log.Warn("The phone's wallpaper couldn't be kept: " + e.Message); } });
                    }
                }
                catch (Exception e) { _wallBusy = false; WallState(Wallpaper.Unreadable(""), isError: true); Log.Debug("wallpaper: " + e.Message); }
            });
        }
        catch (FormatException)
        {
            MainThread.Enqueue(() => { if (serial != _wallSerial) return; _wallBusy = false; WallState(Wallpaper.Unreadable(""), isError: true); });
        }
        catch (Exception e)
        {
            Log.Warn("The phone's wallpaper couldn't be kept: " + e.Message);
            MainThread.Enqueue(() => { if (serial != _wallSerial) return; _wallBusy = false; WallState("Couldn't save that picture on this PC. Your wallpaper didn't change.", isError: true); });
        }
    }

    private static void WallState(string note, bool isError)
    {
        _wallNote = note;
        _wallError = isError;
        if (_phoneOpen) RefreshPhone();
    }

    private static void WallpaperToDefault()
    {
        CommitUndo();
        string dir = WallDir, link = _cfg.PhoneWallpaper.Value;
        int serial = ++_wallSerial;
        Wallpaper.Stash(dir);
        PhoneUi.SetStill(null);
        _wallKept = false;
        _wallNote = "";
        _wallError = false;
        _cfg.PhoneWallpaper.Value = "";
        PhoneUi.SetLinkText("");
        SetUndo("Back to the default wallpaper. The saved picture goes when you leave this screen.", "settings",
            undo: () => { Wallpaper.Unstash(dir); _cfg.PhoneWallpaper.Value = link; PhoneUi.SetLinkText(link); LoadWallpaperSoon(); },
            commit: () => Wallpaper.DropStash(dir));
        RefreshPhone();
    }

    // ------------------------------------------------------------------ /phone

    private static void PhoneCommand(Action<string> say)
    {
        if (!Features.Phone) { say("The phone is off (Features > Phone)."); return; }
        Signal(force: true);
        var w = GameContext.WeatherNumbers();
        say($"Signal: {_bars} bar{(_bars == 1 ? "" : "s")}{(_barsWhy.Length > 0 ? $" ({_barsWhy})" : "")}. {(_cfg.PhoneSignal.Value ? "" : "Not simulated (PhoneSignal off). ")}");
        say($"Here: district '{_here.place}', scene '{_here.scene}'{(_inApartment == 2 ? ", at home" : _inApartment == 1 ? ", in someone's apartment" : _indoors ? ", indoors (a roof overhead)" : ", outdoors")}. A PhoneWeakPlaces name matches any of these.");
        say($"Weather: blizzard {w.blizzard:0.00}, snow {w.snow:0.00}, rain {w.rain:0.00}, thunder {w.thunder:0.00}, fog {w.fog:0.00}.");
        if (_phone != null) say($"Contacts: {_phone.Count}. Unread texts: {_phone.UnreadTotal()}. Your number: {_phone.MyNumber}. The phone key: {PhoneKeyName}.");
    }
}
