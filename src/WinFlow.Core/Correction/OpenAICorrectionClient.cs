using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Correction;

/// <summary>
/// Cloud transcript correction via OpenAI Chat Completions (gpt-4o-mini).
/// Reuses the same API key as STT.
/// </summary>
public sealed class OpenAICorrectionClient : ITranscriptCorrector
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Func<string?> _apiKeyProvider;
    private readonly string _model;
    private readonly Uri _endpoint;

    public OpenAICorrectionClient(
        Func<string?> apiKeyProvider,
        string model = "gpt-4o-mini",
        Uri? endpoint = null)
    {
        _apiKeyProvider = apiKeyProvider;
        _model = model;
        _endpoint = endpoint ?? new Uri("https://api.openai.com/v1/chat/completions");
    }

    public async Task<string> CorrectAsync(
        string transcript,
        CorrectionIntensity intensity,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No OpenAI API key is configured. Set one from the tray menu.");
        }

        string systemPrompt = CorrectionPrompts.BuildSystemPrompt(intensity);
        string userMessage = CorrectionPrompts.BuildUserMessage(transcript);

        var payload = new
        {
            model = _model,
            temperature = 0.2,
            max_tokens = 512,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Correction request failed ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            return transcript;
        }

        JsonElement message = choices[0].GetProperty("message");
        return message.GetProperty("content").GetString()?.Trim() ?? transcript;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
