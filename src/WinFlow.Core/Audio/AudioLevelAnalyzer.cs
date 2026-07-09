namespace WinFlow.Core.Audio;

public static class AudioLevelAnalyzer
{
    /// <summary>
    /// RMS level of a 16-bit signed PCM buffer, normalized to [0, 1].
    /// </summary>
    public static float ComputeRms(ReadOnlySpan<byte> pcm16)
    {
        int sampleCount = pcm16.Length / 2;
        if (sampleCount == 0)
        {
            return 0f;
        }

        double sumOfSquares = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double normalized = Pcm16Codec.SampleToFloat(Pcm16Codec.DecodeSample(pcm16, i));
            sumOfSquares += normalized * normalized;
        }

        return (float)Math.Sqrt(sumOfSquares / sampleCount);
    }
}
