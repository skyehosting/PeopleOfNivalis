using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Npc;

/// <summary>One thing a shop keeper has for sale, as the game has it right now (FB-57).</summary>
public sealed class ShopItem
{
    public string Name = "";
    /// <summary>What it costs the player, in Lims; 0 when unknown.</summary>
    public int Price;
    public int Stock, MaxStock;
    /// <summary>"short" (a shortage at the market: prices up), "plenty" (a surplus: cheap) or "".</summary>
    public string Market = "";
}

/// <summary>The shop or stall a person runs, as the game has it right now (FB-57). No Unity here.</summary>
public sealed class ShopFacts
{
    public string Name = "", Trade = "", Place = "";
    public List<ShopItem> Items = new();
    /// <summary>Today's deal: the item that's cheaper today ("" if none).</summary>
    public string Deal = "";
    /// <summary>How well they know the player as a customer, 0 to 1 (the game's own); -1 when unknown.</summary>
    public double Regular = -1;
}

/// <summary>
/// FB-57: a shop keeper knows what they sell: their trade, what they have in stock and at what price, what's sold out,
/// today's deal, what the market's short of or swimming in, and whether the player is a regular. Told in the chat as facts.
/// </summary>
public static class Shops
{
    /// <summary>Items told at most (the rest is "and more"): a vendor names what's good today, not the whole list.</summary>
    public const int MaxItems = 12;

    /// <summary>An item's name as people say it: an asset-style name ("Item_FreshTomato", "fish_mackerel") made readable.</summary>
    public static string Humanize(string raw)
    {
        string s = (raw ?? "").Trim();
        s = Regex.Replace(s, @"^(?:item|itm|food|ingredient)[_\-\s]+", "", RegexOptions.IgnoreCase);
        s = s.Replace('_', ' ').Replace('-', ' ');
        s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return "";
        bool shouty = s.All(ch => !char.IsLetter(ch) || char.IsUpper(ch));
        if (shouty || s.All(ch => !char.IsLetter(ch) || char.IsLower(ch))) s = s.ToLowerInvariant();
        return s;
    }

    public static string StockWord(int stock, int max) =>
        stock <= 0 ? "sold out" : max > 0 && stock * 4 <= max ? "running low" : "in stock";

    /// <summary>The player as their customer, in words; "" when they've never bought from them (or it's unknown).</summary>
    public static string Customer(double regular) =>
        regular <= 0 ? "" : regular < 0.34 ? "they've bought from you a few times" : regular < 0.67 ? "they're a regular of yours" : "they're one of your best customers";

    /// <summary>
    /// FB-57: someone the game names on screen (or who runs a shop) is known by that name and has that job. One met before
    /// under an invented identity takes the game's name and trade and keeps what they remember (the author's choice); the
    /// old name is swapped in their summary and background so their memories still read as theirs. Returns what changed
    /// ("" if nothing).
    /// </summary>
    public static string KnownByShownName(NpcRecord rec, NpcVisual seen)
    {
        var v = rec.Visual ??= new NpcVisual();
        var p = rec.Persona ??= new Persona();
        var changed = new List<string>();
        v.NameShown = seen.NameShown; // as the game shows it now, not kept once it stops (review F1)
        if (seen.ShopName.Length > 0) v.ShopName = seen.ShopName;
        if (seen.Trade.Length > 0) v.Trade = seen.Trade;
        string name = (seen.GameName ?? "").Trim();
        if (v.NameShown && name.Length > 0)
        {
            v.GameName = name;
            string old = (p.Name ?? "").Trim();
            if (!string.Equals(old, name, StringComparison.Ordinal))
            {
                p.Name = name;
                if (old.Length > 0)
                {
                    if ((p.FormerName ?? "").Length == 0) p.FormerName = old; // kept, so a wrong rename can be undone (review F1)
                    p.Background = Swap(Swap(p.Background, old, name), First(old), First(name));
                    rec.MemorySummary = Swap(Swap(rec.MemorySummary, old, name), First(old), First(name));
                    changed.Add($"known by the name the game shows, {name} (was {old})");
                }
            }
            rec.State.NameRevealed = true;
            rec.State.KnownAs = name;
        }
        if (v.ShopName.Length > 0 || v.Trade.Length > 0)
        {
            string job = $"runs {(v.ShopName.Length > 0 ? v.ShopName : "a stall")}{(v.Trade.Length > 0 ? $" ({v.Trade.ToLowerInvariant()})" : "")}";
            if (!string.Equals(p.Occupation, job, StringComparison.Ordinal))
            {
                if ((p.Occupation ?? "").Length > 0) changed.Add($"their job is their shop ({job}; was {p.Occupation})");
                p.Occupation = job;
                if (v.ShopName.Length > 0) p.Workplace = v.ShopName;
            }
        }
        return string.Join("; ", changed);
    }

