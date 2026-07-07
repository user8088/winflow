using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using WinFlow.Core.Local.Models;

namespace WinFlow.Core.Local;

/// <summary>
/// Downloads and verifies local speech models into
/// <c>%LOCALAPPDATA%\WinFlow\models\&lt;id&gt;</c>.
///
/// Downloads are resumable (HTTP Range from the partial file size) and
/// integrity-checked against the pinned git-lfs SHA256. HuggingFace
/// serves LFS content over a CDN that honours Range requests, so an
/// interrupted 600 MB encoder download picks up where it left off.
/// </summary>
public sealed class LocalModelManager : IDisposable
{
    public string ModelsRoot { get; private set; }

    public LocalModelManager(string? root = null)
    {
        // Priority: explicit root, then the persisted setting is applied by the
        // app via UseRoot, then WINFLOW_MODELS_DIR, then the default.
        ModelsRoot = ResolveRoot(root);
    }

    /// <summary>Switches the model store at runtime (e.g. from a folder picker).</summary>
    public void UseRoot(string root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            ModelsRoot = ResolveRoot(root);
        }
    }

    private static string ResolveRoot(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        // Legacy/env fallback. The app normally pins a persisted path via UseRoot.
        return Environment.GetEnvironmentVariable("WINFLOW_MODELS_DIR")
            ?? BundledModelsRoot()
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinFlow", "models");
    }

    /// <summary>
    /// The installer ships models in a <c>models</c> folder next to the exe,
    /// so everything lives under the install directory the user picked.
    /// </summary>
    private static string? BundledModelsRoot()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "models");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Free bytes on the drive holding <see cref="ModelsRoot"/>, or null if unknown.</summary>
    public long? GetAvailableBytes()
    {
        try
        {
            string? root = Path.GetPathRoot(ModelsRoot);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "WinFlow" } },
    };

    /// <summary>0..1 progress across all files combined, by byte count.</summary>
    public event Action<double, string>? ProgressChanged;

    public event Action<string>? FileCompleted;

    public string ModelDirectory(LocalModelDescriptor model) => Path.Combine(ModelsRoot, model.Id);

    public bool IsInstalled(LocalModelDescriptor model)
    {
        string dir = ModelDirectory(model);
        if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, ".verified")))
        {
            return false;
        }

        foreach (LocalModelFile file in model.Files)
        {
            string path = Path.Combine(dir, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
            {
                return false;
            }
        }

        return true;
    }

    public async Task EnsureInstalledAsync(
        LocalModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        string dir = ModelDirectory(model);
        Directory.CreateDirectory(dir);

        // Only fully verified files count as done; a partial file contributes
        // its actual bytes via output.Position during its own download.
        long completedBytes = 0;
        long totalBytes = model.TotalBytes;

        foreach (LocalModelFile file in model.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(dir, file.RelativePath);

            if (File.Exists(path) && new FileInfo(path).Length == file.Size
                && VerifyHash(path, file))
            {
                completedBytes += file.Size;
                continue; // already complete and intact
            }

            await DownloadFileAsync(file, path, completedBytes, totalBytes, cancellationToken)
                .ConfigureAwait(false);
            completedBytes += file.Size;
            FileCompleted?.Invoke(file.RelativePath);
        }

        // Mark verified only after every file passes its hash/size check.
        foreach (LocalModelFile file in model.Files)
        {
            if (!VerifyHash(Path.Combine(dir, file.RelativePath), file))
            {
                throw new InvalidDataException($"Integrity check failed for {file.RelativePath}.");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, ".verified"), model.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Delete(LocalModelDescriptor model)
    {
        string dir = ModelDirectory(model);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private async Task DownloadFileAsync(
        LocalModelFile file,
        string path,
        long completedBefore,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        long resumeFrom = File.Exists(path) ? new FileInfo(path).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (resumeFrom > 0 && resumeFrom < file.Size)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        using HttpResponseMessage response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // 416 Range Not Satisfiable means the file is already complete.
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return;
        }

        // If the server ignored the Range header (returns 200), restart from zero.
        StreamMode mode = response.StatusCode == System.Net.HttpStatusCode.PartialContent
            ? StreamMode.Append
            : StreamMode.Overwrite;

        response.EnsureSuccessStatusCode();

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            path,
            mode == StreamMode.Append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true);

        var buffer = new byte[65536];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            // FileMode.Append opens positioned at the resume offset, so
            // output.Position already includes previously downloaded bytes.
            long done = completedBefore + output.Position;
            double fraction = totalBytes == 0 ? 0 : (double)done / totalBytes;
            ProgressChanged?.Invoke(
                Math.Clamp(fraction, 0, 1),
                $"{file.RelativePath} ({FormatBytes(done)} / {FormatBytes(totalBytes)})");
        }
    }

    private static bool VerifyHash(string path, LocalModelFile file)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Size)
        {
            return false;
        }

        // Non-LFS files (tokens.txt) have no pinned SHA256; size match is enough.
        if (file.Sha256 is null)
        {
            return true;
        }

        using var stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return string.Equals(
            Convert.ToHexString(hash),
            file.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 20 ? $"{bytes / (double)(1 << 20):F0} MB"
        : bytes >= 1L << 10 ? $"{bytes / (double)(1 << 10):F0} KB"
        : $"{bytes} B";

    private enum StreamMode { Append, Overwrite }

    public void Dispose() => Http.Dispose();
}
