namespace WinFlow.Core.Injection;

/// <summary>
/// Thrown when keystroke injection typed some characters before SendInput
/// failed. The pipeline must not fall back to clipboard paste because that
/// would duplicate the already-typed prefix.
/// </summary>
public sealed class PartialTextInjectionException : InvalidOperationException
{
    public int CharsInjected { get; }

    public PartialTextInjectionException(int charsInjected, int win32Error)
        : base($"SendInput failed after typing {charsInjected} character(s) (error {win32Error}).")
    {
        CharsInjected = charsInjected;
    }
}
