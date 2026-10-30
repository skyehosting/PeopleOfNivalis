using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-65: plans and reminders made in a chat (the AI's reply's reminder field), kept per save (Social/Reminders.cs),
/// shown when due as the game's own pop-up (Game/GameToast.cs), or the mod's own card when the game's can't be used.
/// The person you made a plan with knows it until it's past (their prompt's [PLANS WITH THE STRANGER]).
/// </summary>
public static partial class ModRuntime
{
    private static ReminderBook? _reminders;
    private static float _nextReminderCheck;
    private static List<string> _plans = new();

    /// <summary>A reply asked for a plan or a reminder: kept, and said in the chat.</summary>
    private static void NoteReminder(NpcRecord rec, ReplyExtras extras)
    {
        if (!Features.Reminders || _reminders == null || extras.ReminderWhat.Length == 0) return;
        int today = GameContext.Day();
        var r = _reminders.Add(extras.ReminderWhat, rec.Id, DisplayName(rec), today, extras.ReminderInDays, extras.ReminderAtHour, extras.ReminderPlace);
        if (r == null) return;
        try { Ui.AddLine(LineKind.Notice, "", $"Plan noted: {r.What}, {ReminderBook.When(r, today)}."); } catch { }
        Log.Info($"Reminders: '{r.What}' with {(r.WhoName.Length > 0 ? r.WhoName : "nobody")}, {ReminderBook.When(r, today)}.");
        _plans = PlansWith(rec.Id); // this chat knows at once
    }

    /// <summary>Every few seconds of play: anything due shows once.</summary>
    private static void TickReminders()
    {
        try { TickPlans(); } catch (Exception e) { Log.Debug("plans: " + e.Message); } // the inner-life round: meet-ups happen (settled before they'd be let go below)
        if (!Features.Reminders || _reminders == null || _resetting || Time.unscaledTime < _nextReminderCheck) return;
        _nextReminderCheck = Time.unscaledTime + 3f;
        if (!InGameplay() && !ChatOpen) return;
        int day = GameContext.Day(), hour = GameContext.Hour();
        if (day < 0) return;
        List<string> where = new();
        // Review F11: where you are is only looked up when a reminder is about a place (CF-20: the venue you're in too).
        if (_reminders.NeedsPlace()) try { where = GameContext.PlacesHere(); } catch { }
        foreach (var r in _reminders.Due(day, hour, where))
        {
            // Review F9: the AI's words never carry rich text into the game's pop-up.
            string title = Plain(r.WhoName.Length > 0 ? $"Plan with {r.WhoName}" : "Reminder");
            string body = Plain($"{Cap(r.What)}{(r.Hour >= 0 ? $" ({r.Hour:00}:00)" : "")}{(r.Place.Length > 0 ? $" · {r.Place}" : "")}");
            try { if (!GameToast.Show(title, body)) Ui.ShowToast(title, body); }
            catch (Exception e) { _reminders.Unshow(r.Id); Log.Debug("reminder toast: " + e.Message); continue; } // shown next time
            Log.Info($"Reminders: due: {title}: {body}");
            try { Api.PonApi.RaiseReminder(r.What); } catch { } // FB-67
            try { SpeakUpAboutPlan(r.WhoId, r.What); } catch { } // with you now: they bring it up
        }
    }

    /// <summary>The plans made with someone, for their prompt (they know what you agreed).</summary>
    private static List<string> PlansWith(string id)
    {
        if (!Features.Reminders || _reminders == null || id.Length == 0) return new List<string>();
        int today = GameContext.Day();
        // Review F12: the plan's words quoted, with no brackets that could read as the prompt's own sections.
        return _reminders.With(id, today).Select(r => $"\"{Bare(r.What)}\", {Bare(ReminderBook.When(r, today))}{HowItWent(r)}").ToList();
    }

    /// <summary>The inner-life round: a meet-up's outcome in their prompt ("you met, as planned", "they never came").</summary>
    private static string HowItWent(ReminderBook.Reminder r) => r.Outcome switch
    {
        Mind.Meetups.Met => " (you met, as planned)",
        Mind.Meetups.StoodUp => " (they never came)",
        Mind.Meetups.Flaked => " (you didn't make it)",
        _ => r.Shown ? " (that's now or past)" : "",
    };

    /// <summary>No rich-text tags (the game's pop-up and the mod's card show TextMeshPro text).</summary>
    private static string Plain(string s) => s.Replace('<', '(').Replace('>', ')');

    /// <summary>A plan's words for the prompt: no brackets, no quotes, one line.</summary>
    private static string Bare(string s) =>
        new string(s.Where(ch => ch is not ('[' or ']' or '{' or '}' or '<' or '>' or '"' or '\n' or '\r')).ToArray()).Trim();

    /// <summary>/reminders: what's coming up; /reminders clear empties the list.</summary>
    private static void RemindersCommand(string arg, Action<string> say)
    {
        if (_reminders == null) { say("Reminders aren't available this session (the log says why)."); return; }
        if (arg.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase)) { say($"{_reminders.Clear()} reminders cleared."); return; }
        int today = GameContext.Day();
        var all = _reminders.All().Where(r => !r.Shown).OrderBy(r => r.Day).ThenBy(r => r.Hour).ToList();
        if (all.Count == 0) { say("No plans or reminders coming up. Make one in a chat: \"remind me to pay the rent tomorrow\", \"meet me at the market at 8\"."); return; }
        foreach (var r in all.Take(15)) say($"  {r.What}{(r.WhoName.Length > 0 ? $" (with {r.WhoName})" : "")}: {ReminderBook.When(r, today)}");
        say("/reminders clear empties the list.");
    }
}
