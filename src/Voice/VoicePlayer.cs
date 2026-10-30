using System;
using System.Collections.Generic;
using Nivalis;
using NivalisAIChat.Game;
using UnityEngine;
using UnityEngine.Audio;

namespace NivalisAIChat.Voice;

/// <summary>
/// Plays a person's speech as 3D sound from their head: full close by, fading smoothly with distance to silence at the
/// edge of the hearing range (see <see cref="Falloff"/>), so walking closer mid-line fades them in. Like real hearing,
/// someone behind you is quieter (BehindVolume) and a little muffled, as is someone far off. Each line fades in briefly;
/// one cut off fades out.
///
/// Each person's voice is its own object that follows their head every frame, not a part of their body: the game
/// switches parts of a body off and on as people go in and out of view, and a sound on a switched-off part stops dead
/// (it used to go silent when you turned toward or away from someone, until their next line). If a line still stops
/// early for any reason, it's picked up again where it was.
/// </summary>
internal static class VoicePlayer
{
    /// <summary>One person's voice: where it plays from, and the line it's playing.</summary>
    private sealed class Voice
    {
        public AudioSource Source = null!;
        /// <summary>The clip this voice is playing, kept here too: freed even when its source is already gone (LEAK-7).</summary>
        public AudioClip? Clip;
        public AudioLowPassFilter? Muffle;
        public Character? C;
        public Transform? Anchor;
        /// <summary>The line's own volume (your Volume setting, quieter while your own chat is open).</summary>
        public float Base = 1f;
        // the fade in / fade out (a line cut off)
        public float FadeFrom, FadeTo = 1f, FadeStart, FadeSeconds;
        public bool StopAfter;
        public float Gain = 1f;
        // where you are relative to them (smoothed, so turning around sounds natural)
        public float Behind = 1f, Cutoff = Falloff.OpenCutoff;
        // the line itself: started when, how long, how far it got (to pick it up again if it stops early)
        public bool Line;
        public float StartedAt, Length, LastTime, IdleSince;
        public int Resumes;
        /// <summary>Their talk is over: the voice is let go as soon as the line it's on has finished.</summary>
        public bool ReleaseWhenDone;
    }

    /// <summary>A voice nobody has used for this long is let go anyway (it's made again if they speak).</summary>
    private const float IdleSeconds = 20f;

    private static readonly Dictionary<IntPtr, Voice> Voices = new();
    private static readonly HashSet<IntPtr> WarnedSilent = new();
    private static readonly Dictionary<int, AnimationCurve> Curves = new();

    /// <summary>Volume of someone directly behind you, 0..1 (config VoiceBehind); in front is full volume.</summary>
    public static float BehindVolume = 0.5f;
    /// <summary>Voices behind you and far off sound a little muffled (config VoiceMuffle).</summary>
    public static bool MuffleOn = true;

    private static AudioListener? _listener;
    private static float _nextListenerLook;

    /// <summary>Play through the game's own audio channel, so its volume settings apply (config UseGameVolume).</summary>
    public static bool UseGameVolume = true;
    private static AudioMixerGroup? _channel;
    private static float _nextChannelLook;

    // Channel names in order of preference; never the in-ear/radio voice (it carries a radio filter), music or UI.
    private static readonly string[] Preferred = { "dialog", "voice", "speech", "vo", "character", "npc", "sfx", "effect", "fx", "world", "master" };
    private static readonly string[] Avoid = { "ear", "radio", "phone", "music", "ambien", "ui", "menu", "reverb", "snapshot" };

    /// <summary>
    /// UseGameVolume was switched in the settings: the game's channel is looked for again at the next line (or not used),
    /// and voices that exist drop the one they had, so it takes effect without a restart.
    /// </summary>
    public static void GameVolumeChanged(bool on)
    {
        UseGameVolume = on;
        _channel = null;
        _nextChannelLook = 0f;
        foreach (var v in Voices.Values)
        {
            try { if (v.Source != null && !v.Source.WasCollected) v.Source.outputAudioMixerGroup = null; } catch { }
        }
    }

