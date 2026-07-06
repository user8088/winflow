namespace WinFlow.Core.Local.Models;

/// <summary>
/// One file belonging to a downloadable local model. <see cref="Sha256"/>
/// is the git-lfs object id (the SHA256 of the file content) for LFS files;
/// null for small non-LFS files that are verified by size only.
/// </summary>
public readonly record struct LocalModelFile(
    string RelativePath,
    string Url,
    long Size,
    string? Sha256);

/// <summary>
/// A downloadable, self-contained speech model for offline transcription.
/// </summary>
public sealed record LocalModelDescriptor(
    string Id,
    string DisplayName,
    string ModelType,
    int SampleRate,
    long TotalBytes,
    IReadOnlyList<LocalModelFile> Files);
