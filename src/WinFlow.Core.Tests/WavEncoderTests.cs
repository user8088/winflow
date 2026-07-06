using System.Buffers.Binary;
using System.Text;
using WinFlow.Core.Audio;

namespace WinFlow.Core.Tests;

public class WavEncoderTests
{
    [Fact]
    public void ProducesValidRiffHeaderForMono24k()
    {
        byte[] pcm = new byte[24000 * 2]; // exactly one second
        byte[] wav = WavEncoder.Encode(pcm, sampleRate: 24000);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));      // mono
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));  // sample rate
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));  // byte rate
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));      // block align
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));     // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Fact]
    public void CopiesPcmPayloadVerbatim()
    {
        byte[] pcm = [0x01, 0x02, 0x03, 0x04];
        byte[] wav = WavEncoder.Encode(pcm, sampleRate: 24000);

        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void StereoAdjustsByteRateAndBlockAlign()
    {
        byte[] wav = WavEncoder.Encode(new byte[8], sampleRate: 44100, channels: 2);

        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(44100 * 4, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(4, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
    }
}
