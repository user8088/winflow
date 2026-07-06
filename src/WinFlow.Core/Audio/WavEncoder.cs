using System.Buffers.Binary;
using System.Text;

namespace WinFlow.Core.Audio;

public static class WavEncoder
{
    /// <summary>
    /// Wraps raw 16-bit signed PCM data in a canonical RIFF/WAVE container.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pcm16, int sampleRate, int channels = 1)
    {
        const int bitsPerSample = 16;
        int blockAlign = channels * bitsPerSample / 8;
        int byteRate = sampleRate * blockAlign;

        byte[] wav = new byte[44 + pcm16.Length];
        Span<byte> header = wav.AsSpan(0, 44);

        Encoding.ASCII.GetBytes("RIFF", header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + pcm16.Length);
        Encoding.ASCII.GetBytes("WAVE", header[8..12]);

        Encoding.ASCII.GetBytes("fmt ", header[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);            // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);             // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], bitsPerSample);

        Encoding.ASCII.GetBytes("data", header[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm16.Length);

        pcm16.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
