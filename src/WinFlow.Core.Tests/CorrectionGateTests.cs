using WinFlow.Core.Correction;
using WinFlow.Core.Models;

namespace WinFlow.Core.Tests;

public class CorrectionGateTests
{
    [Theory]
    [InlineData("Hello world.")]
    [InlineData("Please send the report by Friday.")]
    [InlineData("OK")]
    [InlineData("I like pizza.")]
    [InlineData("I actually agree.")]
    public void CleanTranscriptSkipsCorrection(string transcript)
    {
        Assert.False(CorrectionGate.NeedsCorrection(transcript));
    }

    [Theory]
    [InlineData("um I need to uh send the report")]
    [InlineData("I want to go to the store and")]
    [InlineData("no wait I mean the other file")]
    [InlineData("the the report is ready")]
    [InlineData("I went to the the store yesterday um")]
    [InlineData("um so like I was thinking no wait scratch that")]
    public void MessyTranscriptNeedsCorrection(string transcript)
    {
        Assert.True(CorrectionGate.NeedsCorrection(transcript));
    }
}
