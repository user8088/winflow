namespace WinFlow.Core.Injection;

/// <summary>
/// Releases modifier keys after injection so a partial Ctrl+V sequence cannot
/// leave Ctrl stuck and turn the next keypress into a shortcut.
/// </summary>
internal static class ModifierRelease
{
    private const uint KeyeventfKeyup = 0x0002;

    private static readonly ushort[] ModifierVks =
    [
        0x10, // VK_SHIFT
        0x11, // VK_CONTROL
        0x12, // VK_MENU (Alt)
        0xA0, // VK_LSHIFT
        0xA1, // VK_RSHIFT
        0xA2, // VK_LCONTROL
        0xA3, // VK_RCONTROL
        0xA4, // VK_LMENU
        0xA5, // VK_RMENU
    ];

    public static void ReleaseAll()
    {
        var inputs = new NativeInput.Input[ModifierVks.Length];
        for (var i = 0; i < ModifierVks.Length; i++)
        {
            inputs[i] = new NativeInput.Input
            {
                Type = 1,
                VirtualKey = ModifierVks[i],
                Flags = KeyeventfKeyup,
            };
        }

        NativeInput.Send(inputs);
    }
}
