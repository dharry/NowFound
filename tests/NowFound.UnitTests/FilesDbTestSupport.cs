using Microsoft.EntityFrameworkCore;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

/// <summary>テストが files.db の中身を読みやすい形で確認するための行。フルパスは保存されていないので組み立てる。</summary>
public sealed record FileRow
{
    public string Name { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public string Extension { get; init; } = string.Empty;
    public string? Owner { get; init; }
    public DateTime ModifiedAt { get; init; }
    public DateTime CreatedAt { get; init; }

    public string FullPath => Path.Join(FolderPath, Name);
}

internal static class FilesDbTestExtensions
{
    /// <summary>Directories / Owners を結合した読み取り用の行。</summary>
    public static IQueryable<FileRow> Rows(this FilesDbContext db) =>
        db.Files.AsNoTracking().Select(f => new FileRow
        {
            Name = f.Name,
            FolderPath = f.Directory!.Path,
            IsDirectory = f.IsDirectory,
            Size = f.Size,
            Extension = f.Extension,
            Owner = f.Owner == null ? null : f.Owner.Name,
            ModifiedAt = f.ModifiedAt,
            CreatedAt = f.CreatedAt,
        });
}

/// <summary>files.db にテスト用のエントリを直接投入する（フォルダ・オーナーは必要に応じて作る）。</summary>
internal sealed class FilesSeeder(FilesDbContext db)
{
    private readonly Dictionary<(int Folder, string Path), int> _directories = [];
    private readonly Dictionary<string, int> _owners = [];

    public async Task AddAsync(
        int folderId, string folderPath, string name, string extension = "", long? size = null,
        DateTime? modified = null, string? owner = null, bool isDirectory = false)
    {
        var modifiedAt = modified ?? DateTime.UtcNow;
        db.Files.Add(new FileEntry
        {
            SearchFolderId = folderId,
            DirectoryId = await DirectoryIdAsync(folderId, folderPath),
            Name = name,
            IsDirectory = isDirectory,
            Size = isDirectory ? null : size,
            Extension = extension,
            OwnerId = owner is null ? null : await OwnerIdAsync(owner),
            ModifiedAt = modifiedAt,
            CreatedAt = modifiedAt.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> DirectoryIdAsync(int folderId, string path)
    {
        if (_directories.TryGetValue((folderId, path), out var id))
        {
            return id;
        }

        var entity = new DirectoryEntry { SearchFolderId = folderId, Path = path };
        db.Directories.Add(entity);
        await db.SaveChangesAsync();
        return _directories[(folderId, path)] = entity.Id;
    }

    private async Task<int> OwnerIdAsync(string name)
    {
        if (_owners.TryGetValue(name, out var id))
        {
            return id;
        }

        var entity = new OwnerEntry { Name = name };
        db.Owners.Add(entity);
        await db.SaveChangesAsync();
        return _owners[name] = entity.Id;
    }
}
