using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;

namespace WinFlow.Core.Mocks;

/// <summary>
/// Offline stand-in for both transcription paths. Used by tests and by
/// the app when WINFLOW_FAKE_STT=1, so the capture → race → inject flow
/// can be exercised end-to-end without credentials or network.
/// </summary>
public sealed class FakeSttProvider : IStreamingSttProvider, IBatchSttProvider
{
    public string Transcript { get; set; } = "Hello from WinFlow's fake transcriber.";

    public TimeSpan StreamingDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    public int SessionsOpened;
    public int AudioChunksReceived;
    public int BatchCalls;

    public Task<IStreamingSttSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref SessionsOpened);
        return Task.FromResult<IStreamingSttSession>(new FakeSession(this));
    }

    public async Task<string> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref BatchCalls);
        await Task.Delay(BatchDelay, cancellationToken).ConfigureAwait(false);
        return Transcript;
    }

    private sealed class FakeSession : IStreamingSttSession
    {
        private readonly FakeSttProvider _owner;

        public FakeSession(FakeSttProvider owner) => _owner = owner;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _owner.AudioChunksReceived);
            return Task.CompletedTask;
        }

        public async Task<string> FinishAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(_owner.StreamingDelay, cancellationToken).ConfigureAwait(false);
            return _owner.Transcript;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
