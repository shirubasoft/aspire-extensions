using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageTagTests
{
    [Theory]
    [InlineData("feature/orders/api", "feature-orders-api")]
    [InlineData("Release Candidate #1", "release-candidate-1")]
    [InlineData("refs/heads/Fix_Bug.2", "refs-heads-fix_bug.2")]
    [InlineData("---", "latest")]
    [InlineData(null, "latest")]
    public void Branch_name_is_sanitized_as_a_container_image_tag(string? branch, string expected)
    {
        Assert.Equal(expected, ModuleImageTag.FromBranch(branch));
    }

    [Fact]
    public void Branch_tag_is_limited_to_distribution_tag_length()
    {
        var tag = ModuleImageTag.FromBranch(new string('a', 200));

        Assert.Equal(128, tag.Length);
        Assert.All(tag, character => Assert.Equal('a', character));
    }

    [Fact]
    public void Dirty_suffix_preserves_the_distribution_tag_length_limit()
    {
        var dirtyTag = ModuleImageTag.AppendDirtySuffix(new string('a', 128));

        Assert.Equal(128, dirtyTag.Length);
        Assert.EndsWith("-dirty", dirtyTag, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("feature/orders", "ABCDEF0123456789", "feature-orders-abcdef012345")]
    [InlineData(null, "ABCDEF0123456789", "sha-abcdef012345")]
    [InlineData("feature/orders", null, "feature-orders")]
    public void Repository_tag_includes_the_short_commit_when_available(
        string? branch,
        string? commit,
        string expected)
    {
        Assert.Equal(expected, ModuleImageTag.FromRepository(branch, commit));
    }

    [Fact]
    public void Commit_suffix_preserves_the_distribution_tag_length_limit()
    {
        var tag = ModuleImageTag.FromRepository(new string('a', 200), "abcdef0123456789");

        Assert.Equal(128, tag.Length);
        Assert.EndsWith("-abcdef012345", tag, StringComparison.Ordinal);
    }
}
