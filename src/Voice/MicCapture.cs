using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NivalisAIChat.Voice;

/// <summary>
/// FB-64: the microphone, through Windows' own waveIn (in every Windows; the author's NivalisMP records the same way):
/// 16-bit mono in a few short buffers Windows fills in turn; <see cref="Read"/> takes what's been recorded since (call it
/// every frame while recording). Stop lets the microphone go. No Unity here.
/// </summary>
internal sealed class MicCapture : IDisposable
{
    private const uint WaveMapper = 0xFFFFFFFF; // Windows' default microphone
    private const uint WhdrDone = 1;
    private const int HeaderSize = 48; // WAVEHDR on 64-bit: lpData 0, dwBufferLength 8, dwBytesRecorded 12, dwUser 16, dwFlags 24, dwLoops 28, lpNext 32, reserved 40

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCapsW(UIntPtr id, ref WaveInCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr handle, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr handle);

    public const int Rate = 16000;
    private readonly int _bufferSamples;
    private readonly IntPtr[] _headers;
    private IntPtr _handle;
    private int _next;

    public bool Running => _handle != IntPtr.Zero;

    public MicCapture(int bufferMs = 60, int buffers = 10)
    {
        _bufferSamples = Rate * bufferMs / 1000;
        _headers = new IntPtr[buffers];
    }

    /// <summary>The microphones Windows has, by name (cut to 31 characters by Windows).</summary>
    public static List<string> Inputs() => Devices().Select(d => d.name).ToList();

    /// <summary>Each microphone with Windows' own number for it (review F-MIC-1: a device skipped doesn't shift the rest).</summary>
    private static List<(uint id, string name)> Devices()
    {
        var list = new List<(uint, string)>();
        try
        {
            uint n = waveInGetNumDevs();
            for (uint i = 0; i < n && i < 64; i++)
            {
                var caps = new WaveInCaps();
                if (waveInGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WaveInCaps>()) == 0 && !string.IsNullOrWhiteSpace(caps.Name))
                    list.Add((i, caps.Name.Trim()));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return list;
    }

    /// <summary>Starts recording from the named microphone ("" or one not found: Windows' default). Null when started, or why not.</summary>
    public string? Start(string device)
    {
        if (Running) return null;
        if (IntPtr.Size != 8) return "the microphone needs the game's 64-bit process"; // the header layout below is 64-bit's
        uint id = WaveMapper;
        if (!string.IsNullOrWhiteSpace(device))
            foreach (var (devId, name) in Devices())
                if (string.Equals(name, device.Trim(), StringComparison.OrdinalIgnoreCase)) { id = devId; break; }
        var fmt = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = Rate, BitsPerSample = 16, BlockAlign = 2, AvgBytesPerSec = Rate * 2 };
        int r;
        try { r = waveInOpen(out _handle, id, ref fmt, IntPtr.Zero, IntPtr.Zero, 0); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _handle = IntPtr.Zero; return "Windows' sound recording isn't available"; }
        if (r != 0) { _handle = IntPtr.Zero; return $"the microphone couldn't be opened (Windows said {r}; is one plugged in and allowed in Windows' privacy settings?)"; }
        for (int i = 0; i < _headers.Length; i++)
        {
            _headers[i] = NewHeader(_bufferSamples * 2);
            if (waveInPrepareHeader(_handle, _headers[i], HeaderSize) != 0 || waveInAddBuffer(_handle, _headers[i], HeaderSize) != 0)
            {
                Stop();
                return "the microphone's buffers couldn't be set up";
            }
        }
        _next = 0;
        r = waveInStart(_handle);
        if (r != 0) { Stop(); return $"the microphone couldn't start (Windows said {r})"; }
        return null;
    }

    /// <summary>The samples recorded since the last call (in order), handing their buffers back to Windows.</summary>
    public short[] Read()
    {
        if (!Running) return Array.Empty<short>();
        var got = new List<short>();
        for (int k = 0; k < _headers.Length; k++)
        {
            IntPtr h = _headers[_next];
            if ((Flags(h) & WhdrDone) == 0) break; // not filled yet: the rest are later still
            int bytes = Math.Min(Marshal.ReadInt32(h, 12), _bufferSamples * 2);
            var data = new short[bytes / 2];
            if (data.Length > 0) Marshal.Copy(Marshal.ReadIntPtr(h, 0), data, 0, data.Length);
            got.AddRange(data);
            Marshal.WriteInt32(h, 24, (int)(Flags(h) & ~WhdrDone));
            waveInAddBuffer(_handle, h, HeaderSize);
            _next = (_next + 1) % _headers.Length;
        }
        return got.ToArray();
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            waveInStop(_handle);
            waveInReset(_handle);
            foreach (var h in _headers) if (h != IntPtr.Zero) waveInUnprepareHeader(_handle, h, HeaderSize);
            waveInClose(_handle);
        }
        catch { }
        _handle = IntPtr.Zero;
        for (int i = 0; i < _headers.Length; i++) { FreeHeader(_headers[i]); _headers[i] = IntPtr.Zero; }
    }

    public void Dispose() { Stop(); GC.SuppressFinalize(this); }

    /// <summary>Never left open: a capture nobody stopped lets the microphone go when it's collected.</summary>
    ~MicCapture() { try { Stop(); } catch { } }

    private static uint Flags(IntPtr h) => (uint)Marshal.ReadInt32(h, 24);

    private static IntPtr NewHeader(int bufferBytes)
    {
        IntPtr h = Marshal.AllocHGlobal(HeaderSize);
        for (int i = 0; i < HeaderSize; i += 4) Marshal.WriteInt32(h, i, 0);
        IntPtr data = Marshal.AllocHGlobal(bufferBytes);
        for (int i = 0; i < bufferBytes; i++) Marshal.WriteByte(data, i, 0);
        Marshal.WriteIntPtr(h, 0, data);
        Marshal.WriteInt32(h, 8, bufferBytes);
        return h;
    }

    private static void FreeHeader(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        Marshal.FreeHGlobal(Marshal.ReadIntPtr(h, 0));
        Marshal.FreeHGlobal(h);
    }
}
