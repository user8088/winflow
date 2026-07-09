namespace WinFlow.Core.Audio;

/// <summary>
/// Little-endian 16-bit signed PCM encode/decode helpers.
/// </summary>
public static class Pcm16Codec
{
    public static short DecodeSample(ReadOnlySpan<byte> pcm16, int sampleIndex)
    {
        int i = sampleIndex * 2;
        return (short)(pcm16[i] | (pcm16[i + 1] << 8));
    }

    public static float SampleToFloat(short sample) => sample / 32768f;

    public static short FloatToSample(float sample) =>
        (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);

    public static void WriteSample(Span<byte> pcm16, int sampleIndex, short sample)
    {
        int i = sampleIndex * 2;
        pcm16[i] = (byte)(sample & 0xFF);
        pcm16[i + 1] = (byte)((ushort)(sample >> 8));
    }

    public static float[] ToFloatSamples(ReadOnlySpan<byte> pcm16)
    {
        int sampleCount = pcm16.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = SampleToFloat(DecodeSample(pcm16, i));
        }

        return samples;
    }

    public static void WriteFloatSamples(ReadOnlySpan<float> floats, Span<byte> pcm16)
    {
        for (int i = 0; i < floats.Length; i++)
        {
            WriteSample(pcm16, i, FloatToSample(floats[i]));
        }
    }
}
