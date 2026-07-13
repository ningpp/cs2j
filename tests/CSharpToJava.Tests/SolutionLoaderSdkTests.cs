using CSharpToJava.Core.Workspace;

namespace CSharpToJava.Tests;

public class SolutionLoaderSdkTests
{
    [Theory]
    [InlineData("10.0.300", 10, 0, 300)]
    [InlineData("2.1.818", 2, 1, 818)]
    [InlineData("8.0.100-preview.1", 8, 0, 100)]
    [InlineData("9.0.100-rc.2", 9, 0, 100)]
    [InlineData("not-a-version", null, null, null)]
    public void TryParseSdkVersion_ExtractsLeadingNumericVersion(
        string directoryName,
        int? expectedMajor,
        int? expectedMinor,
        int? expectedBuild)
    {
        var version = SolutionLoader.TryParseSdkVersion(directoryName);

        if (expectedMajor == null)
        {
            Assert.Null(version);
            return;
        }

        Assert.NotNull(version);
        Assert.Equal(expectedMajor.Value, version.Major);
        Assert.Equal(expectedMinor.GetValueOrDefault(), version.Minor);
        Assert.Equal(expectedBuild.GetValueOrDefault(), version.Build);
    }

    [Fact]
    public void TryParseSdkVersion_SortByVersion_PicksLatestSdkOverStringOrdering()
    {
        var directoryNames = new[]
        {
            "2.1.818",
            "10.0.300",
            "8.0.100",
        };

        var latest = directoryNames
            .Select(d => new { Name = d, Version = SolutionLoader.TryParseSdkVersion(d) })
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .First();

        Assert.Equal("10.0.300", latest.Name);
    }
}
