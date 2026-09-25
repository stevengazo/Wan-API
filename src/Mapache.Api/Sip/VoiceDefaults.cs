namespace Mapache.Api.Sip;

/// <summary>Valores por defecto de cada proveedor de voz cuando el panel los deja vacíos.</summary>
public static class VoiceDefaults
{
    public const string OpenAiModel = "gpt-realtime-2.1";
    public const string OpenAiVoice = "marin";
    public const string OpenAiTranscriptionModel = "gpt-4o-transcribe";
    public const string DeepgramLanguage = "es";
    public const string DeepgramListenModel = "nova-3";
    public const string DeepgramSpeakModel = "aura-2-celeste-es";
}
