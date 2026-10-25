using Microsoft.Data.Sqlite;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public sealed class SqliteDataSourceTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), $"nowfound-ds-{Guid.NewGuid():N}");

    public SqliteDataSourceTests() => Directory.CreateDirectory(_baseDirectory);

    public void Dispose() => Directory.Delete(_baseDirectory, recursive: true);

    private static string DataSourceOf(string connectionString) => new SqliteConnectionStringBuilder(connectionString).DataSource;

    [Fact]
    public void Resolve_RelativePath_BecomesAbsoluteUnderBaseDirectory()
    {
        var result = SqliteDataSource.Resolve("Data Source=main.db", _baseDirectory);

        Assert.Equal(Path.Combine(_baseDirectory, "main.db"), DataSourceOf(result));
    }

    [Fact]
    public void Resolve_ParentRelativePath_IsNormalizedAndDirectoryCreated()
    {
        var web = Path.Combine(_baseDirectory, "web");
        Directory.CreateDirectory(web);

        var result = SqliteDataSource.Resolve("Data Source=../data/main.db", web);

        Assert.Equal(Path.Combine(_baseDirectory, "data", "main.db"), DataSourceOf(result));
        Assert.True(Directory.Exists(Path.Combine(_baseDirectory, "data")));
    }

    [Fact]
    public void Resolve_AbsolutePath_IsKept()
    {
        var absolute = Path.Combine(_baseDirectory, "abs", "files.db");

        var result = SqliteDataSource.Resolve($"Data Source={absolute}", Path.Combine(_baseDirectory, "other-base"));

        Assert.Equal(absolute, DataSourceOf(result));
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=InMemoryTest;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=file:test.db?mode=memory")]
    public void Resolve_InMemoryOrUri_IsUnchanged(string connectionString)
    {
        Assert.Equal(connectionString, SqliteDataSource.Resolve(connectionString, _baseDirectory));
    }
}
