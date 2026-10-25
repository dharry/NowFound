using Microsoft.EntityFrameworkCore;
using NowFound.Application;
using NowFound.Application.FileKinds;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Settings;

public class FileKindService(AppDbContext db) : IFileKindService
{
    public async Task<FileKindCatalog> GetCatalogAsync(CancellationToken ct = default) =>
        FileKindCatalog.Build(await GetDefinitionsAsync(ct));

    public async Task<IReadOnlyList<FileKindDefinition>> GetDefinitionsAsync(CancellationToken ct = default)
    {
        var rows = await db.FileKindSettings.AsNoTracking()
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return DefaultFileKinds.Definitions;
        }

        var definitions = rows.Select(r => new FileKindDefinition(
            r.Key,
            r.Label,
            r.Icon,
            [.. r.Extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            IsSystem: r.Key is DefaultFileKinds.FolderKey or DefaultFileKinds.GenericKey,
            // 組み込みの名前のまま（日本語の既定）なら翻訳対象。管理者が付けた名前はそのまま表示する。
            LabelKey: DefaultFileKinds.LabelKeyFor(r.Key, r.Label))).ToList();

        // 手動での DB 編集などでフォルダ・汎用のファイルの行が欠けても、画面が壊れないよう既定値で補う。
        foreach (var key in new[] { DefaultFileKinds.GenericKey, DefaultFileKinds.FolderKey })
        {
            if (definitions.All(d => d.Key != key))
            {
                definitions.Insert(0, DefaultFileKinds.Definitions.First(d => d.Key == key));
            }
        }

        return definitions;
    }

    public Task<bool> IsCustomizedAsync(CancellationToken ct = default) => db.FileKindSettings.AnyAsync(ct);

    public async Task<OperationResult> SaveAsync(IReadOnlyList<FileKindEdit> edits, CancellationToken ct = default)
    {
        var validation = FileKindRules.Validate(edits);
        if (!validation.Succeeded)
        {
            return OperationResult.Failure(validation.Errors);
        }

        // 全体を置き換える。途中で失敗しても、元の設定が残るようトランザクションにする。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.FileKindSettings.ExecuteDeleteAsync(ct);
        db.FileKindSettings.AddRange(validation.Definitions!.Select((d, index) => new FileKindSetting
        {
            Key = d.Key,
            Label = d.Label,
            Icon = d.Icon,
            Extensions = string.Join(',', d.Extensions),
            SortOrder = index,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return OperationResult.Success;
    }

    public async Task ResetAsync(CancellationToken ct = default) =>
        await db.FileKindSettings.ExecuteDeleteAsync(ct);
}
