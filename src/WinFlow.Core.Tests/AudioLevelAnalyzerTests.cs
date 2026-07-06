using WinFlow.Core.Audio;

namespace WinFlow.Core.Tests;

public class AudioLevelAnalyzerTests
{
    [Fact]
    public void SilenceIsZero()
    {
        Assert.Equal(0f, AudioLevelAnalyzer.ComputeRms(new byte[4800]));
    }

    [Fact]
    public void EmptyBufferIsZero()
    {
        Assert.Equal(0f, AudioLevelAnalyzer.ComputeRms(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FullScaleSquareWaveIsNearOne()
    {
        byte[] pcm = new byte[2000];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short sample = i % 4 == 0 ? short.MaxValue : short.MinValue;
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        Assert.InRange(AudioLevelAnalyzer.ComputeRms(pcm), 0.99f, 1.01f);
    }

    [Fact]
    public void SineWaveRmsIsAmplitudeOverSqrt2()
    {
        const double amplitude = 0.5;
        const int samples = 24000;
        byte[] pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short sample = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / 24000.0));
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        Assert.InRange(AudioLevelAnalyzer.ComputeRms(pcm), 0.34f, 0.36f); // 0.5 / √2 ≈ 0.354
    }
}
