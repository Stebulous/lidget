using Lidget.Providers.Claude;

namespace Lidget.Tests.Providers;

public class ClaudeAccountConfigTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string json)
    {
        var path = _dir.File(".claude.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ReadsTheVersionAndTheOrganization()
    {
        var path = Write("""{"lastReleaseNotesSeen":"2.1.319","oauthAccount":{"organizationUuid":" org-1 "}}""");

        Assert.Equal("2.1.319", ClaudeAccountConfig.ReadClientVersion(path));
        Assert.Equal("org-1", ClaudeAccountConfig.ReadOrganizationId(path));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"lastReleaseNotesSeen":"2.1"}""")]
    [InlineData("""{"lastReleaseNotesSeen":"2.1.319 (beta)"}""")]
    [InlineData("""{"lastReleaseNotesSeen":21319}""")]
    [InlineData("""not json""")]
    public void UsesTheFallbackVersionWithoutAValidVersion(string json)
    {
        Assert.Equal(ClaudeAccountConfig.FallbackClientVersion, ClaudeAccountConfig.ReadClientVersion(Write(json)));
    }

    [Fact]
    public void AMissingFileGivesTheFallbackVersionAndNoOrganization()
    {
        var path = _dir.File("missing.json");

        Assert.Equal(ClaudeAccountConfig.FallbackClientVersion, ClaudeAccountConfig.ReadClientVersion(path));
        Assert.Null(ClaudeAccountConfig.ReadOrganizationId(path));
    }

    [Fact]
    public void TheUserAgentIsTheOneOfT3Code()
    {
        Assert.Equal("claude-cli/2.1.319 (external, cli)", ClaudeEndpoints.UserAgent("2.1.319"));
    }
}
