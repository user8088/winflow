namespace WinFlow.Core.Models;

/// <summary>Where speech-to-text happens.</summary>
public enum SttMode
{
    /// <summary>Use OpenAI Realtime + batch (streaming during hold). Requires an API key.</summary>
    Cloud,

    /// <summary>Use the on-device Parakeet model. Free, offline, no key. Batch-only (transcribes after release).</summary>
    Local,

    /// <summary>Local when the model is installed; otherwise Cloud. (Future: prefer Local offline.)</summary>
    Auto,
}
