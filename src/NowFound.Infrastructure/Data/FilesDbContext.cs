using Microsoft.EntityFrameworkCore;
using NowFound.Domain.Entities;

namespace NowFound.Infrastructure.Data;

/// <summary>files.db。Indexer が書き込み、Web は参照のみ行う。</summary>
public class FilesDbContext(DbContextOptions<FilesDbContext> options) : DbContext(options)
{
    public DbSet<FileEntry> Files => Set<FileEntry>();

    public DbSet<DirectoryEntry> Directories => Set<DirectoryEntry>();

    public DbSet<OwnerEntry> Owners => Set<OwnerEntry>();

    public DbSet<IndexFolderState> IndexFolderStates => Set<IndexFolderState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<DirectoryEntry>(e =>
        {
            e.ToTable("Directories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).IsRequired();
            e.HasIndex(x => new { x.SearchFolderId, x.Path }).IsUnique();
        });

        builder.Entity<OwnerEntry>(e =>
        {
            e.ToTable("Owners");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        builder.Entity<FileEntry>(e =>
        {
            e.ToTable("Files");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Extension).IsRequired();

            // FTS のトリガーや一括削除の順序を明示するため、連鎖削除（CASCADE）にはしない。ファイルを先に消してからフォルダを消す。
            e.HasOne(x => x.Directory).WithMany().HasForeignKey(x => x.DirectoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.DirectoryId, x.Name }).IsUnique();
            e.HasIndex(x => x.SearchFolderId);
            e.HasIndex(x => x.Extension);
            e.HasIndex(x => x.ModifiedAt);
        });

        builder.Entity<IndexFolderState>(e =>
        {
            e.ToTable("IndexFolderStates");
            e.HasKey(x => x.SearchFolderId);
            e.Property(x => x.SearchFolderId).ValueGeneratedNever();
            e.Property(x => x.SettingsHash).IsRequired();
        });
    }
}

public class FilesDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FilesDbContext>().UseSqlite("Data Source=files.db").Options);
}