    /// <summary>The game's mixer channel for voices, found once (and looked for again every 30 s until the mixer exists).</summary>
    private static AudioMixerGroup? GameChannel()
    {
        if (!UseGameVolume) return null;
        if (_channel != null) return _channel;
        if (Time.unscaledTime < _nextChannelLook) return null;
        _nextChannelLook = Time.unscaledTime + 30f;
        try
        {
            var handler = UnityEngine.Object.FindObjectOfType<AudioHandler>();
            var mixer = handler != null ? handler.mixer : null;
            if (mixer == null) return null;
            var groups = mixer.FindMatchingGroups("Master");
            if (groups == null || groups.Length == 0) groups = mixer.FindMatchingGroups("");
            var all = new List<AudioMixerGroup>();
            if (groups != null) foreach (var g in groups) if (g != null) all.Add(g);
            Log.Info("Game audio channels: " + string.Join(", ", all.ConvertAll(g => g.name)));
            foreach (var want in Preferred)
            {
                foreach (var g in all)
                {
                    string n = (g.name ?? "").ToLowerInvariant();
                    bool match = want == "vo" ? n == "vo" || n.StartsWith("vo ") || n.EndsWith(" vo") : n.Contains(want);
                    if (!match || Array.Exists(Avoid, a => n.Contains(a))) continue;
                    _channel = g;
                    Log.Info($"Voices play through the game's '{g.name}' audio channel, so the game's volume settings apply to them.");
                    return _channel;
                }
            }
            Log.Info("No suitable game audio channel found; voices use the mod's own Volume setting only.");
        }
        catch (Exception e) { Log.Debug("game audio channel: " + e.Message); }
        return null;
    }

    /// <summary>
    /// FB-45: a line heard flat, from nobody in particular (the Setup Wizard's voice test), also with the game paused under
    /// a menu. Returns its length in seconds (0 if it couldn't play); the clip and its player are freed once it's done.
    /// </summary>
    public static float PlayFlat(PcmAudio audio, float volume)
    {
        GameObject? go = null;
        AudioClip? clip = null;
        try
        {
            if (audio.Samples.Length == 0) return 0;
            go = new GameObject("PeopleOfNivalis.Test");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var source = go.AddComponent<AudioSource>();
            int frames = audio.Samples.Length / Math.Max(1, audio.Channels);
            clip = AudioClip.Create("PeopleOfNivalis.Test", frames, Math.Max(1, audio.Channels), audio.SampleRate, false);
            clip.SetData(audio.Samples, 0);
            if (GameChannel() is { } channel) source.outputAudioMixerGroup = channel;
            source.spatialBlend = 0f;
            source.ignoreListenerPause = true;
            source.volume = Mathf.Clamp01(volume);
            source.clip = clip;
            source.Play();
            float length = clip.length;
            var g = go;
            var c = clip;
            ModRuntime.Later(length + 0.5f, () => { try { UnityEngine.Object.Destroy(c); UnityEngine.Object.Destroy(g); } catch { } });
            return length;
        }
        catch (Exception e)
        {
            Log.Debug("test voice: " + e.Message);
            try { if (clip != null) UnityEngine.Object.Destroy(clip); if (go != null) UnityEngine.Object.Destroy(go); } catch { }
            return 0;
        }
    }

