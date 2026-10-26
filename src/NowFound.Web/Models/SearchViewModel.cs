using NowFound.Application.FileKinds;
using NowFound.Application.Search;

namespace NowFound.Web.Models;

/// <summary>検索条件そのもの（検索画面・保存フォームで共通）。</summary>
public class SearchQueryInput
{
    public List<SearchCondition> Conditions { get; set; } = [];

    public SearchOperator Operator { get; set; } = SearchOperator.And;

    public int? FolderId { get; set; }

    public string? FolderPath { get; set; }

    public SearchSortKey Sort { get; set; } = SearchSortKey.Path;

    public bool Desc { get; set; }

    public SavedQuery ToSavedQuery() => new(Operator, [.. Conditions], FolderId, FolderPath, Sort, Desc);

    /// <summary>保存できる内容（空でない条件、またはフォルダ絞り込み）があるか。</summary>
    public bool HasQuery => FolderId is not null || Conditions.Any(c => !c.IsEmpty);
}

public class SearchViewModel : SearchQueryInput
{
    public int Page { get; set; } = 1;

    /// <summary>保存した検索から開いている場合の ID（上書き保存と表示のため、検索を繰り返しても引き継ぐ）。</summary>
    public int? Saved { get; set; }

    public IReadOnlyList<AccessibleFolder> Folders { get; set; } = [];

    public SearchPage? Result { get; set; }

    public IReadOnlyList<SavedSearchSummary> SavedSearches { get; set; } = [];

    /// <summary>名前欄のアイコン。管理者が設定した種類（未設定なら既定値）。入力としては受け取らない。</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public FileKindCatalog FileKinds { get; set; } = FileKindCatalog.Default;

    /// <summary>インクリメンタルサーチの設定（管理者が設定）。入力としては受け取らない。</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public NowFound.Application.Settings.IncrementalSearchSettings Incremental { get; set; }
        = NowFound.Application.Settings.IncrementalSearchSettings.Default;

    /// <summary>検索結果のエクスポートを使えるか（管理者、または管理者が一般ユーザーにも許可した場合）。入力としては受け取らない。</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public bool CanExport { get; set; }

    /// <summary>Saved が指す、自分に見える保存検索。見えない・削除済みなら null。</summary>
    public SavedSearchSummary? CurrentSaved { get; set; }

    /// <summary>検索条件に問題があって無視した内容（画面に警告として表示する）。</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public List<string> Warnings { get; set; } = [];

    /// <summary>指定フォルダが存在しない、またはアクセス権がない。</summary>
    public bool FolderUnavailable { get; set; }

    public static SearchViewModel FromQuery(SavedQuery query, int? savedId) => new()
    {
        Conditions = [.. query.Conditions],
        Operator = query.Operator,
        FolderId = query.FolderId,
        FolderPath = query.FolderPath,
        Sort = query.Sort,
        Desc = query.Descending,
        Saved = savedId,
    };

    /// <summary>
    /// 条件を維持したまま、ページ・並べ替え・フォルダ絞り込みのいずれかを変えるためのクエリ値。
    /// </summary>
    public Dictionary<string, string?> ToRouteValues(
        int page,
        SearchSortKey? sort = null,
        bool? desc = null,
        int? folderId = null,
        string? folderPath = null,
        bool clearFolder = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Operator"] = Operator.ToString(),
            ["Page"] = page.ToString(),
            ["Sort"] = (sort ?? Sort).ToString(),
            ["Desc"] = (desc ?? Desc).ToString().ToLowerInvariant(),
        };

        if (Saved is { } savedId)
        {
            values["Saved"] = savedId.ToString();
        }

        if (!clearFolder)
        {
            var scopeId = folderId ?? FolderId;
            var scopePath = folderId is null ? FolderPath : folderPath;
            if (scopeId is { } id)
            {
                values["FolderId"] = id.ToString();
            }

            if (!string.IsNullOrEmpty(scopePath))
            {
                values["FolderPath"] = scopePath;
            }
        }

        for (var i = 0; i < Conditions.Count; i++)
        {
            var c = Conditions[i];
            values[$"Conditions[{i}].Field"] = c.Field.ToString();
            values[$"Conditions[{i}].Text"] = c.Text;
            values[$"Conditions[{i}].From"] = c.From?.ToString("yyyy-MM-dd");
            values[$"Conditions[{i}].To"] = c.To?.ToString("yyyy-MM-dd");
            if (c.Field == SearchField.Size)
            {
                values[$"Conditions[{i}].SizeComparison"] = c.SizeComparison.ToString();
                values[$"Conditions[{i}].SizeValue"] = c.SizeValue?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                values[$"Conditions[{i}].SizeUnit"] = c.SizeUnit.ToString();
            }
        }

        return values;
    }
}

/// <summary>「保存」ダイアログの送信内容。検索条件は検索画面が描画した hidden 項目で届く。</summary>
public class SaveSearchForm : SearchQueryInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsPublic { get; set; }

    public bool IsPinned { get; set; }
}

/// <summary>保存した検索の編集フォーム。メモ類に加えて、検索条件（対象フォルダ・条件）も編集する。</summary>
public class SavedSearchEditViewModel : SearchQueryInput
{
    public int Id { get; set; }

    /// <summary>対象フォルダの選択肢。入力としては受け取らない。</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public IReadOnlyList<AccessibleFolder> Folders { get; set; } = [];

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsPublic { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>公開にできるか（管理者のみ）。</summary>
    public bool CanPublish { get; set; }

    /// <summary>非公開に戻せるか（非公開のものは常に可。公開のものは作成者のみ）。</summary>
    public bool CanMakePrivate { get; set; } = true;

    public string OwnerEmail { get; set; } = string.Empty;
}
