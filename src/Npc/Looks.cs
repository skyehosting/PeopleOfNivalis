using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>
/// What each of the game's character models looks like, written from the game's own portraits (one per model; the
/// "_2" versions are the same person in other colours). Found by the character's portrait name (or model name), so a
/// newly met person is written to match who's standing there: apparent age, hair, face, augments, clothes.
/// Ages are what the portrait suggests; every model here is an adult.
/// </summary>
public static class Looks
{
    public sealed class Look
    {
        public int MinAge, MaxAge;
        public string Text = "";
        /// <summary>A machine rather than a person-shaped android (service bots).</summary>
        public bool Machine;
    }

    private static Look L(int min, int max, string text) => new() { MinAge = min, MaxAge = max, Text = text };
    private static Look Bot(string text) => new() { MinAge = 0, MaxAge = 0, Text = text, Machine = true };

    private static readonly Dictionary<string, Look> Table = new()
    {
        // ---- crowd: women
        ["female010"] = L(25, 45, "synthetic blue-grey skin, red eyes, bald head, dark jacket with a glowing red implant on the chest"),
        ["female011"] = L(22, 35, "shoulder-length plum-purple hair, warm light-brown skin, fitted dark grey jacket with ribbed panels"),
        ["female022"] = L(20, 30, "long electric-blue hair, tanned skin, purple eye makeup, circuit-pattern tattoos on her shoulder, green patterned top"),
        ["female023"] = L(25, 40, "long black hair, half of her face a dark cybernetic plate, black techwear jacket with a small blue logo"),
        ["female08"] = L(22, 32, "dark skin, white bob with a long fringe, blue lipstick, chest tattoos, blue hoodie over a white top"),
        ["female082"] = L(22, 32, "dark skin, pink-to-purple bob with a long fringe, chest tattoos, peach hoodie over a white top"),
        ["female001"] = L(20, 30, "long teal hair, dark skin, purple eyeshadow, white padded jacket"),
        ["female003"] = L(22, 32, "red hair tied back, light skin with pale scar lines on one cheek, green-collared top with harness straps"),
        ["female004"] = L(22, 35, "brown hair with a blue streak and a fringe, tanned skin, white collar under a black jacket"),
        ["female005"] = L(20, 30, "long black hair, light skin, plain teal-grey crew-neck top"),
        ["female006"] = L(22, 35, "dark hair with a burgundy tint, a cybernetic patch around one eye, black choker, black-and-orange patterned top"),
        ["female007"] = L(20, 30, "blonde-to-pink bob, pale skin with faint scar lines on her face, purple jacket"),
        ["female008"] = L(22, 32, "long magenta hair, tattoo lines on her face and chest, white top with dark straps"),
        ["female009"] = L(25, 40, "silver-white hair with a red streak, stern face, red lipstick, dark green jacket"),
        ["female012"] = L(20, 30, "black bob with a purple streak, pale skin with scar lines on her cheeks, green zip-up top under a purple vest"),
        ["female013"] = L(20, 28, "purple hair, pale skin with faint face lines, pink tank top and a mesh sleeve"),
        ["female014"] = L(20, 30, "black hair with a heavy fringe, light skin, plain grey-blue T-shirt"),
        ["randomfemale02"] = L(25, 40, "dark hair pulled back, olive skin, a plain, tired face, dark jacket with white trim at the collar"),
        ["randomfemale03"] = L(22, 35, "dark skin, big black afro, top with orange straps"),
        ["randomfemale032"] = L(22, 35, "dark skin, big navy-blue afro, top with orange straps"),
        ["randomfemale04"] = L(20, 30, "long auburn braids with orange beads, light skin, white markings painted under her eyes"),
        ["randomfemale042"] = L(20, 30, "long dark purple braids with orange beads, light skin, white markings painted under her eyes"),
        ["randomfemale05"] = L(25, 35, "wavy grey-blue hair in loose braids, olive skin, glowing cyan choker"),
        ["randomfemale052"] = L(25, 35, "wavy dark hair in loose braids, olive skin, glowing cyan choker"),
        ["randomfemale06"] = L(20, 30, "long violet hair, cross earring, goth makeup, tattooed arms, black top with a white geometric print"),
        ["randomfemale062"] = L(20, 30, "long crimson hair, cross earring, goth makeup, tattooed arms, black top with a purple geometric print"),
        ["randomfemale07"] = L(22, 35, "long auburn hair, pale skin, dark red top"),
        ["randomfemale072"] = L(22, 35, "long light-blue hair, pale skin, dark red top"),
        ["randomfemale08"] = L(22, 35, "brown skin, plum-red bob with a fringe, pink eye makeup, black jacket"),
        ["randomfemale082"] = L(22, 35, "brown skin, grey bob with a fringe, pink eye makeup, green-grey jacket"),
        ["randomfemale09"] = L(19, 25, "short black hair with cyan tips, round youthful face, dark green jacket"),
        ["randomfemale01"] = L(22, 35, "long dark grey-blue hair with a fringe, olive skin, faint lines on her face, dark jacket"),
        ["female002"] = L(22, 32, "long green hair, dark skin, green dots painted under her eyes, dark sequinned top"),
        ["female015"] = L(22, 35, "long dark brown hair, brown skin, mustard-yellow top"),
        ["female016"] = L(20, 30, "long green hair, light skin, a tattooed line on her chin, leopard-print top"),
        ["female017"] = L(20, 28, "black bob, pale skin, dark lipstick, teal-and-black top with backpack straps"),
        ["female018"] = L(22, 32, "black bob with an orange headband, tanned skin, a glowing blue tattoo on her cheek, white-and-orange techwear jacket"),
        ["female019"] = L(20, 30, "red bob, light brown skin, purple lipstick, red turtleneck"),
        ["female026"] = L(22, 35, "long silver-lavender hair with bangs, a cybernetic plate along her jaw, black techwear with glowing trim"),
        ["female20"] = L(22, 35, "long red hair, dark cyber-tattoo lines down one side of her face, dark lipstick"),
        ["female21"] = L(22, 32, "long dark brown hair, pale skin, red eye makeup, chest tattoo, dark red top"),
        ["female24"] = L(20, 30, "long pink hair, pale skin, red turtleneck with a hexagon mesh pattern"),
        ["female25"] = L(20, 30, "long black hair, pale skin, violet eyes, thin scar lines on her face and chest, dark outfit"),

        // ---- crowd: men
        ["randommale01"] = L(20, 30, "tanned skin, red-and-black baseball cap, chain necklace, burgundy hoodie"),
        ["randommale012"] = L(20, 30, "tanned skin, black baseball cap, teal hoodie over a white T-shirt"),
        ["randommale02"] = L(25, 40, "grey-blond hair slicked back, an earring, pale skin, black jacket with white trim"),
        ["randommale022"] = L(22, 35, "dark hair swept to the side, an earring, light brown skin, stubble, purple jacket with neon-yellow panels"),
        ["randommale03"] = L(25, 40, "long orange hair, black cap with a white logo, round blue sunglasses, stubble, dark top"),
        ["randommale032"] = L(25, 40, "long grey hair, black cap with a white logo, round green sunglasses, stubble, dark top"),
        ["randommale04"] = L(20, 30, "dark curly hair, olive skin, black jacket with red stripes"),
        ["randommale042"] = L(20, 30, "brown curly hair, olive skin, black jacket with a gold trim"),
        ["randommale05"] = L(30, 45, "dark teal hair and beard, tanned skin, teal-and-grey jacket"),
        ["randommale052"] = L(30, 45, "ginger hair and beard, a breathing tube under his nose, red jacket"),
        ["randommale06"] = L(30, 45, "short black hair, brown skin, olive jacket over a white shirt"),
        ["randommale07"] = L(22, 35, "messy brown hair, thin mustache, an earring, pendant necklace, white shirt under a grey jacket"),
        ["male001"] = L(30, 45, "bald, light brown skin, glasses, tattoos on his face and neck, dark mesh top"),
        ["male002"] = L(40, 55, "dark skin, short hair, greying beard, brown leather jacket with a fur collar"),
        ["male003"] = L(20, 30, "teal hair, light skin, neck tattoos, dark jacket"),
        ["male004"] = L(22, 32, "blond hair, pale skin, cybernetic implants on his temple and cheek, dark jacket"),
        ["male005"] = L(25, 40, "grey hair, pale skin, the lower half of his face covered by a black scale-pattern mask, patterned shirt with a red tie"),
        ["male006"] = L(25, 35, "dark quiff, stubble and a goatee, light skin, neck tattoo, blue track jacket"),
        ["male007"] = L(35, 50, "greying undercut, thick mustache, red-rimmed goggles, dark hooded coat"),
        ["male008"] = L(25, 40, "teal hair, round glasses, light skin, grey suit with a waistcoat and tie"),
        ["male009"] = L(25, 40, "dark skin, short twists, a tattoo on his forehead, small beard, black jacket"),
        ["male010"] = L(35, 50, "burgundy beanie, weathered brown skin, heavy black jacket"),
        ["male011"] = L(25, 40, "dark skin, round gold-rimmed glasses, a purple-glowing implant across half his face, purple suit with neon trim"),
        ["male012"] = L(25, 45, "dark skin, cyan visor goggles, full respirator mask, black clothes"),
        ["male013"] = L(22, 35, "dark skin, a glowing yellow implant line on his face, red bow tie, pinstripe jacket"),
        ["male014"] = L(30, 45, "dark skin, wire-rimmed glasses, faint implant lines on his forehead, white shirt and tie"),
        ["male015"] = L(30, 45, "shaved head, dark sunglasses, respirator mask, broad build, polka-dot shirt"),
        ["male016"] = L(50, 65, "grey hair, glasses, lined face, dark suit with a red tie"),
        ["male017"] = L(25, 40, "short black hair, tanned skin, implant lines on his face, dark shirt with a gold bolo tie"),
        ["male018"] = L(25, 40, "black slicked-back hair, half his face covered in black cyber-skin, black suit over a red shirt"),
        ["male019"] = L(25, 40, "blue cap, blue tech goggles, beard, dark hoodie"),
        ["male020"] = L(30, 45, "bald, dark skin, a respirator mask, black T-shirt"),

        // ---- staff and services
        ["bartender01"] = L(20, 30, "short lilac-pink hair, pale skin, black top with a white logo"),
        ["bartender02"] = L(22, 35, "scruffy brown hair, black-rimmed glasses, light skin, open denim shirt"),
        ["chef"] = L(22, 35, "long orange-red hair, light skin, white chef's jacket"),
        ["chef2"] = L(25, 40, "dark skin, short hair, dark jacket with green glowing trim"),
        ["chef3"] = L(25, 40, "slicked-back blond hair, pale skin, white chef's jacket"),
        ["chef4"] = L(22, 35, "black bob with a fringe, tanned skin, black-and-white chef's top"),
        ["cleaner1"] = L(25, 40, "short dark hair, beard, light brown skin, black work jacket with cyan trim"),
        ["cleaner2"] = L(22, 35, "brown bob, pale skin, olive work jacket"),
        ["cleaner3"] = L(25, 40, "short brown hair, tanned skin, cream T-shirt under green overalls"),
        ["cleaner4"] = L(25, 40, "brown bowl-cut hair, short beard, white shirt under orange overall straps"),
        ["waiter2"] = L(22, 35, "black cap with a logo, glasses, dark uniform shirt"),
        ["businesswoman01"] = L(30, 45, "blonde hair pinned up, pale skin, green top under a brown jacket"),
        ["policeman1"] = L(30, 45, "CorpSec cap with a headset mic, mustache, olive skin, black-and-yellow CorpSec uniform"),
        ["corpsecpolicewoman1"] = L(25, 40, "blonde hair under a CorpSec cap with a headset, black-and-yellow CorpSec uniform"),
        ["corpsecbot"] = Bot("a tall boxy CorpSec security robot, grey and red, with no face"),
        ["waiterbot"] = Bot("a boxy cream-coloured service robot with a green dot-matrix screen for a face"),
        ["humourbot"] = Bot("a tall red-and-teal capsule-shaped robot with a green dot-matrix face on a dark screen"),
        ["therapaybot"] = Bot("a beige kiosk machine whose old screen shows a cartoon face: a crowned, bearded old man in round goggles"),
        ["oldtherapaybot"] = Bot("a beige kiosk machine whose old screen shows a red cartoon face with spiky hair and round goggles"),
        // ---- crowd and role models whose portraits have plain names
        ["businessman"] = L(30, 45, "slicked-back brown hair, dark sunglasses, light-tan skin, a grey-blue blazer over a white shirt and red tie"),
        ["vendormale"] = L(20, 30, "short dark hair under a navy cap with a glowing blue display, a young face, dark blue work jacket with overall straps"),
        ["vendorfemale"] = L(22, 32, "shoulder-length ginger hair, green eyes, a big smile, an olive-green cap, green-and-brown top"),
        ["waiter"] = L(22, 32, "short dark hair swept to one side, light-brown skin, a calm face, white high-collared uniform"),
        ["corpsecagent"] = L(30, 45, "slicked-back dark hair, stubble, a black visor over the eyes, an earpiece with a coiled wire, black uniform"),
        ["misobaron"] = L(30, 45, "long black hair and beard under a dark green hood, brown skin, tired eyes"),
        ["cleanerbot"] = Bot("a small boxy cleaning robot with a single red eye"),
        ["cleanerbotblue"] = Bot("a small blue-and-cream cleaning robot with a single red eye"),
        ["cleanerbotwhite"] = Bot("a small white cleaning robot with a single red eye"),

        // ---- story characters (their own record decides; this is only how they look)
        ["aengusprince"] = L(25, 40, "dark hair swept back, brown skin, a glowing cybernetic collar, dark jacket"),
        ["botannoss"] = L(35, 50, "shaved head, goatee and stubble, gauged earrings, chain necklace, brown leather jacket"),
        ["calebfoamheart"] = Bot("a battered gold-and-grey robot head with one red eye and a long bushy grey beard"),
        ["elainemalloy"] = L(60, 75, "curly white hair, glasses, brown skin, a lined, kind face"),
        ["elviragamov"] = L(28, 40, "short dark purple hair, pale skin, high-collared black coat"),
        ["finlaybasin"] = L(30, 45, "red hair under a black hood with gold trim, pale freckled skin"),
        ["gideon"] = L(40, 55, "long dark hair, curled mustache, red scarf, a gun on his back"),
        ["huckstash"] = L(60, 75, "striped purple beanie, long white hair and beard, round glasses"),
        ["magszunica"] = L(20, 30, "spiky green hair, pale skin with faint face tattoos, orange-and-yellow striped jacket"),
        ["masatoshima"] = L(35, 50, "grey slicked-back hair, stubble, red patterned shirt under a dark jacket"),
        ["miajay"] = L(20, 30, "long red hair, green eyes, a choker and hoop earrings"),
        ["micarain"] = L(30, 45, "white bob, round glasses, purple hoodie with glowing cyan trim"),
        ["milo"] = L(25, 40, "black-rimmed glasses, a headset over short dark hair, orange hoodie"),
        ["palmer"] = L(25, 40, "spiky blonde hair, one red cybernetic eye, black jacket over a red plaid shirt"),
        ["ptolemy"] = L(25, 40, "short blond hair with a camo headband, broad bare shoulders"),
        ["razlo"] = L(20, 25, "curly brown hair, thick black glasses, white shirt with a bow tie"),
        ["reehanscott"] = L(20, 30, "short dark hair, pale skin, glowing implant lines on her neck, white-and-dark sports top"),
        ["rhenorichardson"] = L(20, 30, "wavy auburn hair, pale skin, blue-and-yellow jacket"),
        ["sagehullen"] = L(22, 35, "white hair over one eye, cyber-tattoos down one cheek, black jacket with a white collar"),
        ["silasrhodes"] = L(35, 50, "long black hair, dark beard, brown skin, grey shirt"),
        ["spartacusdoom"] = L(30, 45, "dark mohawk, scarred face, a cybernetic jaw, bare muscular shoulders"),
        ["thaddeus"] = L(65, 80, "long white beard, an eyepatch, flat cap, green checked shirt with braces"),
        ["zaffer"] = L(22, 35, "blue spiked mohawk, piercings, red-and-white jacket with a raised collar"),
        ["ziggyrasand"] = L(25, 40, "short red hair, pale skin, red jacket with a high collar"),
    };

