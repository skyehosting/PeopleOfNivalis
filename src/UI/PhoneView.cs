using System;
using System.Collections.Generic;
using UnityEngine;

namespace NivalisAIChat.UI;

/// <summary>
/// What the phone shows, filled in by the runtime before each draw (the window keeps no game state of its own).
/// </summary>
internal sealed class PhoneView
{
    public string Time = "", Clock = "", Date = "";
    /// <summary>The weather under the date on the home screen (FB-74): its icon (sun, moon, cloud, rain, storm, snow, fog) and words.</summary>
    public string Sky = "", SkyWords = "";
    public int Bars = 4;
    /// <summary>FB-70: the battery (full at 8 AM, never dies).</summary>
    public int Battery = 100;
    public string Carrier = "NIVA";
    public bool FirstTime;
    public int Unread;
    /// <summary>The phone key's name as the hints show it ("N").</summary>
    public string Key = "N";

    public sealed class Person
    {
        public string Id = "", Name = "", Number = "", Tag = "", Kind = "known";
        /// <summary>The relationship bar, 0-100 (the card's six cells).</summary>
        public double Bond = 50;
        public int TimesMet;
        public string Got = "", LastTexted = "";
        /// <summary>"her", "him", "their" (the card's "Got her number").</summary>
        public string Their = "their";
        /// <summary>The game's own portrait of them (null: their initial instead).</summary>
        public Sprite? Portrait;
        /// <summary>The chat window's info sheet (only what they told you; "?" where they haven't), and its sections.</summary>
        public List<(string label, string value, bool known)> About = new();
        public List<(string heading, List<string> items)> AboutSections = new();
    }

    public sealed class Conversation
    {
        public string Id = "", Name = "", Kind = "known", Preview = "", When = "";
        public bool Unread;
        public Sprite? Portrait;
    }

    public sealed class Line
    {
        /// <summary>"you", "them", "stamp".</summary>
        public string From = "";
        public string Text = "";
        /// <summary>Under your text: "Sending…", "Sent", "Delivered", "Seen 9:42 PM", or "Not sent yet · waiting for signal" (Failed).</summary>
        public string Meta = "";
        public bool Failed;
        /// <summary>FB-70: the circle beside your text: "sending", "sent", "delivered", "seen", "fail", or "" (none: answered).</summary>
        public string Receipt = "";
        /// <summary>Where it is in the conversation (to delete it), -1 for a stamp.</summary>
        public int Index = -1;
        /// <summary>A line from a story conversation (Ava's), and a voice note in a made voice (not her recording).</summary>
        public bool Story, MadeVoice;
        /// <summary>A voice note under their text: its file ("" none), length, wave.</summary>
        public string Voice = "";
        public float VoiceSeconds;
        public int[] Wave = Array.Empty<int>();
    }

    // ------------------------------------------------------------------ Nightline (FB-71)

    /// <summary>Someone in a line's popover (who liked it, who saw it): name, ring, portrait.</summary>
    public sealed class Who
    {
        public string Name = "", Kind = "stranger";
        public Sprite? Portrait;
    }

    public sealed class LineCard
    {
        public string Id = "", Name = "", Handle = "", Place = "", Ago = "", Text = "";
        /// <summary>Their ring: "friend", "partner", "known", "stranger", or "you".</summary>
        public string Kind = "stranger";
        public Sprite? Portrait;
        /// <summary>The photo's file on the PC ("" none); being made.</summary>
        public string Photo = "", PhotoNote = "";
        public bool Developing;
        public int Up, Down, Comments;
        /// <summary>Your own reaction: true liked, false disliked, null none.</summary>
        public bool? Mine;
        public bool Yours, Waiting;
        /// <summary>Reactions and comments on your line you haven't seen ("+2 new").</summary>
        public int New;
        public List<Who> Likers = new(), Dislikers = new(), Seen = new();
        public int SeenCount;
    }

    public sealed class LineComment
    {
        public string Id = "", Name = "", Handle = "", Text = "", Ago = "", Kind = "stranger";
        public Sprite? Portrait;
        public bool Yours, Reply;
        /// <summary>The commenter's own reaction to the line (shown beside their name).</summary>
        public bool? Up;
    }