    /// <summary>Starts the line and returns its length in seconds (0 if it couldn't play).</summary>
    public static float Play(Character c, PcmAudio audio, float volume, float hearingDistance, float fullVolumeWithin = 2f)
    {
        AudioClip? made = null;
        bool attached = false;
        try
        {
            if (c == null || audio.Samples.Length == 0) return 0;
            var v = VoiceFor(c);
            if (v == null) return 0;
            var source = v.Source;
            int frames = audio.Samples.Length / Math.Max(1, audio.Channels);
            var clip = made = AudioClip.Create("PeopleOfNivalis.Line", frames, Math.Max(1, audio.Channels), audio.SampleRate, false);
            clip.SetData(audio.Samples, 0);
            if (source.outputAudioMixerGroup == null && GameChannel() is { } channel) source.outputAudioMixerGroup = channel;
            var previous = source.clip;
            source.Stop();
            source.clip = clip;
            attached = true;
            v.Clip = clip;
            // The line it replaces is freed now, before anything below can fail (LEAK-7).
            if (previous != null && previous != clip) UnityEngine.Object.Destroy(previous);
            v.C = c;
            v.Anchor = GameContext.HeadOf(c);
            Place(v);
            Reach(source, hearingDistance, fullVolumeWithin);
            v.Base = Mathf.Clamp01(volume);
            v.Gain = 0f; // faded in (see Tick)
            v.FadeFrom = 0f;
            v.FadeTo = 1f;
            v.FadeStart = Time.unscaledTime;
            v.FadeSeconds = Falloff.FadeInSeconds;
            v.StopAfter = false;
            Hear(v, snap: true);
            source.volume = 0f;
            source.Play();
            v.Line = true;
            v.ReleaseWhenDone = false;
            v.StartedAt = Time.unscaledTime;
            v.Length = clip.length;
            v.LastTime = 0f;
            v.Resumes = 0;
            if (!source.isPlaying && WarnedSilent.Add(c.Pointer))
                Log.Warn("A voice line couldn't be played (Unity's audio refused it); their words still show.");
            // Free the audio once it has finished (a newer line replaces and frees it earlier). Still playing then (a pause,
            // a slow start): looked at again a second later, not left for good (LEAK-7).
            float length = clip.length;
            void FreeWhenDone()
            {
                try
                {
                    bool gone = source == null;
                    if (!gone && source!.clip == clip && source.isPlaying) { ModRuntime.Later(1f, FreeWhenDone); return; }
                    if (gone || source!.clip == clip)
                    {
                        if (!gone) source!.clip = null;
                        if (v.Clip == clip) v.Clip = null;
                        if (clip != null) UnityEngine.Object.Destroy(clip);
                    }
                }
                catch { }
            }
            ModRuntime.Later(length + 2f, FreeWhenDone);
            return length;
        }
        catch (Exception e)
        {
            Log.Warn("Couldn't play a voice line: " + e.Message);
            return 0;
        }
        finally
        {
            // Made but never put on a source (something failed in between): freed here, or it would never be (LEAK-7).
            if (made != null && !attached) try { UnityEngine.Object.Destroy(made); } catch { }
        }
    }

    /// <summary>Stops whatever this person is saying (their conversation was broken up), fading it out quickly.</summary>
    public static void Stop(Character? c)
    {
        try
        {
            if (c == null || !Voices.TryGetValue(c.Pointer, out var v) || v.Source == null) return;
            v.Line = false; // not picked up again (also when it had stopped early and was waiting to be picked up: #9)
            if (!v.Source.isPlaying) { v.Source.Stop(); return; } // quiet now (finished, cut off or paused): stopped outright, so it can't play on later
            v.FadeFrom = v.Gain;
            v.FadeTo = 0f;
            v.FadeStart = Time.unscaledTime;
            v.FadeSeconds = Falloff.FadeOutSeconds;
            v.StopAfter = true;
        }
        catch { }
    }

    /// <summary>
    /// They're done talking (their conversation, call, thought or your chat is over): their voice object is removed as
    /// soon as the line it's on has finished, or right away if it's quiet. Made again if they speak again.
    /// </summary>
    public static void Release(Character? c)
    {
        if (c == null) return;
        try
        {
            if (!Voices.TryGetValue(c.Pointer, out var v)) return;
            if (v.Source != null && !v.Source.WasCollected && (v.Source.isPlaying || v.Line)) { v.ReleaseWhenDone = true; return; }
            Destroy(v);
            Voices.Remove(c.Pointer);
        }
        catch { }
    }

    /// <summary>Every voice stopped and removed (the mod is stopping, or everyone was reset).</summary>
    public static void StopAll()
    {
        foreach (var v in Voices.Values) Destroy(v);
        Voices.Clear();
    }

    /// <summary>How many voice objects exist right now (for the console's checks).</summary>
    public static int Count => Voices.Count;

