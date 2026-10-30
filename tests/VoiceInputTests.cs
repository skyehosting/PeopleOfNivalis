using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-64: speech to text (Ai/SpeechToText.cs) against a fake service: the address, which key goes where (never the main
/// key to another service), the WAV it sends, the form's fields, the answer read, errors that never carry the key.
/// </summary>
internal static partial class Program
{
    private sealed class FakeSpeech : HttpMessageHandler
    {
        public string Url = "", Auth = "", Form = "";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Answer = "{\"text\":\" Hello there, Mae. \"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            Auth = request.Headers.Authorization?.ToString() ?? "";
            Form = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(ct));
            return new HttpResponseMessage(Status) { Content = new StringContent(Answer) };
        }
    }

    private static void VoiceInputTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-64: voice input (speech to text)");
        Check(SpeechToText.Endpoint("https://api.openai.com/v1/") == "https://api.openai.com/v1/audio/transcriptions"
              && SpeechToText.Endpoint("http://127.0.0.1:8080/v1/audio/transcriptions") == "http://127.0.0.1:8080/v1/audio/transcriptions",
            "the service's address: the base with /audio/transcriptions");
        Check(SpeechToText.KeyFor("https://api.openai.com/v1", "", "https://api.openai.com/v1", "main-key") == "main-key"
              && SpeechToText.KeyFor("https://api.groq.com/openai/v1", "", "https://api.openai.com/v1", "main-key") == ""
              && SpeechToText.KeyFor("http://127.0.0.1:8080/v1", "", "https://openrouter.ai/api/v1", "main-key") == ""
              && SpeechToText.KeyFor("https://api.groq.com/openai/v1", "own-key", "https://api.openai.com/v1", "main-key") == "own-key",
            "the main ApiKey only goes to the same service; another (or a local server) gets its own key or none");

        var samples = new short[1600];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(Math.Sin(i / 8.0) * 8000);
        var wav = SpeechToText.Wav(samples, 16000);
        Check(wav.Length == 44 + 3200 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE"
              && BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 34) == 16 && BitConverter.ToInt32(wav, 40) == 3200,
            "the recording goes as a 16 kHz 16-bit mono WAV");
        Check(SpeechToText.Peak(samples) > 0.2 && SpeechToText.Peak(new short[100]) == 0, "a silent recording is told from speech");

        var fake = new FakeSpeech();
        var stt = new SpeechToText(() => new SpeechSettings { Url = "https://api.openai.com/v1", Key = "sk-test-123456789", Model = "whisper-1", Language = "EN" },
            new HttpMessageInvoker(fake));
        string text = stt.TranscribeAsync(samples, 16000, CancellationToken.None).GetAwaiter().GetResult();
        Check(text == "Hello there, Mae." && fake.Url == "https://api.openai.com/v1/audio/transcriptions" && fake.Auth == "Bearer sk-test-123456789",
            "what you said comes back as text, from the right address with its key");
        Check(fake.Form.Contains("name=model") && fake.Form.Contains("whisper-1") && fake.Form.Contains("name=language") && fake.Form.Contains("\r\n\r\nen\r\n")
              && fake.Form.Contains("filename=speech.wav") && fake.Form.Contains("RIFF"), "the form: the model, the language, the WAV file");

        var local = new FakeSpeech { Answer = "plain words" };
        var none = new SpeechToText(() => new SpeechSettings { Url = "http://127.0.0.1:8080/v1", Key = "", Model = "", Language = "" }, new HttpMessageInvoker(local));
        Check(none.TranscribeAsync(samples, 16000, CancellationToken.None).GetAwaiter().GetResult() == "plain words" && local.Auth == ""
              && !local.Form.Contains("name=language") && local.Form.Contains("whisper-1"),
            "a local server: no key sent, no language when it's to be worked out, plain-text answers read");

        var refused = new FakeSpeech { Status = HttpStatusCode.Unauthorized, Answer = "{\"error\":{\"message\":\"Incorrect API key provided: sk-test-123456789\"}}" };
        var bad = new SpeechToText(() => new SpeechSettings { Url = "https://api.openai.com/v1", Key = "sk-test-123456789" }, new HttpMessageInvoker(refused));
        string error = "";
        try { bad.TranscribeAsync(samples, 16000, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (SpeechException e) { error = e.Message; }
        Check(error.Contains("refused the key") && !error.Contains("sk-test-123456789"), "a refused key is said plainly, and never shown");
        string nowhere = "";
        try { new SpeechToText(() => new SpeechSettings { Url = "not a url" }).TranscribeAsync(samples, 16000, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (SpeechException e) { nowhere = e.Message; }
        Check(nowhere.Contains("SpeechToTextUrl"), "an address that isn't one is said, nothing sent");
        Check(SpeechToText.TextOf("{\"text\":\"hi\"}") == "hi" && SpeechToText.TextOf("{\"oops\":1}") == "" && SpeechToText.TextOf("") == "",
            "the answer's words read, anything else is nothing");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.VoiceInput.cs"));
        Check(rt.Contains("bool types = key is >= Key.A and <= Key.Z") && rt.Contains("_voiceKey = ok ? parsed : Key.F4;"),
            "the voice key is never one that types into the box (F4 then)");
        Check(rt.Contains("private static SpeechToText SpeechNow()") && File.ReadAllText(Path.Combine(src, "ModRuntime.Chat.cs")).Contains("try { StopVoiceInput(); } catch { }"),
            "the settings are read on the game's thread; the microphone lets go when the chat closes");
        Check(new ModConfigProbe().SecretsHaveSpeechKey(), "SpeechToTextKey is a secret (masked, never logged or shipped)");
    }

    private sealed class ModConfigProbe
    {
        public bool SecretsHaveSpeechKey([CallerFilePath] string here = "") =>
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src", "ModConfig.cs"))
                .Contains("_secrets.UnionWith(new ConfigEntryBase[] { ApiKey, VoyageKey, ExtraHeaders, BackgroundApiKey, SpeechToTextKey, ImageApiKey });");
    }
}
