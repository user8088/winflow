using WinFlow.Core.Local.Models;

namespace WinFlow.Core.Tests;

public class LocalModelCatalogTests
{
    [Fact]
    public void DefaultModelIsParakeetTdt()
    {
        LocalModelDescriptor model = LocalModelCatalog.Default;

        Assert.Equal(LocalModelCatalog.ParakeetTdt06bV2Int8, model.Id);
        Assert.Equal("nemo_transducer", model.ModelType);
        Assert.Equal(16000, model.SampleRate);
    }

    [Fact]
    public void ParakeetHasEncoderDecoderJoinerAndTokens()
    {
        LocalModelDescriptor model = LocalModelCatalog.ParakeetTdt06bV2;

        IEnumerable<string> paths = model.Files.Select(f => f.RelativePath);
        Assert.Contains("encoder.int8.onnx", paths);
        Assert.Contains("decoder.int8.onnx", paths);
        Assert.Contains("joiner.int8.onnx", paths);
        Assert.Contains("tokens.txt", paths);
    }

    [Fact]
    public void LargeFilesCarryPinnedSha256()
    {
        foreach (LocalModelFile file in LocalModelCatalog.Default.Files)
        {
            // The encoder dominates the download; it must be verifiable.
            if (file.Size > 1_000_000)
            {
                Assert.NotNull(file.Sha256);
                Assert.Equal(64, file.Sha256!.Length); // SHA256 hex
            }

            Assert.True(file.Size > 0);
            Assert.StartsWith("https://", file.Url);
        }
    }

    [Fact]
    public void TotalBytesMatchesFileSizes()
    {
        LocalModelDescriptor model = LocalModelCatalog.Default;
        Assert.Equal(model.Files.Sum(f => f.Size), model.TotalBytes);
    }

    [Fact]
    public void FindResolvesKnownId()
    {
        Assert.Equal(
            LocalModelCatalog.ParakeetTdt06bV2,
            LocalModelCatalog.Find(LocalModelCatalog.ParakeetTdt06bV2Int8));
        Assert.Null(LocalModelCatalog.Find("does-not-exist"));
    }
}
