using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Social;

/// <summary>
/// Where people like to eat, from the town's real venues and menus, and what they make of the prices there. Every
/// person is different: some eat out a lot, some never; some know the menu prices to the Lim, others just "a bit
/// pricey"; frugal people find more things expensive. All of it is stable per person, so a follow-up question gets a
/// consistent answer.
/// </summary>
public static class FoodScene
{
    public sealed class Place
    {
        public string Name = "";
        public string Location = "";
        public bool Players;
        public List<(string Dish, int Price)> Dishes = new();
        public double AveragePrice => Dishes.Count == 0 ? 0 : Dishes.Average(d => d.Price);
    }

    /// <summary>How much this person knows about prices: just an impression, the gist, or the actual numbers.</summary>
    public enum Detail { Vague, Rough, Precise }

    /// <summary>Stable per person: people who follow prices (food workers, practical types: <paramref name="factor"/>) know more exactly.</summary>
    public static Detail DetailFor(string npcId, double factor)
    {
        double r = Unit(Hash(npcId + "|detail"));
        double precise = Math.Clamp(0.2 * factor, 0.05, 0.6);
        return r < precise ? Detail.Precise : r < precise + 0.45 ? Detail.Rough : Detail.Vague;
    }

    /// <summary>
    /// The places this person likes to eat (0 to 3; about one in four rarely eats out), described with their own sense
    /// of the prices. Places in <paramref name="nearLocation"/> are likelier picks.
    /// </summary>
    public static List<string> PlacesFor(string npcId, IReadOnlyList<Place> places, Detail detail, string nearLocation)
    {
        var result = new List<string>();
        if (places.Count == 0 || Unit(Hash(npcId + "|eatsout")) < 0.25) return result;
        int want = 1 + (int)(Unit(Hash(npcId + "|count")) * 3); // 1..3
        double city = places.Where(p => p.Dishes.Count > 0).Select(p => p.AveragePrice).DefaultIfEmpty(0).Average();
        // Frugal people find things pricier; comfortable ones less so.
        double bias = 0.85 + Unit(Hash(npcId + "|thrift")) * 0.35;
        var picked = places
            .OrderBy(p => Unit(Hash($"{npcId}|place|{p.Name}")) / (string.Equals(p.Location, nearLocation, StringComparison.OrdinalIgnoreCase) ? 2.0 : 1.0))
            .Take(Math.Min(want, places.Count));
        foreach (var p in picked)
        {
            string sense = Sense(city > 0 ? p.AveragePrice / city * bias : 1);
            var dishes = p.Dishes.OrderBy(d => Unit(Hash($"{npcId}|dish|{p.Name}|{d.Dish}"))).ToList();
            string where = p.Location.Length > 0 ? $"{p.Name} ({p.Location})" : p.Name;
            result.Add(detail switch
            {
                Detail.Precise when dishes.Count > 1 =>
                    $"{where}: you usually get the {dishes[0].Dish} (about {dishes[0].Price} Lims); the {dishes[1].Dish} is about {dishes[1].Price}. You'd call it {sense}.",
                Detail.Precise => $"{where}: you usually get the {dishes[0].Dish} (about {dishes[0].Price} Lims). You'd call it {sense}.",
                Detail.Rough => $"{where}: you usually get the {dishes[0].Dish}; {sense} for what you get. You don't remember exact prices.",
                _ => $"{where}: you like it; you think it's {sense}, but you couldn't say what anything costs.",
            });
        }
        return result;
    }

    public static string Sense(double ratio) => ratio switch
    {
        < 0.85 => "cheap",
        < 1.12 => "fair",
        < 1.3 => "a bit pricey",
        _ => "expensive",
    };

    internal static double Unit(uint h) => (h & 0xFFFFFF) / (double)0x1000000;

    /// <summary>FNV-1a: stable across runs (string.GetHashCode isn't).</summary>
    internal static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
