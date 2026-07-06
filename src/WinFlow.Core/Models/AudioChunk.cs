namespace WinFlow.Core.Models;

/// <summary>
/// A chunk of captured audio in the pipeline's canonical format
/// (16-bit signed PCM, mono, 24 kHz), plus its RMS level for HUD
/// metering and silence gating.
/// </summary>
public readonly record struct AudioChunk(byte[] Pcm16, float Rms);
