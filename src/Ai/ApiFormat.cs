namespace NivalisAIChat;

public enum ApiFormat
{
    /// <summary>Anthropic Messages API (POST /v1/messages).</summary>
    Anthropic,
    /// <summary>OpenAI-style chat completions (OpenAI, OpenRouter, Ollama, LM Studio, KoboldCpp, vLLM...).</summary>
    OpenAI,
}
