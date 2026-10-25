using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Information;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Information;

namespace NowFound.UnitTests;

public class HostInfoTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(17_179_869_184, "16.0 GB")]
    [InlineData(-1, "")]
    public void ByteSizeFormatter_UsesBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, ByteSizeFormatter.Format(bytes));

    [Fact]
    public void ParseCpuName_ReturnsFirstModelName()
    {
        string[] lines =
        [
            "processor\t: 0",
            "vendor_id\t: GenuineIntel",
            "model name\t: Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz",
            "model name\t: second",
        ];

        Assert.Equal("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", HostInfoService.ParseCpuName(lines));
    }

    [Theory]
    [InlineData("processor : 0", null)]
    [InlineData("model name :   ", null)]
    public void ParseCpuName_WithoutName_ReturnsNull(string line, string? expected) =>
        Assert.Equal(expected, HostInfoService.ParseCpuName([line]));

    [Fact]
    public void CollectHost_ReportsCurrentMachine()
    {
        var host = HostInfoService.CollectHost();

        Assert.Equal(Environment.MachineName, host.MachineName);
        Assert.False(string.IsNullOrWhiteSpace(host.OsDescription));
        Assert.True(host.LogicalProcessors > 0);
        Assert.True(host.TotalMemoryBytes > 0);
        Assert.True(host.ProcessMemoryBytes > 0);
    }

    [Fact]
    public void CollectRuntime_ReportsRunningDotNet()
    {
        var runtime = HostInfoService.CollectRuntime();

        Assert.Contains(".NET", runtime.FrameworkDescription);
        Assert.Equal(Environment.Version.ToString(), runtime.Version);
    }

    [Fact]
    public async Task GetBasic_ReturnsMachineNameAndRuntimeOnly()
    {
        await using var host = await TestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();

        var basic = scope.ServiceProvider.GetRequiredService<IHostInfoService>().GetBasic();

        Assert.Equal(Environment.MachineName, basic.MachineName);
        Assert.Equal(Environment.Version.ToString(), basic.Runtime.Version);
    }

    [Fact]
    public async Task GetAsync_ReturnsThreeDatabases_WithVersionPathAndSize_AndFilesCount()
    {
        await using var host = await TestHost.CreateAsync();
        await using (var seedScope = host.Services.CreateAsyncScope())
        {
            var seeder = new FilesSeeder(seedScope.ServiceProvider.GetRequiredService<FilesDbContext>());
            await seeder.AddAsync(1, "/data", "a.txt", ".txt", 10);
            await seeder.AddAsync(1, "/data", "b.txt", ".txt", 20);
            await seeder.AddAsync(1, "/data", "sub", isDirectory: true);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var info = await scope.ServiceProvider.GetRequiredService<IHostInfoService>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["main", "ope", "files"], info.Databases.Select(d => d.Name));
        Assert.All(info.Databases, d =>
        {
            Assert.Equal("SQLite", d.Provider);
            Assert.False(string.IsNullOrWhiteSpace(d.EngineVersion));
            Assert.False(string.IsNullOrWhiteSpace(d.Path));
            Assert.True(d.SizeBytes > 0);
        });
        var files = info.Databases.Single(d => d.Name == "files").Tables;
        Assert.Equal(3, files.Single(t => t.Table == "Files").Rows);
        Assert.Equal(1, files.Single(t => t.Table == "Directories").Rows);
        Assert.All(info.Databases, d => Assert.NotEmpty(d.Tables));
        Assert.Contains(info.Databases.Single(d => d.Name == "main").Tables, t => t.Table == "AspNetUsers" && t.Rows >= 1);
    }

    [Fact]
    public async Task GetAsync_WithEmptyFilesDb_CountsZero()
    {
        await using var host = await TestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();

        var info = await scope.ServiceProvider.GetRequiredService<IHostInfoService>().GetAsync(TestContext.Current.CancellationToken);

        Assert.All(info.Databases.Single(d => d.Name == "files").Tables, t => Assert.Equal(0, t.Rows));
    }
}