    /// <summary>
    /// Every frame: each voice follows its person's head; its volume follows the fade, and where you are (behind you:
    /// quieter and a little muffled); a line that stopped early is picked up again; voices nobody uses are let go.
    /// </summary>
    public static void Tick()
    {
        if (Voices.Count == 0) return;
        float now = Time.unscaledTime;
        bool paused = false;
        try { paused = AudioListener.pause; } catch { }
        var ear = Ear(); // once for every voice this frame
        List<IntPtr>? drop = null;
        foreach (var kv in Voices)
        {
            var v = kv.Value;
            try
            {
                if (v.Source == null || v.Source.WasCollected) { Destroy(v); (drop ??= new()).Add(kv.Key); continue; } // its clip freed too (LEAK-7)
                bool alive = v.C != null && !v.C.WasCollected && v.C.isActiveAndEnabled;
                bool playing = v.Source.isPlaying;
                if (!playing && !v.Line)
                {
                    // Quiet: removed once their talk is over or they're gone (or, as a safety net, after a while unused).
                    if (v.IdleSince <= 0f) v.IdleSince = now;
                    if (v.ReleaseWhenDone || !alive || now - v.IdleSince > IdleSeconds) { Destroy(v); (drop ??= new()).Add(kv.Key); }
                    continue;
                }
                v.IdleSince = 0f;
                if (alive) Place(v);

                // the fade in / out
                float k = v.FadeSeconds <= 0f ? 1f : Mathf.Clamp01((now - v.FadeStart) / v.FadeSeconds);
                v.Gain = Mathf.Lerp(v.FadeFrom, v.FadeTo, k * k * (3f - 2f * k));
                if (k >= 1f && v.StopAfter)
                {
                    v.StopAfter = false;
                    v.Line = false;
                    v.Source.Stop();
                    continue;
                }

                if (playing) v.LastTime = v.Source.time;
                else if (v.Line && !paused)
                {
                    // Stopped before the end: pick it up where it was (a few times at most), unless it really finished.
                    float elapsed = now - v.StartedAt;
                    bool finished = v.LastTime >= v.Length - 0.2f || elapsed >= v.Length + 0.5f || v.Source.clip == null;
                    if (finished || !alive || v.Resumes >= 5) { v.Line = false; continue; }
                    v.Resumes++;
                    v.Source.Play();
                    v.Source.time = Mathf.Clamp(v.LastTime, 0f, Mathf.Max(0f, v.Length - 0.05f)); // where it was, not the start
                    if (v.Resumes == 1) Log.Debug($"voice: {(v.C != null ? v.C.name : "?")}'s line had stopped at {v.LastTime:0.0}/{v.Length:0.0} s; picked up again.");
                }
                Hear(v, snap: false, ear);
            }
            catch { Destroy(v); (drop ??= new()).Add(kv.Key); }
        }
        if (drop != null) foreach (var k in drop) Voices.Remove(k);
    }

    /// <summary>The voice follows their head (or, if that's hidden or somewhere else, their body at head height).</summary>
    private static void Place(Voice v)
    {
        var c = v.C;
        if (c == null) return;
        try { v.Source.transform.position = GameContext.HeadPoint(v.Anchor, c.transform); }
        catch { v.Anchor = null; }
    }

    /// <summary>Where you hear from and which way you face, read once (null when there's no listener).</summary>
    private static (Vector3 at, Vector3 forward)? Ear()
    {
        var listener = Listener();
        if (listener == null) return null;
        try { var lt = listener.transform; return (lt.position, lt.forward); }
        catch { return null; }
    }

    /// <summary>How it sounds from where you are: quieter from behind, muffled behind you and far off (smoothed).</summary>
    private static void Hear(Voice v, bool snap) => Hear(v, snap, Ear());

    /// <param name="ear">Read once per frame for every voice (D4, PERF-12), not once per voice.</param>
    private static void Hear(Voice v, bool snap, (Vector3 at, Vector3 forward)? ear)
    {
        float behind = 1f, cutoff = Falloff.OpenCutoff;
        if (ear is { } e)
        {
            try
            {
                var to = v.Source.transform.position - e.at;
                float angle = to.sqrMagnitude < 0.01f ? 0f : Vector3.Angle(e.forward, to);
                behind = Falloff.BehindGain(angle, BehindVolume);
                cutoff = MuffleOn ? Falloff.MuffleCutoff(angle, to.magnitude, v.Source.maxDistance) : Falloff.OpenCutoff;
            }
            catch { }
        }
        float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
        v.Behind = snap ? behind : Mathf.MoveTowards(v.Behind, behind, dt * 2.5f); // about 0.2 s to swing as you turn
        // the cutoff moves in steps of pitch, not hertz, so the change is even to the ear
        float la = Mathf.Log(Mathf.Max(20f, v.Cutoff)), lb = Mathf.Log(Mathf.Max(20f, cutoff));
        v.Cutoff = snap ? cutoff : Mathf.Exp(Mathf.MoveTowards(la, lb, dt * 4f));
        v.Source.volume = v.Base * v.Gain * v.Behind;
        if (v.Muffle != null)
        {
            bool on = MuffleOn && v.Cutoff < Falloff.OpenCutoff - 1f;
            if (v.Muffle.enabled != on) v.Muffle.enabled = on;
            if (on) v.Muffle.cutoffFrequency = v.Cutoff;
        }
    }

