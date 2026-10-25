namespace NowFound.Application.Search;

public enum SearchField
{
    All,
    FileName,
    FolderName,
    Extension,
    ModifiedDate,
    CreatedDate,
    Size,
    Owner,
}

/// <summary>サイズの比較演算子。既定（先頭）は「以上」。</summary>
public enum SizeComparison
{
    GreaterOrEqual,
    GreaterThan,
    LessOrEqual,
    LessThan,
    Equal,
    NotEqual,
}

/// <summary>サイズの単位。1 KB = 1,024 バイト（Windows のエクスプローラーと同じ）。</summary>
public enum SizeUnit
{
    B,
    KB,
    MB,
    GB,
    TB,
}

public enum SearchOperator
{
    And,
    Or,
}

/// <summary>検索条件 1 行分。日付系は From / To、サイズは SizeComparison / SizeValue / SizeUnit、それ以外は Text を使う。</summary>
public sealed class SearchCondition
{
    public SearchField Field { get; set; }
    public string? Text { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public SizeComparison SizeComparison { get; set; }

    /// <summary>サイズの数値（SizeUnit の単位）。小数も可（例: 1.5 MB）。</summary>
    public decimal? SizeValue { get; set; }

    public SizeUnit SizeUnit { get; set; } = SizeUnit.MB;

    /// <summary>
    /// サイズ条件のバイト数。数値が未入力・負・大きすぎる（long に収まらない）場合は null。
    /// 小数は四捨五入して、バイト単位にする。
    /// </summary>
    public long? SizeBytes => ToBytes(SizeValue, SizeUnit);

    /// <summary>
    /// 条件として使えない（無視される）か。サイズは、数値が使える値のときだけ有効。
    /// </summary>
    public bool IsEmpty => Field switch
    {
        SearchField.ModifiedDate or SearchField.CreatedDate => From is null && To is null,
        SearchField.Size => SizeBytes is null,
        _ => string.IsNullOrWhiteSpace(Text),
    };

    /// <summary>サイズは入力されているのに使えない値（負・大きすぎる）。画面で警告するために使う。</summary>
    public bool HasInvalidSize => Field == SearchField.Size && SizeValue is not null && SizeBytes is null;

    public static long? ToBytes(decimal? value, SizeUnit unit)
    {
        if (value is null or < 0)
        {
            return null;
        }

        var multiplier = unit switch
        {
            SizeUnit.KB => 1L << 10,
            SizeUnit.MB => 1L << 20,
            SizeUnit.GB => 1L << 30,
            SizeUnit.TB => 1L << 40,
            _ => 1L,
        };

        try
        {
            var bytes = decimal.Round(value.Value * multiplier, MidpointRounding.AwayFromZero);
            return bytes <= long.MaxValue ? (long)bytes : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

/// <summary>並べ替えキー。Path は既定（フルパス順）で、列ヘッダーには出さない。</summary>
public enum SearchSortKey
{
    Path,
    Name,
    Folder,
    Size,
    Extension,
    Owner,
    Modified,
    Created,
}

public sealed record SearchRequest(
    string UserId,
    bool IsAdmin,
    IReadOnlyList<SearchCondition> Conditions,
    SearchOperator Operator,
    int? FolderId,
    string? FolderPath,
    int Page,
    SearchSortKey SortKey = SearchSortKey.Path,
    bool Descending = false,
    TimeZoneInfo? TimeZone = null);

public sealed record SearchResultItem(
    int SearchFolderId,
    string FullPath,
    string Name,
    string FolderPath,
    bool IsDirectory,
    long? Size,
    string Extension,
    string? Owner,
    DateTime ModifiedAt,
    DateTime CreatedAt,
    string? DisplayFullPath = null,
    string? DisplayFolderPath = null)
{
    /// <summary>画面表示・クリップボードコピー用のフルパス。共有フォルダでは UNC。</summary>
    public string CopyPath => DisplayFullPath ?? FullPath;

    /// <summary>画面表示用のフォルダパス。共有フォルダでは UNC。絞り込みには FolderPath を使う。</summary>
    public string ShownFolderPath => DisplayFolderPath ?? FolderPath;
}

public sealed record SearchPage(
    IReadOnlyList<SearchResultItem> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record AccessibleFolder(int Id, string Name, string Path);

public sealed record SubFolder(string Name, string Path);

/// <summary>検索とフォルダ閲覧。ユーザーのグループに割り当てられたフォルダのみが対象になる（管理者は全て）。</summary>
public interface ISearchService
{
    Task<IReadOnlyList<AccessibleFolder>> GetAccessibleFoldersAsync(string userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>folderId 直下（parentPath 省略時はルート直下）のサブフォルダ。権限がなければ空。</summary>
    Task<IReadOnlyList<SubFolder>> GetSubFoldersAsync(
        string userId, bool isAdmin, int folderId, string? parentPath, CancellationToken ct = default);

    Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// 検索結果を全件、並べ替え順に 1 件ずつ返す（ページ送りなし、件数集計なし）。メモリに溜めずに流すための経路。
    /// 権限・条件は <see cref="SearchAsync"/> と同じ。<paramref name="maxRows"/> を超える分は返さない。
    /// </summary>
    IAsyncEnumerable<SearchResultItem> ExportAsync(SearchRequest request, int maxRows, CancellationToken ct = default);

    /// <summary>ユーザー個別設定 → 管理者既定値 → 100 の順で解決したページサイズ。</summary>
    Task<int> ResolvePageSizeAsync(string userId, CancellationToken ct = default);
}
