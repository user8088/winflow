using WinFlow.Core.Models;

namespace WinFlow.Core.Abstractions;

/// <summary>
/// Watches the global push-to-talk key and reports press/release
/// transitions. Auto-repeat while the key is held must be filtered out
/// by the implementation.
/// </summary>
public interface IHotkeyProvider : IDisposable
{
    /// <summary>
    /// Raised off the hook thread — handlers may do real work, but events
    /// for one provider are always delivered sequentially and in order.
    /// </summary>
    event Action<HotkeyEvent>? HotkeyChanged;

    void Start();

    void Stop();
}
