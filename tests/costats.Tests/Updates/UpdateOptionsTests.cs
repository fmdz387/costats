using costats.App.Services.Updates;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace costats.Tests.Updates;

public sealed class UpdateOptionsTests
{
    [Fact]
    public void FromConfiguration_defaults_to_fork_repository()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = UpdateOptions.FromConfiguration(configuration);

        Assert.Equal("RileyCornelius/costats", options.Repository);
    }

    [Fact]
    public void FromConfiguration_allows_repository_override()
    {
        var values = new Dictionary<string, string?>
        {
            ["Costats:Update:Repository"] = "ExampleOwner/example-repo"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = UpdateOptions.FromConfiguration(configuration);

        Assert.Equal("ExampleOwner/example-repo", options.Repository);
    }
}