    /// <summary>"Random Female 08_portrait" → "randomfemale08"; "Reehan_Scott_v4.51_portrait" → "reehanscott".</summary>
    public static string Key(string? name)
    {
        string n = (name ?? "").ToLowerInvariant();
        foreach (var junk in new[] { "(clone)", "_portrait", "portrait", "_nobg", "nobg", "_v4.51" })
            n = n.Replace(junk, "");
        return new string(n.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>The look for a portrait or model name; null when the mod doesn't know that model.</summary>
    public static Look? Find(string? name)
    {
        string k = Key(name);
        if (k.Length == 0) return null;
        if (Table.TryGetValue(k, out var look)) return look;
        // "female013_something" / "npcrandommale07rig..." → the longest known key it starts with or contains (never a
        // man's look inside a woman's name: "female020" isn't "male020")
        var hit = Table.Keys.Where(t => t.Length >= 6 && Holds(k, t)).OrderByDescending(t => t.Length).FirstOrDefault();
        return hit != null ? Table[hit] : null;
    }

    private static bool Holds(string name, string key)
    {
        for (int at = name.IndexOf(key, StringComparison.Ordinal); at >= 0; at = name.IndexOf(key, at + 1, StringComparison.Ordinal))
            if (!(key.StartsWith("male") && at >= 2 && name.Substring(at - 2, 2) == "fe")) return true;
        return false;
    }

    public static int Count => Table.Count;
}
