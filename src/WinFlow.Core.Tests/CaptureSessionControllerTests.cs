using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WinFlow.Core.Abstractions;
using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.Core.Tests;

public class CaptureSessionControllerTests
{
    private static readonly CapturedAudio SampleTake = new(
        new byte[4800], 24000, TimeSpan.FromSeconds(0.1), 0.05f);

    private readonly IHotkeyProvider _hotkeys = Substitute.For<IHotkeyProvider>();
    private readonly IAudioProvider _audio = Substitute.For<IAudioProvider>();
    private readonly RecordingCoordinator _coordinator = new();

    private CaptureSessionController CreateController() =>
        new(_hotkeys, _audio, _coordinator);

    [Fact]
    public async Task PressedStartsAudioAndEntersRecording()
    {
        using var controller = CreateController();

        await controller.ProcessAsync(HotkeyEvent.Pressed());

        await _audio.Received(1).StartAsync();
        Assert.Equal(RecordingState.Recording, controller.State);
    }

    [Fact]
    public async Task ReleasedStopsAudioPublishesTakeAndReturnsToIdle()
    {
        _audio.StopAsync().Returns(SampleTake);
        using var controller = CreateController();
        CapturedAudio? completed = null;
        controller.CaptureCompleted += captured => completed = captured;

        await controller.ProcessAsync(HotkeyEvent.Pressed());
        await controller.ProcessAsync(HotkeyEvent.Released());

        Assert.Equal(SampleTake, completed);
        Assert.Equal(RecordingState.Idle, controller.State);
    }

    [Fact]
    public async Task ReleasedWithoutPressedIsIgnored()
    {
        using var controller = CreateController();

        await controller.ProcessAsync(HotkeyEvent.Released());

        await _audio.DidNotReceive().StopAsync();
        Assert.Equal(RecordingState.Idle, controller.State);
    }

    [Fact]
    public async Task SecondPressedWhileRecordingIsIgnored()
    {
        using var controller = CreateController();

        await controller.ProcessAsync(HotkeyEvent.Pressed());
        await controller.ProcessAsync(HotkeyEvent.Pressed());

        await _audio.Received(1).StartAsync();
    }

    [Fact]
    public async Task StartFailureReportsAndReturnsToIdle()
    {
        _audio.StartAsync().ThrowsAsync(new InvalidOperationException("no microphone"));
        using var controller = CreateController();
        Exception? failure = null;
        controller.CaptureFailed += exception => failure = exception;

        await controller.ProcessAsync(HotkeyEvent.Pressed());

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(RecordingState.Idle, controller.State);
    }

    [Fact]
    public async Task StopFailureReportsAndReturnsToIdle()
    {
        _audio.StopAsync().ThrowsAsync(new InvalidOperationException("device unplugged"));
        using var controller = CreateController();
        Exception? failure = null;
        controller.CaptureFailed += exception => failure = exception;

        await controller.ProcessAsync(HotkeyEvent.Pressed());
        await controller.ProcessAsync(HotkeyEvent.Released());

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(RecordingState.Idle, controller.State);
    }

    [Fact]
    public async Task FullSessionCanRepeat()
    {
        _audio.StopAsync().Returns(SampleTake);
        using var controller = CreateController();
        int completions = 0;
        controller.CaptureCompleted += _ => completions++;

        for (int i = 0; i < 3; i++)
        {
            await controller.ProcessAsync(HotkeyEvent.Pressed());
            await controller.ProcessAsync(HotkeyEvent.Released());
        }

        Assert.Equal(3, completions);
        await _audio.Received(3).StartAsync();
    }
}
