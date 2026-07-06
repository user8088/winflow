using System.Text.Json;

namespace WinFlow.Core.Net;

/// <summary>
/// Pure message builders and event parsing for the OpenAI Realtime API's
/// transcription mode. Mirrors freeflow's protocol exactly: session.update
/// configures transcription-only with manual commit (turn_detection null),
/// audio flows as base64 pcm16 appends, and the final transcript arrives
/// as conversation.item.input_audio_transcription.completed.
/// </summary>
public static class RealtimeProtocol
{
    public enum EventKind
    {
        TranscriptionDelta,
        TranscriptionCompleted,
        Error,
        Other,
    }

    public static string BuildSessionUpdate(string sttModel, string? language, bool nearFieldMic)
    {
        var transcription = new Dictionary<string, object?> { ["model"] = sttModel };
        if (language is not null)
        {
            transcription["language"] = language;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = new Dictionary<string, object?>
            {
                ["modalities"] = new[] { "text" },
                ["input_audio_format"] = "pcm16",
                ["input_audio_transcription"] = transcription,
                // null disables server VAD so the client controls when
                // audio ends via commit.
                ["turn_detection"] = null,
                ["input_audio_noise_reduction"] = new Dictionary<string, object?>
                {
                    ["type"] = nearFieldMic ? "near_field" : "far_field",
                },
            },
        });
    }

    public static string BuildAudioAppend(ReadOnlySpan<byte> pcm16)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(pcm16),
        });
    }

    public static string BuildCommit() => """{"type":"input_audio_buffer.commit"}""";

    /// <summary>
    /// Classifies a server event. Payload is the transcript (completed),
    /// delta text, or error message; empty for <see cref="EventKind.Other"/>.
    /// </summary>
    public static (EventKind Kind, string Payload) ParseEvent(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement))
            {
                return (EventKind.Other, "");
            }

            switch (typeElement.GetString())
            {
                case "conversation.item.input_audio_transcription.completed":
                    return (EventKind.TranscriptionCompleted,
                        root.TryGetProperty("transcript", out JsonElement transcript)
                            ? transcript.GetString() ?? ""
                            : "");

                case "conversation.item.input_audio_transcription.delta":
                    return (EventKind.TranscriptionDelta,
                        root.TryGetProperty("delta", out JsonElement delta)
                            ? delta.GetString() ?? ""
                            : "");

                case "error":
                    string message = "unknown error";
                    if (root.TryGetProperty("error", out JsonElement error)
                        && error.TryGetProperty("message", out JsonElement messageElement))
                    {
                        message = messageElement.GetString() ?? message;
                    }

                    return (EventKind.Error, message);

                default:
                    return (EventKind.Other, "");
            }
        }
        catch (JsonException)
        {
            return (EventKind.Other, "");
        }
    }
}
