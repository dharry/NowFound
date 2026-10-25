using System.Diagnostics;
using NowFound.Indexer;

namespace NowFound.UnitTests;

public class IndexerOptionsTests
{
    private static IndexerOptions Parse(params string[] args) => IndexerOptions.Parse(args);

    [Fact]
    public void NoArguments_UsesDefaults()
    {
        var options = Parse();

        Assert.Equal("main.db", options.MainDb);
        Assert.Equal("files.db", options.FilesDb);
        Assert.False(options.Full);
        Assert.False(options.ShowHelp);
        Assert.Empty(options.Errors);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-help")]
    [InlineData("-HELP")]
    [InlineData("--help")]
    [InlineData("--HELP")]
    [InlineData("-H")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void HelpFlags_ShowHelp(string flag)
    {
        var options = Parse(flag);

        Assert.True(options.ShowHelp);
        Assert.Empty(options.Errors);
    }

    [Fact]
    public void Help_WinsEvenWhenOtherArgumentsAreWrong()
    {
        var options = Parse("--bogus", "--main-db", "--help");

        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void Options_AreParsed_InAnyOrderAndCase()
    {
        var options = Parse("--FULL", "--files-db", @"D:\data\files.db", "--Main-Db", @"D:\data\main.db");

        Assert.Equal(@"D:\data\main.db", options.MainDb);
        Assert.Equal(@"D:\data\files.db", options.FilesDb);
        Assert.True(options.Full);
        Assert.Empty(options.Errors);
    }

    [Fact]
    public void EqualsSyntax_IsAccepted()
    {
        var options = Parse(@"--main-db=D:\a b\main.db", "--files-db=files.db");

        Assert.Equal(@"D:\a b\main.db", options.MainDb);
        Assert.Equal("files.db", options.FilesDb);
        Assert.Empty(options.Errors);
    }

    [Theory]
    [InlineData("--main-db")]                          // 値なし（末尾）
    [InlineData("--files-db", "--full")]               // 値の位置に別のオプション
    [InlineData("--main-db", "--help2")]
    [InlineData("--main-db=")]                         // = の後が空
    public void MissingValue_IsReported(params string[] args)
    {
        var options = Parse(args);

        Assert.Single(options.Errors, e => e.Contains("requires a value"));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("positional.db")]
    [InlineData("--full=1")]
    [InlineData("-x")]
    public void UnknownArguments_AreReported(string arg)
    {
        var options = Parse(arg);

        Assert.Single(options.Errors, e => e.Contains("Unknown argument") && e.Contains(arg));
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void MissingValue_DoesNotSwallowTheNextOption()
    {
        var options = Parse("--main-db", "--full");

        Assert.True(options.Full);
        Assert.Single(options.Errors);
        Assert.Equal("main.db", options.MainDb);
    }

    [Fact]
    public void Usage_DescribesEveryAcceptedOption_Defaults_ModesAndExitCodes()
    {
        var usage = IndexerOptions.Usage("NowFound.Indexer");

        foreach (var token in new[] { "--main-db", "--files-db", "--full", "-h, --help" })
        {
            Assert.Contains(token, usage);
        }

        Assert.Contains("NowFound.Indexer [options]", usage);
        Assert.Contains("Default: main.db", usage);
        Assert.Contains("Default: files.db", usage);
        Assert.Contains("fast mode", usage);
        Assert.Contains("--full", usage);
        Assert.Contains("Exit codes", usage);
        Assert.Contains("NowFound.Indexer --main-db", usage);
    }

    [Fact]
    public void Usage_DoesNotPromiseOptionsTheParserRejects()
    {
        var usage = IndexerOptions.Usage("NowFound.Indexer");

        // 使い方に書いた「--xxx」形式のオプションは、すべて解析できること。
        var documented = System.Text.RegularExpressions.Regex.Matches(usage, @"(?<=\s)--[a-z][a-z-]*")
            .Select(m => m.Value).Distinct().ToList();

        Assert.NotEmpty(documented);
        foreach (var option in documented)
        {
            var args = option is "--full" or "--help" ? new[] { option } : [option, "x"];
            Assert.DoesNotContain(IndexerOptions.Parse(args).Errors, e => e.Contains("Unknown argument"));
        }
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("-h", 0)]
    [InlineData("--bogus", 1)]
    public void RealProcess_PrintsUsageOrErrorWithTheExpectedExitCode(string argument, int expectedExitCode)
    {
        var dll = typeof(IndexerOptions).Assembly.Location;
        if (!File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")))
        {
            // 参照先アプリの実行構成がテスト出力に無い環境では、プロセス起動の確認を省く（解析自体は上のテストで確認済み）。
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" {argument}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // 文字コードに依存しないよう、判定は ASCII のオプション名だけで行う。
            WorkingDirectory = Path.GetTempPath(),
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "プロセスが終了しない");

        Assert.Equal(expectedExitCode, process.ExitCode);
        if (expectedExitCode == 0)
        {
            Assert.Contains("--main-db", stdout);
            Assert.Contains("--files-db", stdout);
            Assert.Contains("--full", stdout);
            Assert.Empty(stderr);
        }
        else
        {
            Assert.Contains("--help", stderr);
            Assert.Empty(stdout);
        }
    }
}