    /// <summary>The feed's tab: "city", "people" or "you".</summary>
    public string LineTab = "city";
    public List<LineCard> Lines = new();
    /// <summary>The opened line and its comments.</summary>
    public LineCard? LineOpen;
    public List<LineComment> LineComments = new();
    /// <summary>The home badge: others' reactions and comments on your lines you haven't seen.</summary>
    public int LineUnseen;
    /// <summary>"Kiko liked your line · Mara commented" (the strip under the tabs), "" none.</summary>
    public string LineNews = "";
    /// <summary>New lines above while you're scrolled down ("2 new lines").</summary>
    public int LineNewAbove;
    /// <summary>The night dim over the light app (0 by day).</summary>
    public float LineDim;
    /// <summary>You're replying to this comment: its handle and its first words ("" none).</summary>
    public string ReplyTo = "", ReplyHandle = "", ReplyQuote = "";
    public bool NightlineOn = true;
    public string YourName = "You", YourHandle = "you";

    public List<Person> Contacts = new();
    /// <summary>The News app: the holos' headlines (when, title, summary) and the talk around town (what, where).</summary>
    public List<(string when, string title, string summary)> News = new();
    public List<(string text, string where)> Talk = new();
    public List<Conversation> Conversations = new();

    // The open conversation.
    public string ThreadId = "", ThreadName = "", ThreadNumber = "";
    /// <summary>Their initial, on the seen circle.</summary>
    public string ThreadInitial = "";
    public List<Line> Thread = new();
    /// <summary>Texts before the ones shown (Show earlier).</summary>
    public int Earlier;
    public bool Typing;
    /// <summary>"Mara didn't answer." with Try again, or the no-key line (no Try again); "" for none.</summary>
    public string NoAnswer = "";
    public bool CanRetry;
    /// <summary>"No signal here (blizzard). Texts wait and go out when you have a bar again." or "".</summary>
    public string NoSignal = "";

    // Settings.
    public string WallpaperUrl = "", WallpaperNote = "";
    public bool WallpaperError, WallpaperBusy, WallpaperKept;
    public bool Signal = true, Weather = true, Places = true, PeopleText = true, RealLife = true;
    public int NeverSees = 10, AnswersLater = 10;
    public bool VoiceNotes, AvaVoice;
    public int VoiceChance = 20;
    public string CostsSummary = "", PlacesSummary = "";
    public int[] Costs = new int[5];
    /// <summary>Which of the five weathers is now (-1 none): rain, thunder, snow, blizzard, fog.</summary>
    public int WeatherNow = -1;
    public int Indoors = 1;
    public string HerePlace = "";
    public int HereBars = 4;
    public bool HereListed;
    public List<(string place, int bars)> PlaceList = new();
    public string Readout = "";
    public int ReadoutBars = 4;
    public string MyNumber = "";
    /// <summary>The Undo strip on the screen it belongs to ("" none), and which screen.</summary>
    public string Undo = "", UndoScreen = "";

    // FB-80: the Camera and the Gallery.
    /// <summary>The Camera's live view (the player's own, no HUD) and its width over height.</summary>
    public Texture? CameraFeed;
    public float CameraAspect = 16f / 9f;
    /// <summary>What the Camera says under the view ("Saved to the gallery", or why it couldn't).</summary>
    public string CameraNote = "";
    /// <summary>FB-81/82: the shutter's and the way back's names, as the Camera says them.</summary>
    public string CameraKey = "Click", CameraBack = "Right-click";
    public List<GalleryItem> Gallery = new();
    /// <summary>How many of the gallery's newest are drawn ("Show more" adds some).</summary>
    public int GalleryShown = 48;
    /// <summary>The gallery opened to pick a photo for a Nightline line.</summary>
    public bool GalleryPick;
    /// <summary>The photo going with your next Nightline line (its path), "" none.</summary>
    public string LineAttach = "";

    public sealed class GalleryItem
    {
        public string Path = "", File = "", When = "", Where = "";
        /// <summary>Someone under 18 was in view, or the mod didn't take it: never shown to an AI.</summary>
        public bool Child, Ours;
    }
}
