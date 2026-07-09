namespace WinFlow.Core.Injection;

/// <summary>
/// An in-process copy of every duplicable clipboard format (text, HTML/RTF,
/// DIB images, file drop lists, registered formats), taken before paste
/// injection overwrites the clipboard and restored afterwards. Created via
/// <see cref="ClipboardHelper.TrySnapshot"/> and consumed by
/// <see cref="ClipboardHelper.TryRestore"/>.
/// </summary>
public sealed class ClipboardSnapshot
{
    internal readonly record struct Entry(uint Format, byte[] Data);

    internal ClipboardSnapshot(IReadOnlyList<Entry> entries) => Entries = entries;

    internal IReadOnlyList<Entry> Entries { get; }

    /// <summary>True when the clipboard held nothing we could capture.</summary>
    public bool IsEmpty => Entries.Count == 0;
}
