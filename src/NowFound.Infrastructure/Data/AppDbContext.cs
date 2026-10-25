using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NowFound.Domain;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Identity;

namespace NowFound.Infrastructure.Data;

/// <summary>main.db。ユーザー・グループ・検索対象フォルダなどの設定を管理する。</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole, string>(options)
{
    public DbSet<Group> Groups => Set<Group>();

    public DbSet<UserGroup> UserGroups => Set<UserGroup>();

    public DbSet<SearchFolder> SearchFolders => Set<SearchFolder>();

    public DbSet<ExcludePattern> ExcludePatterns => Set<ExcludePattern>();

    public DbSet<GroupSearchFolder> GroupSearchFolders => Set<GroupSearchFolder>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();

    public DbSet<FileKindSetting> FileKindSettings => Set<FileKindSetting>();

    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>().Property(u => u.Theme).HasMaxLength(30);
        builder.Entity<AppUser>().Property(u => u.Language).HasMaxLength(10);
        builder.Entity<AppUser>().Property(u => u.MailAddress).HasMaxLength(256);
        builder.Entity<AppUser>().Property(u => u.Description).HasMaxLength(UserLimits.DescriptionMaxLength);
        builder.Entity<AppUser>().Property(u => u.AuthSource).HasMaxLength(20).HasDefaultValue(AuthSources.Local);

        builder.Entity<PasswordHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            e.Property(x => x.PasswordHash).IsRequired();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Group>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
        });

        builder.Entity<UserGroup>(e =>
        {
            e.HasKey(x => new { x.UserId, x.GroupId });
            e.HasOne(x => x.Group).WithMany(g => g.UserGroups).HasForeignKey(x => x.GroupId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
        });

        builder.Entity<SearchFolder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Path).IsRequired().HasMaxLength(1000);
            e.Property(x => x.ShareName).HasMaxLength(200);
            e.HasIndex(x => x.Path).IsUnique();
        });

        builder.Entity<ExcludePattern>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Pattern).IsRequired().HasMaxLength(500);
            e.HasOne<SearchFolder>().WithMany(f => f.ExcludePatterns).HasForeignKey(x => x.SearchFolderId);
        });

        builder.Entity<GroupSearchFolder>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.SearchFolderId });
            e.HasOne(x => x.Group).WithMany(g => g.GroupSearchFolders).HasForeignKey(x => x.GroupId);
            e.HasOne(x => x.SearchFolder).WithMany(f => f.GroupSearchFolders).HasForeignKey(x => x.SearchFolderId);
        });

        builder.Entity<SavedSearch>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.QueryJson).IsRequired();
            e.HasIndex(x => x.OwnerUserId);
            e.HasIndex(x => x.IsPublic);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FileKindSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).IsRequired().HasMaxLength(50);
            e.Property(x => x.Label).IsRequired().HasMaxLength(50);
            e.Property(x => x.Icon).IsRequired().HasMaxLength(64);
            e.Property(x => x.Extensions).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
        });

        builder.Entity<SystemSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).IsRequired().HasMaxLength(1000);
        });
    }

    // パスワードが設定・変更されたユーザーは、最終変更日時を更新し、履歴（再利用禁止の判定用）に残す。
    // ユーザー作成・パスワード変更・初期データ投入のどの経路でも漏れないよう、保存時にまとめて行う。
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var userId in RecordPasswordChanges())
        {
            // 今追加した履歴を含めて AccountPolicy.HistoryCountMax 件になるよう、古いものを削除する。
            var surplus = await PasswordHistories
                .Where(h => h.UserId == userId)
                .OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id)
                .Skip(Application.Users.AccountPolicy.HistoryCountMax - 1)
                .ToListAsync(cancellationToken);
            PasswordHistories.RemoveRange(surplus);
        }

        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RecordPasswordChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private List<string> RecordPasswordChanges()
    {
        ChangeTracker.DetectChanges();
        var now = DateTime.UtcNow;
        var changed = new List<string>();
        foreach (var entry in ChangeTracker.Entries<AppUser>().ToList())
        {
            var user = entry.Entity;
            // UserManager.UpdateAsync は全プロパティを「変更あり」にするので、フラグではなく値が実際に変わったかで判定する。
            var passwordSet = entry.State == EntityState.Added
                || (entry.State == EntityState.Modified
                    && entry.Property(u => u.PasswordHash).IsModified
                    && !string.Equals(
                        entry.Property(u => u.PasswordHash).OriginalValue,
                        entry.Property(u => u.PasswordHash).CurrentValue,
                        StringComparison.Ordinal));
            if (!passwordSet || string.IsNullOrEmpty(user.PasswordHash))
            {
                continue;
            }

            user.PasswordChangedAt = now;
            PasswordHistories.Add(new PasswordHistory { UserId = user.Id, PasswordHash = user.PasswordHash, CreatedAt = now });
            changed.Add(user.Id);
        }

        return changed;
    }
}
