using NAudio.Wave;

namespace WinFlow.Core.Audio;

/// <summary>
/// Downmixes any channel count to mono by averaging all channels per frame.
/// </summary>
internal sealed class MonoAverageSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _frameBuffer = [];

    public MonoAverageSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceCount = count * _channels;
        if (_frameBuffer.Length < sourceCount)
        {
            _frameBuffer = new float[sourceCount];
        }

        int samplesRead = _source.Read(_frameBuffer, 0, sourceCount);
        int frames = samplesRead / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < _channels; channel++)
            {
                sum += _frameBuffer[frame * _channels + channel];
            }

            buffer[offset + frame] = sum / _channels;
        }

        return frames;
    }
}
