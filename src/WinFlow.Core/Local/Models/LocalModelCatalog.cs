namespace WinFlow.Core.Local.Models;

/// <summary>
/// Catalog of downloadable local models. The default is NVIDIA Parakeet
/// TDT 0.6B v2 (int8) — the same model family freeflow uses locally, the
/// quality leader for English ASR. It runs faster than real-time on a
/// modern CPU via ONNX Runtime with no GPU required.
/// </summary>
public static class LocalModelCatalog
{
    public const string ParakeetTdt06bV2Int8 = "parakeet-tdt-0.6b-v2-int8";
    public const string Qwen25CorrectionGguf = "qwen2.5-0.5b-instruct-q4-k-m";

    private const string ParakeetBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/main";

    private const string QwenBase =
        "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main";

    /// <summary>
    /// Pinned git-lfs object ids (SHA256 of file content), taken from the
    /// HuggingFace <c>X-Linked-ETag</c> header. Verifies downloads at rest.
    /// </summary>
    private static readonly LocalModelFile[] ParakeetFiles =
    [
        new("encoder.int8.onnx", $"{ParakeetBase}/encoder.int8.onnx",
            652184296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),
        new("decoder.int8.onnx", $"{ParakeetBase}/decoder.int8.onnx",
            7257753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),
        new("joiner.int8.onnx", $"{ParakeetBase}/joiner.int8.onnx",
            1739080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),
        // tokens.txt is a small non-LFS file; verify by size only.
        new("tokens.txt", $"{ParakeetBase}/tokens.txt", 9384, null),
    ];

    public static LocalModelDescriptor ParakeetTdt06bV2 { get; } = new(
        Id: ParakeetTdt06bV2Int8,
        DisplayName: "Parakeet TDT 0.6B v2 (English, int8)",
        ModelType: "nemo_transducer", // sherpa-onnx NeMo transducer path (encoder/decoder/joiner)
        SampleRate: 16000,
        TotalBytes: 661190513,
        Files: ParakeetFiles);

    private static readonly LocalModelFile[] QwenCorrectionFiles =
    [
        new("qwen2.5-0.5b-instruct-q4_k_m.gguf",
            $"{QwenBase}/qwen2.5-0.5b-instruct-q4_k_m.gguf",
            491400032, "0663b826e09042345d6e2069d9655cd3ae84812d8f39a5894bed7b24fc0e86cc"),
    ];

    public static LocalModelDescriptor Qwen25Correction { get; } = new(
        Id: Qwen25CorrectionGguf,
        DisplayName: "Qwen 2.5 0.5B Instruct (correction, Q4)",
        ModelType: "gguf",
        SampleRate: 0,
        TotalBytes: 491400032,
        Files: QwenCorrectionFiles);

    public static LocalModelDescriptor Default => ParakeetTdt06bV2;

    public static LocalModelDescriptor? Find(string id) => id switch
    {
        ParakeetTdt06bV2Int8 => ParakeetTdt06bV2,
        Qwen25CorrectionGguf => Qwen25Correction,
        _ => null,
    };
}
