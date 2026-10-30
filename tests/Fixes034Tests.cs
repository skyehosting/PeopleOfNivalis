using System;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>0.3.4: FB-38 the three rings.</summary>
internal static partial class Program
{
    private static void Fixes034Tests()
    {
        Console.WriteLine("-- 0.3.4 FB-38: the hearing, voices and chatter rings");
        var r = Rings.From(15f, 3f, 18.25f);
        Check(r.Hearing == 15f && r.Voices == 18f && r.Chatter == 18.25f && r.ToSettings() == (15f, 3f, 18.25f),
              "from the settings: hearing = VoiceDistance, voices = VoiceDistance + VoiceBeyond, chatter = ChatterRange (and back)");
        Check(Rings.Snap(15.1f) == 15f && Rings.Snap(15.13f) == 15.25f && Rings.Snap(15.375f) == 15.5f && Rings.Snap(float.NaN) == 0f && Rings.Snap(-3f) == 0f,
              "every value moves in 0.25 m steps (nothing negative or unreadable)");

        // One ring moved: it wins, the others keep the order (hearing <= voices <= chatter).
        var wider = r.WithHearing(20f);
        Check(wider == new Rings(20f, 20f, 20f), $"hearing out to 20 m: the voices and chatter rings come out with it ({wider})");
        var tighter = r.WithChatter(10f);
        Check(tighter == new Rings(10f, 10f, 10f), $"chatter in to 10 m: the inner rings come in to it ({tighter})");
        var voices = r.WithVoices(17.5f);
        Check(voices == new Rings(15f, 17.5f, 18.25f), $"voices to 17.5 m: nothing else moves ({voices})");
        Check(r.WithVoices(50f).Hearing == 20f, "voices at most 30 m past hearing: hearing comes up with a far voices ring");
        Check(r.WithChatter(2f).Chatter == Rings.MinChatter && r.WithChatter(99f).Chatter == Rings.MaxRing, "chatter stays within 5-60 m");
        var off = r.WithHearing(0f);
        Check(off.Hearing == 0f && off.Chatter == 18.25f && off.ToSettings() == (0f, 3f, 18.25f), $"hearing 0 = no voices; the voices margin (3 m) is kept, the chatter ring stays ({off})");
        Check(off.WithHearing(15f) == r, "...and turning voices back on finds the rings as they were");
        Check(r.WithHearing(80f) == new Rings(60f, 60f, 60f) && r.WithVoices(100f) == new Rings(30f, 60f, 60f),
              "nothing past 60 m: the rings stay in order (chatter never inside the others)");
        Check(Rings.From(15f, 3f, 10f).Clamped() == new Rings(15f, 18f, 18f), "an older file's chatter ring inside the others is drawn (and kept) outside them: in order");
        var messy = Rings.From(25f, 3f, 18.25f); // /chatter voice 25 with the default chatter ring: out of order
        var tidy = messy.ScaledTo(20f);
        Check(tidy.Hearing <= tidy.Voices && tidy.Voices <= tidy.Chatter && tidy.Chatter == 20f, $"rings out of order (a hand edit) are put in order first, never an error ({tidy})");
        Check(Rings.From(15f, 3f, 10f).ScaledTo(12.25f) is { Chatter: 12.25f } t2 && t2.Hearing <= t2.Voices && t2.Voices <= 12.25f, "...also an older file's chatter ring inside hearing");

        // The master: the outer ring set, the others scaled by the same factor.
        var half = r.ScaledTo(9.125f);
        Check(half.Chatter == 9.25f && half.Hearing == 7.5f && half.Voices == 9f, $"the master scales all three together, each on a 0.25 m step ({half})");
        var up = r.ScaledTo(r.Chatter + Rings.Step);
        Check(up.Chatter == 18.5f && up.Hearing >= r.Hearing && up.Voices >= r.Voices, $"one master step up moves the outer ring 0.25 m and never shrinks the others ({up})");
        var anchored = r;
        for (int i = 0; i < 73; i++) anchored = r.ScaledTo(anchored.Chatter + Rings.Step);
        Check(anchored == r.ScaledTo(36.5f) && anchored == new Rings(30f, 36f, 36.5f), $"73 steps from the same anchor end where one scale would: no drift ({anchored})");

        // Talk wraps up at the chatter ring, or the voices ring if that's farther.
        Check(new ChatterSettings { VoiceDistance = 15, VoiceBeyond = 3, Range = 18.25f }.WrapUpDistance == 21.25f,
              "talk wraps up 3 m past the chatter ring (21.25 m): never at the line it started inside of");
        Check(new ChatterSettings { VoiceDistance = 15, VoiceBeyond = 3, Range = 12 }.WrapUpDistance == 18f,
              "...or past the voices ring when the chatter ring is smaller (an older file's 12 m)");
        Check(new ChatterSettings().Range == 18.25f, "the chatter ring's default is 0.25 m past the voices ring");
    }
}
