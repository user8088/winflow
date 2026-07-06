using WinFlow.Core.Local;
using WinFlow.Core.Models;

namespace WinFlow.Core.Tests;

public class SttModeControllerTests
{
    [Fact]
    public void CloudModeResolvesToCloud()
    {
        var controller = new SttModeController(SttMode.Cloud, () => true, () => true);
        Assert.Equal(SttBackend.Cloud, controller.ResolvedBackend);
    }

    [Fact]
    public void LocalModeResolvesToLocalWhenAvailable()
    {
        var controller = new SttModeController(SttMode.Local, () => true, () => true);
        Assert.Equal(SttBackend.Local, controller.ResolvedBackend);
    }

    [Fact]
    public void AutoPrefersLocalWhenModelInstalled()
    {
        var controller = new SttModeController(SttMode.Auto, () => true, () => true);
        Assert.Equal(SttBackend.Local, controller.ResolvedBackend);
    }

    [Fact]
    public void AutoFallsBackToCloudWhenLocalMissing()
    {
        var controller = new SttModeController(SttMode.Auto, () => true, () => false);
        Assert.Equal(SttBackend.Cloud, controller.ResolvedBackend);
    }

    [Fact]
    public void ApplyRaisesBackendChangedOnlyOnFlip()
    {
        var controller = new SttModeController(SttMode.Cloud, () => true, () => true);
        var changes = new List<SttBackend>();
        controller.BackendChanged += b => changes.Add(b);

        controller.Apply(SttMode.Local); // Cloud -> Local: fires
        controller.Apply(SttMode.Local); // no change: silent
        controller.Apply(SttMode.Cloud); // Local -> Cloud: fires

        Assert.Equal([SttBackend.Local, SttBackend.Cloud], changes);
    }

    [Fact]
    public void NotifyLocalAvailabilityResolvesAuto()
    {
        bool installed = false;
        var controller = new SttModeController(SttMode.Auto, () => true, () => installed);
        Assert.Equal(SttBackend.Cloud, controller.ResolvedBackend);

        var changes = new List<SttBackend>();
        controller.BackendChanged += b => changes.Add(b);

        installed = true;
        controller.NotifyLocalAvailabilityChanged();

        Assert.Equal(SttBackend.Local, controller.ResolvedBackend);
        Assert.Equal([SttBackend.Local], changes);
    }
}
