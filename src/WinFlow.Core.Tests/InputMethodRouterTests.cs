using WinFlow.Core.Abstractions;
using WinFlow.Core.Injection;
using WinFlow.Core.Models;

namespace WinFlow.Core.Tests;

public class InputMethodRouterTests
{
    [Fact]
    public void Constructor_SetsMethod()
    {
        var router = CreateRouter(InputMethod.Type);

        Assert.Equal(InputMethod.Type, router.Method);
    }

    [Theory]
    [InlineData(InputMethod.Paste)]
    [InlineData(InputMethod.Type)]
    [InlineData(InputMethod.Auto)]
    public void Method_PassthroughConfiguredMode(InputMethod method)
    {
        var router = CreateRouter(method);

        Assert.Equal(method, router.Method);
    }

    [Fact]
    public void Method_CanBeUpdatedAfterConstruction()
    {
        var router = CreateRouter(InputMethod.Paste);

        router.Method = InputMethod.Type;

        Assert.Equal(InputMethod.Type, router.Method);
    }

    [Fact]
    public async Task PasteMode_RoutesToPasteInjector()
    {
        var paste = new TrackingInjector("paste");
        var type = new TrackingInjector("type");
        var router = new InputMethodRouter(paste, type, InputMethod.Paste);

        await router.InjectAsync("hello");

        Assert.Equal(["hello"], paste.Calls);
        Assert.Empty(type.Calls);
    }

    [Fact]
    public async Task TypeMode_RoutesToTypeInjector()
    {
        var paste = new TrackingInjector("paste");
        var type = new TrackingInjector("type");
        var router = new InputMethodRouter(paste, type, InputMethod.Type);

        await router.InjectAsync("hello");

        Assert.Empty(paste.Calls);
        Assert.Equal(["hello"], type.Calls);
    }

    [Fact]
    public async Task AutoMode_RoutesToExactlyOneInjector()
    {
        var paste = new TrackingInjector("paste");
        var type = new TrackingInjector("type");
        var router = new InputMethodRouter(paste, type, InputMethod.Auto);

        await router.InjectAsync("auto text");

        int totalCalls = paste.Calls.Count + type.Calls.Count;
        Assert.Equal(1, totalCalls);
        Assert.Equal("auto text", paste.Calls.Concat(type.Calls).Single());
    }

    [Fact]
    public async Task InjectAsync_PassesCancellationToken()
    {
        var paste = new TrackingInjector("paste");
        var type = new TrackingInjector("type");
        var router = new InputMethodRouter(paste, type, InputMethod.Paste);
        using var cts = new CancellationTokenSource();

        await router.InjectAsync("token test", cts.Token);

        Assert.Equal(cts.Token, paste.LastToken);
    }

    private static InputMethodRouter CreateRouter(InputMethod method)
    {
        var paste = new TrackingInjector("paste");
        var type = new TrackingInjector("type");
        return new InputMethodRouter(paste, type, method);
    }

    private sealed class TrackingInjector(string tag) : ITextInjector
    {
        public string Tag { get; } = tag;

        public List<string> Calls { get; } = [];

        public CancellationToken LastToken { get; private set; }

        public Task InjectAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls.Add(text);
            LastToken = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
