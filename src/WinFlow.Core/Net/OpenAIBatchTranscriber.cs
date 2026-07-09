using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Audio;
using WinFlow.Core.Models;

namespace WinFlow.Core.Net;

/// <summary>
/// Whole-take transcription via POST /v1/audio/transcriptions. Races the
/// streaming path on every dictation so a stalled WebSocket never loses
/// the user's words.
/// </summary>
public sealed class OpenAIBatchTranscriber : IBatchSttProvider
{
    /// <summary>Matches <see cref="Services.DictationPipelineOptions.BatchTimeout"/> default.</summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = new() { Timeout = DefaultRequestTimeout };

    private readonly Func<string?> _apiKeyProvider;
    private readonly string _model;
    private readonly Uri _endpoint;

    public OpenAIBatchTranscriber(
        Func<string?> apiKeyProvider,
        string model = "gpt-4o-mini-transcribe",
        Uri? endpoint = null)
    {
        _apiKeyProvider = apiKeyProvider;
        _model = model;
        _endpoint = endpoint ?? new Uri("https://api.openai.com/v1/audio/transcriptions");
    }

    public async Task<string> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
    {
        string? apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No OpenAI API key is configured. Set one from the tray menu.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var wavContent = new ByteArrayContent(WavEncoder.Encode(audio.Pcm16, audio.SampleRate));
        wavContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        var form = new MultipartFormDataContent
        {
            { new StringContent(_model), "model" },
            { wavContent, "file", "audio.wav" },
        };
        request.Content = form;

        using HttpResponseMessage response = await Http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Transcription request failed ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("text", out JsonElement text)
            ? (text.GetString() ?? "").Trim()
            : "";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
