namespace WinFlow.Core.Abstractions;

/// <summary>Delivers finished text into the currently focused application.</summary>
public interface ITextInjector
{
    Task InjectAsync(string text, CancellationToken cancellationToken = default);
}
