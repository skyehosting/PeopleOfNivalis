using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Voice;

/// <summary>
/// The voices built into Windows (SAPI), reached through COM: free and offline, no key, but robotic and only a few
/// voices. Each call runs on its own STA thread, as SAPI prefers.
/// </summary>
public static class WindowsSpeech
{
    private const int Saft22kHz16BitMono = 22;
    private const int SvsfIsNotXml = 16; // the text is read as text, never as SAPI markup

    public static Task<List<VoiceInfo>> VoicesAsync(CancellationToken ct = default) => OnSta(() =>
    {
        var list = new List<VoiceInfo>();
        dynamic voice = Create("SAPI.SpVoice");
        try
        {
            dynamic tokens = voice.GetVoices("", "");
            int count = tokens.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic token = tokens.Item(i);
                string id = token.Id;
                string name = token.GetDescription(0);
                string gender = "", age = "", language = "";
                try { gender = token.GetAttribute("Gender") ?? ""; } catch { }
                try { age = token.GetAttribute("Age") ?? ""; } catch { }
                try { language = token.GetAttribute("Language") ?? ""; } catch { }
                list.Add(new VoiceInfo { Id = id, Name = name, Gender = VoiceCatalog.Gender(gender), Age = age.ToLowerInvariant().Contains("senior") ? "old" : "", Locale = language });
                Release(token);
            }
            Release(tokens);
        }
        finally { Release(voice); }
        return list;
    }, ct);

    public static Task<PcmAudio?> SpeakAsync(string voiceId, string text, double speed, CancellationToken ct = default) => OnSta<PcmAudio?>(() =>
    {
        dynamic voice = Create("SAPI.SpVoice");
        dynamic stream = Create("SAPI.SpMemoryStream");
        try
        {
            dynamic tokens = voice.GetVoices("", "");
            int count = tokens.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic token = tokens.Item(i);
                if ((string)token.Id == voiceId) { voice.Voice = token; break; }
            }
            Release(tokens);
            dynamic format = stream.Format;
            format.Type = Saft22kHz16BitMono;
            stream.Format = format;
            voice.AudioOutputStream = stream;
            voice.Rate = Math.Clamp((int)Math.Round((speed - 1) * 10), -3, 3);
            voice.Speak(text, SvsfIsNotXml);
            byte[] data = (byte[])stream.GetData();
            return data.Length < 2 ? null : Audio.FromPcm16(data, 22050, 1);
        }
        finally
        {
            Release(stream);
            Release(voice);
        }
    }, ct);

    /// <summary>COM objects are freed now rather than whenever the garbage collector gets round to it.</summary>
    private static void Release(object? com)
    {
        try { if (com != null && OperatingSystem.IsWindows() && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); } catch { }
    }

    private static object Create(string progId)
    {
        // The game only runs on Windows; this also tells the analyzer so (CA1416, audit step 7f).
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows speech (SAPI) only exists on Windows.");
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("Windows speech (SAPI) isn't available on this PC.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Couldn't start Windows speech.");
    }

    /// <summary>
    /// At most two SAPI calls at once. A call SAPI never returns from keeps its slot (its thread can't be stopped), so
    /// waiting never parks more than two threads; a line that can't get a slot gives up at its deadline instead.
    /// </summary>
    private static readonly SemaphoreSlim Slots = new(2, 2);

    /// <summary>Both SAPI calls are taken (stuck, or very slow): the voice service sets Windows aside for a while.</summary>
    public static bool AllBusy => Slots.CurrentCount == 0;

    private static async Task<T> OnSta<T>(Func<T> work, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows voices need Windows.");
        await Slots.WaitAsync(ct).ConfigureAwait(false);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                try { done.TrySetResult(work()); }
                catch (Exception e) { done.TrySetException(e); }
                finally { Slots.Release(); } // when SAPI really returns, even after the caller gave up
            }) { IsBackground = true, Name = "PeopleOfNivalis.WindowsSpeech" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            Slots.Release(); // the thread never ran: its slot is free again
            throw;
        }
        return await done.Task.WaitAsync(ct).ConfigureAwait(false);
    }
}