    private static string First(string name) => (name ?? "").Trim().Split(' ')[0];

    /// <summary>
    /// <paramref name="from"/> swapped for <paramref name="to"/> as a whole word only (review F2: "Sam" inside "Samantha"
    /// stays), and never a name under 3 letters.
    /// </summary>
    private static string Swap(string? text, string from, string to)
    {
        text ??= "";
        if (from.Length < 3 || from == to || text.Length == 0) return text;
        return Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(from)}(?![\p{{L}}\p{{N}}])", _ => to);
    }

    /// <summary>The chat's [YOUR SHOP]: what they run and sell right now. Empty when there's no shop.</summary>
    public static List<string> Lines(ShopFacts? shop)
    {
        var lines = new List<string>();
        if (shop == null || (shop.Name.Length == 0 && shop.Trade.Length == 0 && shop.Items.Count == 0)) return lines;
        string what = shop.Name.Length > 0 ? shop.Name : "your stall";
        string trade = shop.Trade.Length > 0 ? $", selling {shop.Trade.ToLowerInvariant()}" : "";
        string where = shop.Place.Length > 0 ? $" at {shop.Place}" : "";
        lines.Add($"You run {what}{trade}{where}: it's your job, and you're at it now (a fact, never contradict it).");
        var have = shop.Items.Where(i => i.Name.Length > 0 && i.Stock > 0)
            .OrderByDescending(i => i.Name == shop.Deal).ThenByDescending(i => i.Stock).ToList();
        var gone = shop.Items.Where(i => i.Name.Length > 0 && i.Stock <= 0).Select(i => i.Name).ToList();
        if (have.Count > 0)
        {
            string Item(ShopItem i) =>
                i.Name + (i.Price > 0 ? $" {i.Price} Lims" : "") + (StockWord(i.Stock, i.MaxStock) == "running low" ? ", running low" : "");
            lines.Add($"For sale right now: {string.Join("; ", have.Take(MaxItems).Select(Item))}{(have.Count > MaxItems ? "; and more" : "")}.");
        }
        else if (shop.Items.Count > 0) lines.Add("You're sold out of everything right now.");
        if (gone.Count > 0) lines.Add($"Sold out today: {string.Join(", ", gone.Take(6))}.");
        if (shop.Deal.Length > 0) lines.Add($"Today's deal: {shop.Deal}, cheaper than usual today.");
        var shortItems = shop.Items.Where(i => i.Market == "short").Select(i => i.Name).ToList();
        var plenty = shop.Items.Where(i => i.Market == "plenty").Select(i => i.Name).ToList();
        if (shortItems.Count > 0) lines.Add($"The market's short of {string.Join(", ", shortItems.Take(4))} right now, so it costs more.");
        if (plenty.Count > 0) lines.Add($"There's plenty of {string.Join(", ", plenty.Take(4))} at the market right now, so it's cheap.");
        if (Customer(shop.Regular) is { Length: > 0 } c) lines.Add($"The person in front of you: {c}.");
        lines.Add("Asked what you sell, answer like a vendor would: what's good today, a price or two, the deal if there is one. Never read out the whole list.");
        return lines;
    }
}
