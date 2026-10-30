using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;

namespace NivalisAIChat;

/// <summary>
/// From RiftSpeak's list (the author: "a memory list with single deletes"): in a conversation, /memories lists what
/// this person keeps about you (the meetings in their diary, the facts they know about you, what they found out), and
/// /forget N takes one of them out of their memory for good. The memory index follows (it's built from their record).
/// </summary>
public static partial class ModRuntime
{
    private sealed class MemoryItem
    {
        public string Kind = "", Text = "";
        public int Day = -1;
    }

    /// <summary>The last list shown, so /forget N removes what was numbered N.</summary>
    private static List<MemoryItem> _memoryListed = new();
    private static string _memoryListedFor = "";

    private static List<MemoryItem> MemoryItems(NpcRecord rec)
    {
        var items = new List<MemoryItem>();
        foreach (var m in rec.Meetings.AsEnumerable().Reverse()) items.Add(new MemoryItem { Kind = "meeting", Text = m.Summary, Day = m.GameDay });
        foreach (var f in rec.State.PlayerFacts.AsEnumerable().Reverse()) items.Add(new MemoryItem { Kind = "fact", Text = f });
        foreach (var h in rec.State.Hurts.AsEnumerable().Reverse()) items.Add(new MemoryItem { Kind = "hurt", Text = h });
        return items.Where(i => i.Text.Trim().Length > 0).ToList();
    }

    private static void MemoriesCommand()
    {
        if (_session == null) { Ui.AddLine(LineKind.Notice, "", "/memories works in a conversation: talk to someone, then type it there."); return; }
        var rec = _session.Record;
        var items = MemoryItems(rec);
        _memoryListed = items;
        _memoryListedFor = rec.Id;
        string who = SpeakerName(rec);
        if (items.Count == 0) { Ui.AddLine(LineKind.Notice, "", $"{who} keeps nothing about you yet."); return; }
        Ui.AddLine(LineKind.Notice, "", $"What {who} keeps about you (newest first). /forget N takes one out of their memory:");
        for (int i = 0; i < items.Count && i < 40; i++)
        {
            var it = items[i];
            string kind = it.Kind switch { "meeting" => it.Day >= 0 ? $"day {it.Day}" : "a meeting", "fact" => "about you", _ => "found out" };
            Ui.AddLine(LineKind.Notice, "", $"{i + 1}. ({kind}) {Cut(it.Text, 180)}");
        }
        if (items.Count > 40) Ui.AddLine(LineKind.Notice, "", $"…and {items.Count - 40} older ones.");
    }

    private static void ForgetCommand(string arg)
    {
        if (_session == null) { Ui.AddLine(LineKind.Notice, "", "/forget works in a conversation, after /memories."); return; }
        var rec = _session.Record;
        if (_memoryListedFor != rec.Id || _memoryListed.Count == 0) { Ui.AddLine(LineKind.Notice, "", "Type /memories first, then /forget and the number."); return; }
        if (!int.TryParse(arg.Trim(), out int n) || n < 1 || n > _memoryListed.Count) { Ui.AddLine(LineKind.Notice, "", $"/forget takes a number from 1 to {_memoryListed.Count}."); return; }
        var it = _memoryListed[n - 1];
        bool Remove(NpcRecord r)
        {
            switch (it.Kind)
            {
                case "meeting":
                    int i = r.Meetings.FindLastIndex(m => m.Summary == it.Text && m.GameDay == it.Day);
                    if (i < 0) return false;
                    r.Meetings.RemoveAt(i);
                    return true;
                case "fact": return r.State.PlayerFacts.Remove(it.Text);
                default: return r.State.Hurts.Remove(it.Text);
            }
        }
        // On the record this chat holds (so the rest of the chat forgets it too), and on its saved copy.
        bool done = Remove(rec);
        _ = ActiveBrain.Mutate(rec.Id, r => { Remove(r); return true; });
        _memoryListed.RemoveAt(n - 1);
        Ui.AddLine(LineKind.Notice, "", done ? $"{SpeakerName(rec)} no longer remembers: {Cut(it.Text, 120)}" : "That one was already gone.");
        Log.Info($"/forget: {rec.Persona.Name} forgot a {it.Kind}.");
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
