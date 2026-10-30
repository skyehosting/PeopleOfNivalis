using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Game;

namespace NivalisAIChat.Npc;

/// <summary>
/// What the player has learned about someone: only what they told you (true or not) plus what the game itself
/// shows you (their face's gender presentation, a named character's bio, your staff's workplace).
/// </summary>
public sealed class InfoSheet
{
    public sealed class Row
    {
        public string Label = "";
        public string Value = "";
        public bool Known;
    }

    public string Title = "";
    public List<Row> Rows = new();
    public List<(string heading, List<string> items)> Sections = new();

    public static InfoSheet Build(NpcRecord rec, GameSnapshot? snap, string displayName, string genderLabel, string moodCue)
    {
        var st = rec.State;
        var sheet = new InfoSheet { Title = displayName };
        string? Told(string topic) => st.Told.LastOrDefault(t => t.Topic == topic)?.Said;
        // CF-25: a remark about the moment isn't shown (saved before the fix too); "other" keeps its newest few.
        List<string> ToldAll(params string[] topics)
        {
            var items = st.Told.Where(t => topics.Contains(t.Topic) && !ToldTidy.Passing(t.Topic, t.Said)).Select(t => t.Said).Distinct().ToList();
            return topics.Contains("other") ? items.Skip(Math.Max(0, items.Count - ToldTidy.OtherKept)).ToList() : items;
        }
        void Add(string label, string? value)
        {
            bool known = !string.IsNullOrWhiteSpace(value);
            sheet.Rows.Add(new Row { Label = label, Value = known ? value! : "?", Known = known });
        }

        Add("Name", displayName == "Stranger" ? null : displayName);
        string pronouns = Told("pronouns") ?? (st.IdentityShared ? rec.Persona.Pronouns : null) ?? "";
        string gender = Told("gender") is { } g && !st.IdentityShared ? $"{genderLabel} (says: {g})" : genderLabel;
        Add("Gender", pronouns.Length > 0 && !gender.Contains(pronouns) ? $"{gender} · {pronouns}" : gender);
        Add("Age", Told("age"));
        Add("Orientation", Told("orientation"));
        Add("Relationship", Told("relationship"));

        // Work: what they said, or what the game shows for named characters and your own staff.
        string? work = Told("job");
        string? workplace = Told("workplace");
        if (work != null && workplace != null && !work.Contains(workplace)) work = $"{work} — {workplace}";
        work ??= workplace;
        if (work == null && snap != null && snap.NpcWorkplace.Length > 0 && (rec.Visual.Named || rec.Visual.Employee))
            work = $"{(snap.NpcRoles.Length > 0 ? snap.NpcRoles + " at " : "")}{snap.NpcWorkplace}{(snap.NpcWorksForPlayer ? " (works for you)" : "")}";
        Add("Work", work);
        Add("Lives in", Told("home"));
        Add("Faith", Told("faith"));
        Add("Politics", Told("politics"));
        Add("Education", Told("education"));

        void Section(string heading, List<string> items)
        {
            if (items.Count > 0) sheet.Sections.Add((heading, items));
        }
        Section("Interests", ToldAll("hobbies", "likes")); // CF-20: "Into" in capitals read as a typo of INFO
        Section("Can't stand", ToldAll("dislikes"));
        Section("People in their life", ToldAll("family"));
        Section("Wants", ToldAll("goals"));
        Section("Worries", ToldAll("fears"));
        Section("Their story", ToldAll("background"));
        Section("Body & health", ToldAll("health"));
        Section("Other things they've mentioned", ToldAll("other"));
        if (rec.Visual.Description.Length > 0)
            Section("Your Automata's notes", new List<string> { rec.Visual.Description });

        var between = new List<string>();
        if (Features.Relationships) between.Add($"{Feeling(st.Affinity)} ({st.Relationship:0.#}% on the relationship bar)");
        between.Add(st.TimesMet <= 1 ? "First time you've talked" : $"Talked {st.TimesMet} times");
        if (st.PlayerFacts.Count > 0) between.Add("They know about you: " + string.Join("; ", st.PlayerFacts.TakeLast(6)));
        if (moodCue.Length > 0) between.Add("Right now they " + TheyForm(moodCue));
        Section("Between you", between);
        return sheet;
    }

    /// <summary>Mood cues are written for "he/she" ("looks curious"); this is "they look curious".</summary>
    public static string TheyForm(string cue)
    {
        int sp = cue.IndexOf(' ');
        string verb = sp < 0 ? cue : cue[..sp], rest = sp < 0 ? "" : cue[sp..];
        string plural = verb switch
        {
            "is" => "are",
            "has" => "have",
            "does" => "do",
            "goes" => "go",
            _ when verb.EndsWith("s") && verb.Length > 2 => verb[..^1],
            _ => verb,
        };
        return plural + rest;
    }

    public static string Feeling(int affinity) => affinity switch
    {
        >= 60 => "Considers you a friend",
        >= 30 => "Likes you",
        >= 10 => "Warming up to you",
        > -10 => "No strong feelings about you yet",
        > -30 => "You rub them the wrong way",
        > -60 => "Doesn't like you",
        _ => "Can't stand you",
    };
}
