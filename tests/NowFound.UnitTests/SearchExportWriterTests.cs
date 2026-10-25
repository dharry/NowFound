using System.Text;
using NowFound.Application.Search;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class SearchExportWriterTests
{
    private static readonly ExportLabels Labels = new(
        ["Name", "Folder", "FullPath", "Type", "Size", "Ext", "Owner", "Modified", "Created"], "File", "Dir");

    private static SearchResultItem Item(
        string name, string folder = @"D:\docs", bool isDirectory = false, long? size = 10, string ext = "txt", string? owner = "alice") =>
        new(1, $@"{folder}\{name}", name, folder, isDirectory, size, ext, owner,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static async IAsyncEnumerable<SearchResultItem> Stream(IEnumerable<SearchResultItem> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async Task<(byte[] Bytes, int Rows)> WriteAsync(
        IEnumerable<SearchResultItem> items, ExportFormat format = ExportFormat.Csv, ExportEncoding encoding = ExportEncoding.Utf8Bom)
    {
        using var stream = new NonSeekableStream();
        var rows = await SearchExportWriter.WriteAsync(stream, Stream(items), format, encoding, Labels, TestContext.Current.CancellationToken);
        return (stream.ToArray(), rows);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("日本語.txt", "日本語.txt")]
    [InlineData("", "")]
    public void Field_Csv_QuotesOnlyWhenNeeded(string value, string expected) =>
        Assert.Equal(expected, SearchExportWriter.Field(value, ExportFormat.Csv));

    [Fact]
    public void Field_Null_IsEmpty() => Assert.Equal(string.Empty, SearchExportWriter.Field(null, ExportFormat.Csv));

    [Theory]
    [InlineData("a,b", "a,b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\r\nb", "a  b")]
    public void Field_Tsv_DoesNotQuote_AndReplacesSeparators(string value, string expected) =>
        Assert.Equal(expected, SearchExportWriter.Field(value, ExportFormat.Tsv));

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@x", "'@x")]
    public void Field_NeutralizesFormulaInjection_InBothFormats(string value, string expected)
    {
        Assert.Equal(expected, SearchExportWriter.Field(value, ExportFormat.Csv));
        Assert.Equal(expected, SearchExportWriter.Field(value, ExportFormat.Tsv));
    }

    [Fact]
    public async Task Write_Csv_EmitsBom_Header_AndRows()
    {
        var (bytes, rows) = await WriteAsync([Item("a.txt"), Item("sub", isDirectory: true, size: null, ext: "", owner: null)]);

        Assert.Equal(2, rows);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("Name,Folder,FullPath,Type,Size,Ext,Owner,Modified,Created", lines[0]);
        Assert.StartsWith(@"a.txt,D:\docs,D:\docs\a.txt,File,10,txt,alice,", lines[1]);
        Assert.StartsWith(@"sub,D:\docs,D:\docs\sub,Dir,,,,", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
    }

    [Fact]
    public async Task Write_Tsv_UsesTabsAndCrLf()
    {
        var (bytes, _) = await WriteAsync([Item("a.txt")], ExportFormat.Tsv);

        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("Name\tFolder\tFullPath\tType\tSize\tExt\tOwner\tModified\tCreated", lines[0]);
        Assert.Equal(9, lines[1].Split('\t').Length);
    }

    [Fact]
    public async Task Write_ShiftJis_HasNoBom_AndEncodesJapanese()
    {
        var (bytes, _) = await WriteAsync([Item("報告書.txt")], encoding: ExportEncoding.ShiftJis);

        Assert.NotEqual(0xEF, bytes[0]);
        var text = SearchExportWriter.GetEncoding(ExportEncoding.ShiftJis).GetString(bytes);
        Assert.Contains("報告書.txt", text);
        Assert.NotEqual(Encoding.UTF8.GetString(bytes), text);
    }

    [Fact]
    public async Task Write_DoesNotNeutralizeNumbers_ButNeutralizesNames()
    {
        var (bytes, _) = await WriteAsync([Item("=evil.txt", size: 5)]);

        var line = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n")[1];
        Assert.StartsWith("'=evil.txt,", line);
        Assert.Contains(",File,5,", line);
    }

    [Fact]
    public async Task Write_ManyRows_StreamsAndCountsAll()
    {
        var (bytes, rows) = await WriteAsync(Enumerable.Range(0, 5_000).Select(i => Item($"f{i}.txt")));

        Assert.Equal(5_000, rows);
        Assert.Equal(5_001, Encoding.UTF8.GetString(bytes[3..]).TrimEnd().Split("\r\n").Length);
    }

    [Fact]
    public async Task Write_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stream = new NonSeekableStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SearchExportWriter.WriteAsync(stream, Stream([Item("a.txt")]), ExportFormat.Csv, ExportEncoding.Utf8Bom, Labels, cts.Token));
    }

    [Fact]
    public void ContentType_AndFileName_FollowFormatAndEncoding()
    {
        Assert.Equal("text/csv; charset=utf-8", SearchExportWriter.ContentType(ExportFormat.Csv, ExportEncoding.Utf8Bom));
        Assert.Equal("text/tab-separated-values; charset=shift_jis", SearchExportWriter.ContentType(ExportFormat.Tsv, ExportEncoding.ShiftJis));
        Assert.Equal("nowfound-20261025-130102.csv", SearchExportWriter.FileName(ExportFormat.Csv, new DateTime(2026, 10, 25, 13, 1, 2)));
        Assert.EndsWith(".tsv", SearchExportWriter.FileName(ExportFormat.Tsv, DateTime.Now));
    }

    /// <summary>レスポンスのようにシーク不可で、同期書き込みを許さない出力先。</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        // Kestrel は同期書き込みを許さない（AllowSynchronousIO = false）ので、同期の Write は失敗させる。
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _inner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
