using System.Text.Json;
using WinFlow.Core.Net;

namespace WinFlow.Core.Tests;

public class RealtimeProtocolTests
{
    [Fact]
    public void SessionUpdateConfiguresTranscriptionOnlyWithManualCommit()
    {
        string json = RealtimeProtocol.BuildSessionUpdate(
            "gpt-4o-mini-transcribe", language: null, nearFieldMic: true);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("session.update", root.GetProperty("type").GetString());
        JsonElement session = root.GetProperty("session");
        Assert.Equal("pcm16", session.GetProperty("input_audio_format").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("turn_detection").ValueKind);
        Assert.Equal(
            "gpt-4o-mini-transcribe",
            session.GetProperty("input_audio_transcription").GetProperty("model").GetString());
        Assert.Equal(
            "near_field",
            session.GetProperty("input_audio_noise_reduction").GetProperty("type").GetString());
        Assert.False(session.GetProperty("input_audio_transcription").TryGetProperty("language", out _));
    }

    [Fact]
    public void SessionUpdateIncludesLanguageAndFarField()
    {
        string json = RealtimeProtocol.BuildSessionUpdate(
            "gpt-4o-mini-transcribe", language: "en", nearFieldMic: false);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement session = document.RootElement.GetProperty("session");

        Assert.Equal(
            "en",
            session.GetProperty("input_audio_transcription").GetProperty("language").GetString());
        Assert.Equal(
            "far_field",
            session.GetProperty("input_audio_noise_reduction").GetProperty("type").GetString());
    }

    [Fact]
    public void AudioAppendCarriesBase64Pcm()
    {
        byte[] pcm = [1, 2, 3, 4, 5];
        string json = RealtimeProtocol.BuildAudioAppend(pcm);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("input_audio_buffer.append", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(pcm, Convert.FromBase64String(document.RootElement.GetProperty("audio").GetString()!));
    }

    [Fact]
    public void CommitIsWellFormed()
    {
        using JsonDocument document = JsonDocument.Parse(RealtimeProtocol.BuildCommit());
        Assert.Equal("input_audio_buffer.commit", document.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(
        """{"type":"conversation.item.input_audio_transcription.completed","transcript":"hi there"}""",
        RealtimeProtocol.EventKind.TranscriptionCompleted, "hi there")]
    [InlineData(
        """{"type":"conversation.item.input_audio_transcription.delta","delta":"hi"}""",
        RealtimeProtocol.EventKind.TranscriptionDelta, "hi")]
    [InlineData(
        """{"type":"error","error":{"message":"bad key"}}""",
        RealtimeProtocol.EventKind.Error, "bad key")]
    [InlineData(
        """{"type":"session.updated"}""",
        RealtimeProtocol.EventKind.Other, "")]
    [InlineData("not json at all", RealtimeProtocol.EventKind.Other, "")]
    [InlineData("""{"no_type":true}""", RealtimeProtocol.EventKind.Other, "")]
    public void ParsesServerEvents(string json, RealtimeProtocol.EventKind expectedKind, string expectedPayload)
    {
        (RealtimeProtocol.EventKind kind, string payload) = RealtimeProtocol.ParseEvent(json);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedPayload, payload);
    }
}
