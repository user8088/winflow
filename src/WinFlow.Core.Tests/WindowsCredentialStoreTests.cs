using WinFlow.Core.Security;

namespace WinFlow.Core.Tests;

/// <summary>
/// Integration tests against the real Windows Credential Manager, using a
/// test-only target name that is always cleaned up.
/// </summary>
public class WindowsCredentialStoreTests : IDisposable
{
    private readonly WindowsCredentialStore _store = new("WinFlow/Test");

    public void Dispose() => _store.DeleteApiKey();

    [Fact]
    public void ReturnsNullWhenNoKeySaved()
    {
        _store.DeleteApiKey();
        Assert.Null(_store.GetApiKey());
    }

    [Fact]
    public void RoundTripsKey()
    {
        _store.SetApiKey("sk-test-12345");
        Assert.Equal("sk-test-12345", _store.GetApiKey());
    }

    [Fact]
    public void OverwritesExistingKey()
    {
        _store.SetApiKey("sk-old");
        _store.SetApiKey("sk-new");
        Assert.Equal("sk-new", _store.GetApiKey());
    }

    [Fact]
    public void DeleteRemovesKeyAndIsIdempotent()
    {
        _store.SetApiKey("sk-doomed");
        _store.DeleteApiKey();
        _store.DeleteApiKey();
        Assert.Null(_store.GetApiKey());
    }
}
