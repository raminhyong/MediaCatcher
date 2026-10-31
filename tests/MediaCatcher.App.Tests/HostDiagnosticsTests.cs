using MediaCatcher.App.Services;

namespace MediaCatcher.App.Tests;

public sealed class HostDiagnosticsTests
{
    [Fact]
    public void ComputeExtensionId_rejects_invalid_base64()
    {
        Assert.Null(HostDiagnostics.ComputeExtensionId("not base64 !!"));
    }

    [Fact]
    public void ComputeExtensionId_maps_sha256_to_chromium_alphabet()
    {
        var key = Convert.ToBase64String(new byte[64]);
        var id = HostDiagnostics.ComputeExtensionId(key);

        Assert.NotNull(id);
        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.InRange(c, 'a', 'p'));
    }

    [Fact]
    public void Check_never_throws_and_reports_issues()
    {
        var issues = HostDiagnostics.Check();

        Assert.NotNull(issues);
        Assert.All(issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
    }
}
