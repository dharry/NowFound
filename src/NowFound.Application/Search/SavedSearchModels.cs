using System.Text.Json;
using System.Text.Json.Serialization;
using NowFound.Application.Users;

namespace NowFound.Application.Search;

public static class SavedSearchLimits
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;
    public const int MaxPrivatePerUser = 100;
    public const int MaxPublic = 100;
    public const int MaxConditions = 10;
    public const int MaxConditionTextLength = 200;
    public const int MaxFolderPathLength = 1000;
}

/// <summary>保存する検索条件。ページ番号は含めない（常に 1 ページ目から実行する）。</summary>
public sealed record SavedQuery(
    SearchOperator Operator,
    IReadOnlyList<SearchCondition> Conditions,
    int? FolderId,
    string? FolderPath,
    SearchSortKey Sort,
    bool Descending);

/// <summary>SavedQuery の検証と正規化。</summary>
public static class SavedQueryRules
{
    /// <summary>空の条件を除き、文字列を整える。</summary>
    public static SavedQuery Normalize(SavedQuery query) => query with
    {
        Conditions =
        [
            .. query.Conditions
                .Where(c => !c.IsEmpty)
                .Select(c => new SearchCondition
                {
                    Field = c.Field,
                    Text = c.Text?.Trim(),
                    From = c.From?.Date,
                    To = c.To?.Date,
                    SizeComparison = c.SizeComparison,
                    SizeValue = c.SizeValue,
                    SizeUnit = c.SizeUnit,
                }),
        ],
        FolderPath = string.IsNullOrWhiteSpace(query.FolderPath) ? null : query.FolderPath,
    };

    /// <summary>問題があればエラー（キー＋引数）、なければ null（Normalize 後の値を渡す）。</summary>
    public static LocalizedMessage? Validate(SavedQuery query)
    {
        if (query.Conditions.Count == 0 && query.FolderId is null)
        {
            return new("Error.SavedSearch.NoConditions");
        }

        if (query.Conditions.Count > SavedSearchLimits.MaxConditions)
        {
            return new("Error.SavedSearch.TooManyConditions", SavedSearchLimits.MaxConditions);
        }

        if (query.Conditions.Any(c => (c.Text?.Length ?? 0) > SavedSearchLimits.MaxConditionTextLength))
        {
            return new("Error.SavedSearch.KeywordTooLong", SavedSearchLimits.MaxConditionTextLength);
        }

        if ((query.FolderPath?.Length ?? 0) > SavedSearchLimits.MaxFolderPathLength)
        {
            return new("Error.SavedSearch.FolderPathTooLong");
        }

        if (!Enum.IsDefined(query.Operator) || !Enum.IsDefined(query.Sort)
            || query.Conditions.Any(c => !Enum.IsDefined(c.Field) || !Enum.IsDefined(c.SizeComparison) || !Enum.IsDefined(c.SizeUnit)))
        {
            return new("Error.SavedSearch.InvalidCondition");
        }

        return null;
    }
}

/// <summary>SavedQuery の JSON 変換。形式にバージョンを持たせ、将来の条件追加後も古い保存データを読めるようにする。</summary>
public static class SavedQueryJson
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Dto(
        int Version,
        SearchOperator Operator,
        List<ConditionDto> Conditions,
        int? FolderId,
        string? FolderPath,
        SearchSortKey Sort,
        bool Descending);

    /// <summary>サイズの項目は後から追加したもの。無い（古い保存データ）場合は null で、サイズ以外の条件では常に出力しない。</summary>
    private sealed record ConditionDto(
        SearchField Field,
        string? Text,
        DateTime? From,
        DateTime? To,
        SizeComparison? SizeComparison = null,
        decimal? SizeValue = null,
        SizeUnit? SizeUnit = null);

    private static ConditionDto ToDto(SearchCondition c) => c.Field == SearchField.Size
        ? new ConditionDto(c.Field, null, null, null, c.SizeComparison, c.SizeValue, c.SizeUnit)
        : new ConditionDto(c.Field, c.Text, c.From, c.To);

    public static string Serialize(SavedQuery query) => JsonSerializer.Serialize(
        new Dto(
            CurrentVersion,
            query.Operator,
            [.. query.Conditions.Select(ToDto)],
            query.FolderId,
            query.FolderPath,
            query.Sort,
            query.Descending),
        Options);

    /// <summary>読み込めない（壊れている・未知のバージョン）場合は null。</summary>
    public static SavedQuery? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json, Options);
            if (dto is null || dto.Version is < 1 or > CurrentVersion || dto.Conditions is null)
            {
                return null;
            }

            return new SavedQuery(
                dto.Operator,
                [.. dto.Conditions.Select(c => new SearchCondition
                {
                    Field = c.Field,
                    Text = c.Text,
                    From = c.From,
                    To = c.To,
                    SizeComparison = c.SizeComparison ?? default,
                    SizeValue = c.SizeValue,
                    SizeUnit = c.SizeUnit ?? SizeUnit.MB,
                })],
                dto.FolderId,
                dto.FolderPath,
                dto.Sort,
                dto.Descending);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record SavedSearchSummary(
    int Id,
    string Name,
    string? Description,
    bool IsPublic,
    bool IsPinned,
    bool CanEdit,
    string OwnerEmail,
    DateTime UpdatedAt);

/// <param name="Query">保存内容が壊れていて読めない場合は null。</param>
public sealed record SavedSearchDetail(SavedSearchSummary Summary, SavedQuery? Query);

public sealed record SavedSearchMeta(string Name, string? Description, bool IsPublic, bool IsPinned);

public sealed record SavedSearchCreateResult(OperationResult Result, int? Id);

/// <summary>
/// 保存した検索（ブックマーク）。非公開は作成者のみ、公開は全員が実行でき、
/// 公開の作成・変更・削除は管理者のみ。他人の非公開は管理者にも見えない。
/// </summary>
public interface ISavedSearchService
{
    /// <summary>自分の非公開と全員の公開。固定（ピン留め）が先頭、次に名前順。</summary>
    Task<IReadOnlyList<SavedSearchSummary>> ListAsync(string userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>ブックマーク一覧用。検索・並べ替えをして 1 ページ分。並べ替えを指定しないときは ListAsync と同じ並び（固定が先頭、次に名前順）。</summary>
    Task<PagedResult<SavedSearchSummary>> ListPageAsync(
        string userId, bool isAdmin, int page, int pageSize, SavedSearchListQuery? query = null, CancellationToken ct = default);

    /// <summary>見えない（他人の非公開・存在しない）場合は null。</summary>
    Task<SavedSearchDetail?> GetAsync(int id, string userId, bool isAdmin, CancellationToken ct = default);

    Task<SavedSearchCreateResult> CreateAsync(
        string userId, bool isAdmin, SavedSearchMeta meta, SavedQuery query, CancellationToken ct = default);

    /// <summary>名前・説明・公開範囲・固定を変更する。<paramref name="newQuery"/> を渡すと条件も置き換える。</summary>
    Task<OperationResult> UpdateAsync(
        int id, string userId, bool isAdmin, SavedSearchMeta meta, SavedQuery? newQuery, CancellationToken ct = default);

    /// <summary>名前などはそのままに、検索条件だけを上書きする。</summary>
    Task<OperationResult> OverwriteQueryAsync(
        int id, string userId, bool isAdmin, SavedQuery query, CancellationToken ct = default);

    Task<OperationResult> DeleteAsync(int id, string userId, bool isAdmin, CancellationToken ct = default);
}
