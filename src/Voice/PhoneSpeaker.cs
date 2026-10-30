using System;

namespace NivalisAIChat.Voice;

/// <summary>
/// A voice as it sounds through a phone on speaker (FB-19b, the author: "have it sound like its coming through speaker
/// using a filter"): the telephone band only (about 300 to 3400 Hz), softly overdriven, a little quieter. Done on the
/// samples themselves, not with Unity's audio filters: the voice object's own muffle filter is rewritten every frame, and
/// this way it can be tested. A new clip; the original isn't changed.
/// </summary>
public static class PhoneSpeaker
{
    public const double Low = 300, High = 3400, Drive = 1.6, Level = 0.85;

    public static PcmAudio Filter(PcmAudio audio)
    {
        int channels = Math.Max(1, audio.Channels);
        int rate = Math.Max(8000, audio.SampleRate);
        var output = new float[audio.Samples.Length];
        for (int ch = 0; ch < channels; ch++)
        {
            var highPass = Biquad.HighPass(rate, Low);
            var lowPass = Biquad.LowPass(rate, Math.Min(High, rate * 0.45));
            for (int i = ch; i < audio.Samples.Length; i += channels)
            {
                double x = lowPass.Next(highPass.Next(audio.Samples[i]));
                output[i] = (float)(Level * Math.Tanh(Drive * x) / Drive); // quieter (gain Level), loud parts squashed under Level / Drive
            }
        }
        return new PcmAudio { Samples = output, SampleRate = audio.SampleRate, Channels = audio.Channels };
    }

    /// <summary>A second-order filter (the RBJ audio cookbook's, Q = 0.707).</summary>
    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public static Biquad HighPass(int rate, double freq) => Make(rate, freq, high: true);
        public static Biquad LowPass(int rate, double freq) => Make(rate, freq, high: false);

        private static Biquad Make(int rate, double freq, bool high)
        {
            double w = 2 * Math.PI * freq / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.7071);
            double a0 = 1 + alpha;
            double b0 = high ? (1 + cos) / 2 : (1 - cos) / 2;
            double b1 = high ? -(1 + cos) : 1 - cos;
            return new Biquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b0 / a0, _a1 = -2 * cos / a0, _a2 = (1 - alpha) / a0 };
        }

        public double Next(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
