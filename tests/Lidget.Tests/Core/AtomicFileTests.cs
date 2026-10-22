using Lidget.Core.Storage;

namespace Lidget.Tests.Core;

public class AtomicFileTests
{
    [Fact]
    public void CreatesNewFile()
    {
        using var dir = new TempDirectory();
        var path = dir.File("sub/new.json");

        AtomicFile.WriteAllText(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void ReplacesExistingFileAndLeavesNoTemporaryFile()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(["data.json"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void WritesUtf8WithoutByteOrderMark()
    {
        using var dir = new TempDirectory();
        var path = dir.File("data.json");

        AtomicFile.WriteAllText(path, "é");

        Assert.Equal([0xC3, 0xA9], File.ReadAllBytes(path));
    }
}
