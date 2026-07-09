using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.Core.Tests;

public class RecordingCoordinatorTests
{
    [Theory]
    [InlineData(RecordingState.Idle, RecordingState.Recording)]
    [InlineData(RecordingState.Recording, RecordingState.Processing)]
    [InlineData(RecordingState.Recording, RecordingState.Idle)]
    [InlineData(RecordingState.Processing, RecordingState.Idle)]
    public void AllowsValidTransitions(RecordingState from, RecordingState to)
    {
        var coordinator = new RecordingCoordinator();
        DriveTo(coordinator, from);

        Assert.True(coordinator.TryTransition(from, to));
        Assert.Equal(to, coordinator.State);
    }

    [Theory]
    [InlineData(RecordingState.Idle, RecordingState.Processing)]
    [InlineData(RecordingState.Processing, RecordingState.Recording)]
    public void RejectsInvalidTransitions(RecordingState from, RecordingState to)
    {
        var coordinator = new RecordingCoordinator();
        DriveTo(coordinator, from);

        Assert.False(coordinator.TryTransition(from, to));
        Assert.Equal(from, coordinator.State);
    }

    [Fact]
    public void RejectsTransitionWhenNotInFromState()
    {
        var coordinator = new RecordingCoordinator();

        Assert.False(coordinator.TryTransition(RecordingState.Recording, RecordingState.Processing));
        Assert.Equal(RecordingState.Idle, coordinator.State);
    }

    [Fact]
    public void RaisesStateChangedOnTransition()
    {
        var coordinator = new RecordingCoordinator();
        var observed = new List<RecordingState>();
        coordinator.StateChanged += observed.Add;

        coordinator.TryTransition(RecordingState.Idle, RecordingState.Recording);
        coordinator.TryTransition(RecordingState.Recording, RecordingState.Processing);

        Assert.Equal([RecordingState.Recording, RecordingState.Processing], observed);
    }

    [Fact]
    public void DoesNotRaiseStateChangedOnRejectedTransition()
    {
        var coordinator = new RecordingCoordinator();
        int raised = 0;
        coordinator.StateChanged += _ => raised++;

        coordinator.TryTransition(RecordingState.Recording, RecordingState.Processing);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ResetForcesIdleFromAnyStateAndNotifies()
    {
        var coordinator = new RecordingCoordinator();
        DriveTo(coordinator, RecordingState.Processing);
        var observed = new List<RecordingState>();
        coordinator.StateChanged += observed.Add;

        coordinator.Reset();

        Assert.Equal(RecordingState.Idle, coordinator.State);
        Assert.Equal([RecordingState.Idle], observed);
    }

    [Fact]
    public void ResetFromIdleIsSilent()
    {
        var coordinator = new RecordingCoordinator();
        int raised = 0;
        coordinator.StateChanged += _ => raised++;

        coordinator.Reset();

        Assert.Equal(0, raised);
    }

    private static void DriveTo(RecordingCoordinator coordinator, RecordingState target)
    {
        switch (target)
        {
            case RecordingState.Idle:
                return;
            case RecordingState.Recording:
                coordinator.TryTransition(RecordingState.Idle, RecordingState.Recording);
                return;
            case RecordingState.Processing:
                coordinator.TryTransition(RecordingState.Idle, RecordingState.Recording);
                coordinator.TryTransition(RecordingState.Recording, RecordingState.Processing);
                return;
        }
    }
}
