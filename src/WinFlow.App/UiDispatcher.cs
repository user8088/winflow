using System.Windows;

namespace WinFlow.App;

/// <summary>
/// Marshals work onto the WPF application dispatcher when called from background threads.
/// </summary>
internal static class UiDispatcher
{
    public static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
