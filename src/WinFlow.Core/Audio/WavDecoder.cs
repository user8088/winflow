using System.Buffers.Binary;
using System.Text;

namespace WinFlow.Core.Audio;

public readonly record struct DecodedWav(byte[] Pcm16, int SampleRate, int Channels, int BitsPerSample);

public static class WavDecoder
{
    /// <summary>
    /// Parses a RIFF/WAVE container and extracts 16-bit PCM payload and fmt metadata.
    /// </summary>
    public static DecodedWav Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12)
            throw new InvalidDataException("WAV file too short.");

        if (Encoding.ASCII.GetString(wav[..4]) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");

        if (Encoding.ASCII.GetString(wav.Slice(8, 4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        int offset = 12;
        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        while (offset + 8 <= wav.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wav.Slice(offset, 4));
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            offset += 8;

            if (offset + chunkSize > wav.Length)
                throw new InvalidDataException($"Chunk '{chunkId}' extends past file end.");

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("fmt chunk too small.");

                int audioFormat = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(offset, 2));
                if (audioFormat != 1)
                    throw new InvalidDataException($"Unsupported WAV format {audioFormat}; only PCM (1) is supported.");

                channels = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(offset + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(offset + 14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = offset;
                dataLength = chunkSize;
            }

            offset += chunkSize + (chunkSize & 1);
        }

        if (sampleRate == 0)
            throw new InvalidDataException("WAV missing fmt chunk.");

        if (dataOffset < 0)
            throw new InvalidDataException("WAV missing data chunk.");

        if (bitsPerSample != 16)
            throw new InvalidDataException($"Unsupported bits per sample: {bitsPerSample}; only 16-bit PCM is supported.");

        byte[] pcm = wav.Slice(dataOffset, dataLength).ToArray();
        return new DecodedWav(pcm, sampleRate, channels, bitsPerSample);
    }
}