    /// <summary>Where you hear from: the game's audio listener (usually the camera).</summary>
    private static AudioListener? Listener()
    {
        try
        {
            if (_listener != null && !_listener.WasCollected && _listener.isActiveAndEnabled) return _listener;
            if (Time.unscaledTime < _nextListenerLook) return null;
            _nextListenerLook = Time.unscaledTime + 1f;
            _listener = null;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                if (l != null && l.isActiveAndEnabled) { _listener = l; break; }
            return _listener;
        }
        catch { return null; }
    }

    /// <summary>
    /// How far the line carries: full volume within <paramref name="full"/> metres, fading smoothly to silence at
    /// <paramref name="edge"/> (a custom roll-off, see <see cref="Falloff"/>).
    /// </summary>
    private static void Reach(AudioSource source, float edge, float full)
    {
        edge = Mathf.Max(3f, edge);
        full = Mathf.Clamp(full, 0.5f, edge * 0.8f);
        source.maxDistance = edge;
        source.minDistance = full;
        try
        {
            int key = Mathf.RoundToInt(full / edge * 200f);
            if (!Curves.TryGetValue(key, out var curve) || curve == null)
            {
                curve = new AnimationCurve();
                foreach (var p in Falloff.Curve(full, edge)) curve.AddKey(new Keyframe(p.X, p.V, p.SlopeIn, p.SlopeOut));
                Curves[key] = curve;
            }
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
        }
        catch (Exception e)
        {
            source.rolloffMode = AudioRolloffMode.Linear;
            Log.Debug("voice roll-off: " + e.Message);
        }
    }

    /// <summary>This person's voice, made on first use: its own object (not part of their body), placed at their head each frame.</summary>
    private static Voice? VoiceFor(Character c)
    {
        if (Voices.TryGetValue(c.Pointer, out var existing))
        {
            try { if (existing.Source != null && !existing.Source.WasCollected && existing.Source.gameObject.activeInHierarchy) return existing; }
            catch { }
            Destroy(existing);
            Voices.Remove(c.Pointer);
        }
        if (Voices.Count > 64) Prune();
        var go = new GameObject("PeopleOfNivalis.Voice");
        try { return SetUp(go, c); }
        catch { try { UnityEngine.Object.Destroy(go); } catch { } throw; } // not left behind half set up (LEAK-7)
    }

    private static Voice SetUp(GameObject go, Character c)
    {
        var source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.spread = 40f;          // a voice to one side still reaches the other ear a little, as in life
        source.dopplerLevel = 0f;
        source.priority = 32;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2f;
        AudioLowPassFilter? muffle = null;
        try
        {
            muffle = go.AddComponent<AudioLowPassFilter>();
            muffle.cutoffFrequency = Falloff.OpenCutoff;
            muffle.lowpassResonanceQ = 1f;
            muffle.enabled = false;
        }
        catch (Exception e) { Log.Debug("voice muffle: " + e.Message); muffle = null; }
        var v = new Voice { Source = source, Muffle = muffle, C = c, Anchor = GameContext.HeadOf(c) };
        Voices[c.Pointer] = v;
        return v;
    }

    /// <summary>Frees a voice: its clip whatever state its source is in (gone with a scene, say), then the source's own, then the object. Each on its own (LEAK-7).</summary>
    private static void Destroy(Voice v)
    {
        var clip = v.Clip;
        v.Clip = null;
        try { if (clip != null) UnityEngine.Object.Destroy(clip); } catch { }
        try
        {
            if (v.Source != null && !v.Source.WasCollected && v.Source.clip != null && v.Source.clip != clip)
                UnityEngine.Object.Destroy(v.Source.clip);
        }
        catch { }
        try { if (v.Source != null && !v.Source.WasCollected) UnityEngine.Object.Destroy(v.Source.gameObject); } catch { }
    }

    /// <summary>Frees voices that aren't speaking (they're made again when that person speaks).</summary>
    private static void Prune()
    {
        var drop = new List<IntPtr>();
        foreach (var kv in Voices)
        {
            var v = kv.Value;
            try
            {
                if (v.Source != null && !v.Source.WasCollected && (v.Source.isPlaying || v.Line)) continue;
                Destroy(v);
                drop.Add(kv.Key);
            }
            catch { Destroy(v); drop.Add(kv.Key); } // dropped, so freed first (LEAK-7)
        }
        foreach (var k in drop) Voices.Remove(k);
    }
}
