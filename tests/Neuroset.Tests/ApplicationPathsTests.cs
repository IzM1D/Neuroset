using FluentAssertions;
using Neuroset.Application;
using Neuroset.Security;
using Neuroset.Storage;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class ApplicationPathsTests
{
    [Fact]
    public void DefaultsKeepExistingInstallationPaths()
    {
        var local = Path.Combine(Path.GetTempPath(), "local-app-data");
        ApplicationPaths.ResolveDataDirectory(null, local).Should().Be(Path.Combine(local, "Neuroset"));
        DatabaseOptions.CreateDefault().DatabasePath.Should().Be(Path.Combine(ApplicationPaths.DataDirectory, "neuroset.db"));
        SecretStoreOptions.CreateDefault().FilePath.Should().Be(Path.Combine(ApplicationPaths.DataDirectory, "secrets.dat"));
        HtmlArtifactOptions.CreateDefault().RootDirectory.Should().Be(Path.Combine(ApplicationPaths.DataDirectory, "Artifacts"));
    }

    [Fact]
    public void IsolatedDataDirectoryRequiresAnAbsolutePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Neuroset-qa");
        ApplicationPaths.ResolveDataDirectory(path, "ignored").Should().Be(path);
        var relative = () => ApplicationPaths.ResolveDataDirectory("relative-data", Path.GetTempPath());
        relative.Should().Throw<ArgumentException>();
    }
}
