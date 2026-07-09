using System.Net.WebSockets;
using System.Text;
using WinFlow.Core.Abstractions;

namespace WinFlow.Core.Net;

/// <summary>
/// Streaming transcription over the OpenAI Realtime API.
///
/// To avoid paying the TLS/WebSocket handshake (~300ms) on every dictation,
/// a warm backup connection is pre-opened after each session and adopted by
/// the next <see cref="OpenSessionAsync"/>. Backups older than
/// <see cref="MaxBackupAge"/> are discarded (idle sockets get dropped by
/// intermediaries), matching freeflow's 180s policy.
/// </summary>
public sealed class OpenAIRealtimeClient : IStreamingSttProvider, IDisposable
{
    public static readonly TimeSpan MaxBackupAge = TimeSpan.FromSeconds(180);

    private readonly Func<string?> _apiKeyProvider;
    private readonly string _realtimeModel;
    private readonly string _sttModel;
    private readonly bool _nearFieldMic;
    private readonly Uri _endpoint;

    private readonly Lock _gate = new();
    private ClientWebSocket? _backup;
    private DateTime _backupOpenedAt;
    private bool _warmupInFlight;

    public OpenAIRealtimeClient(
        Func<string?> apiKeyProvider,
        string realtimeModel = "gpt-4o-realtime-preview",
        string sttModel = "gpt-4o-mini-transcribe",
        bool nearFieldMic = true,
        Uri? endpoint = null)
    {
        _apiKeyProvider = apiKeyProvider;
        _realtimeModel = realtimeModel;
        _sttModel = sttModel;
        _nearFieldMic = nearFieldMic;
        _endpoint = endpoint ?? new Uri("wss://api.openai.com/v1/realtime");
    }

    public async Task<IStreamingSttSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        ClientWebSocket socket = TakeFreshBackup() ?? await ConnectAsync(cancellationToken).ConfigureAwait(false);

        // Until the Session below takes ownership, any failure (e.g. the
        // session.update send hitting a half-closed backup socket) must
        // dispose the socket here or it leaks.
        try
        {
            string sessionUpdate = RealtimeProtocol.BuildSessionUpdate(_sttModel, language: null, _nearFieldMic);
            await SendTextAsync(socket, sessionUpdate, cancellationToken).ConfigureAwait(false);

            return new Session(this, socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Pre-opens the next connection so the next dictation skips the handshake.</summary>
    public void WarmUpInBackground()
    {
        lock (_gate)
        {
            if (_backup is not null || _warmupInFlight)
            {
                return;
            }

            _warmupInFlight = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                ClientWebSocket socket = await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _backup = socket;
                    _backupOpenedAt = DateTime.UtcNow;
                }
            }
            catch
            {
                // Warm-up is best-effort; the next session just connects fresh.
            }
            finally
            {
                lock (_gate)
                {
                    _warmupInFlight = false;
                }
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _backup?.Dispose();
            _backup = null;
        }
    }

    private ClientWebSocket? TakeFreshBackup()
    {
        lock (_gate)
        {
            ClientWebSocket? backup = _backup;
            _backup = null;
            if (backup is null)
            {
                return null;
            }

            if (backup.State == WebSocketState.Open
                && DateTime.UtcNow - _backupOpenedAt < MaxBackupAge)
            {
                return backup;
            }

            backup.Dispose();
            return null;
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        string? apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No OpenAI API key is configured. Set one from the tray menu.");
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var url = new Uri($"{_endpoint}?model={Uri.EscapeDataString(_realtimeModel)}");
        try
        {
            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            WebSocketReceiveResult result = await socket
                .ReceiveAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Server closed the connection.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    private sealed class Session : IStreamingSttSession
    {
        private readonly OpenAIRealtimeClient _owner;
        private readonly ClientWebSocket _socket;
        private int _audioBytesSent;

        public Session(OpenAIRealtimeClient owner, ClientWebSocket socket)
        {
            _owner = owner;
            _socket = socket;
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
        {
            if (pcm16.IsEmpty)
            {
                return Task.CompletedTask;
            }

            _audioBytesSent += pcm16.Length;
            return SendTextAsync(_socket, RealtimeProtocol.BuildAudioAppend(pcm16.Span), cancellationToken);
        }

        public async Task<string> FinishAsync(CancellationToken cancellationToken = default)
        {
            // The server rejects a commit on an empty buffer.
            if (_audioBytesSent == 0)
            {
                return "";
            }

            await SendTextAsync(_socket, RealtimeProtocol.BuildCommit(), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                string json = await ReceiveTextAsync(_socket, cancellationToken).ConfigureAwait(false);
                (RealtimeProtocol.EventKind kind, string payload) = RealtimeProtocol.ParseEvent(json);

                switch (kind)
                {
                    case RealtimeProtocol.EventKind.TranscriptionCompleted:
                        return payload.Trim();
                    case RealtimeProtocol.EventKind.Error:
                        throw new InvalidOperationException($"Realtime API error: {payload}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket
                        .CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort close; the socket is being discarded either way.
            }
            finally
            {
                _socket.Dispose();
                _owner.WarmUpInBackground();
            }
        }
    }
}
