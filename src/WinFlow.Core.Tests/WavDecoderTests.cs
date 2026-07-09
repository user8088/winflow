using System.Buffers.Binary;
using System.Text;
using WinFlow.Core.Audio;

namespace WinFlow.Core.Tests;

public class WavDecoderTests
{
    [Fact]
    public void DecodesCanonicalWavFromEncoder()
    {
        byte[] pcm = [0x01, 0x02, 0x03, 0x04];
        byte[] wav = WavEncoder.Encode(pcm, sampleRate: 24000);

        DecodedWav decoded = WavDecoder.Decode(wav);

        Assert.Equal(pcm, decoded.Pcm16);
        Assert.Equal(24000, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(16, decoded.BitsPerSample);
    }

    [Fact]
    public void FindsDataChunkAfterExtraRiffChunks()
    {
        byte[] pcm = [0x10, 0x20, 0x30, 0x40];
        byte[] wav = WavEncoder.Encode(pcm, sampleRate: 16000);

        byte[] junk = Encoding.ASCII.GetBytes("JUNK");
        byte[] junkPayload = [0x00, 0x01, 0x02, 0x03, 0x04]; // odd size -> padded
        byte[] withJunk = new byte[12 + 8 + junkPayload.Length + 1 + (wav.Length - 12)];
        wav.AsSpan(0, 12).CopyTo(withJunk);
        int offset = 12;
        junk.CopyTo(withJunk.AsSpan(offset, 4));
        BinaryPrimitives.WriteInt32LittleEndian(withJunk.AsSpan(offset + 4), junkPayload.Length);
        junkPayload.CopyTo(withJunk.AsSpan(offset + 8));
        offset += 8 + junkPayload.Length + 1;
        wav.AsSpan(12).CopyTo(withJunk.AsSpan(offset));
        BinaryPrimitives.WriteInt32LittleEndian(withJunk.AsSpan(4), withJunk.Length - 8);

        DecodedWav decoded = WavDecoder.Decode(withJunk);

        Assert.Equal(pcm, decoded.Pcm16);
        Assert.Equal(16000, decoded.SampleRate);
    }
}
