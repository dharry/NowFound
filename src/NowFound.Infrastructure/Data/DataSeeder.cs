using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Identity;

namespace NowFound.Infrastructure.Data;

/// <summary>初期データ（ロール・初期管理者）の投入。</summary>
public static class DataSeeder
{
    public const string AdminEmail = "admin@nowfound.invalid";
    public const string AdminPassword = "password123!";

    public static async Task SeedAsync(UserManager<AppUser> users, RoleManager<IdentityRole> roles)
    {
        foreach (var role in AppRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
            }
        }

        if (await users.FindByEmailAsync(AdminEmail) is not null)
        {
            return;
        }

        var admin = new AppUser
        {
            UserName = AdminEmail,
            Email = AdminEmail,
            EmailConfirmed = true,
            MustChangePassword = true,
        };

        var created = await users.CreateAsync(admin, AdminPassword);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "初期管理者の作成に失敗しました: " + string.Join(", ", created.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(admin, AppRoles.Admin);
    }

    /// <summary>
    /// 既定のグループ（デフォルトグループ）を用意する。既にあれば何もしない。
    /// 同名のグループが手動で作られていた場合は、それを既定のグループにする。作成時は既存ユーザー全員を所属させる。
    /// </summary>
    public static async Task SeedDefaultGroupAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Groups.AnyAsync(g => g.IsDefault, ct))
        {
            return;
        }

        var group = await db.Groups.SingleOrDefaultAsync(g => g.Name == Group.DefaultName, ct);
        if (group is null)
        {
            group = new Group { Name = Group.DefaultName, Description = Group.DefaultDescription };
            db.Groups.Add(group);
            var userIds = await db.Users.Select(u => u.Id).ToListAsync(ct);
            group.UserGroups.AddRange(userIds.Select(id => new UserGroup { UserId = id }));
        }

        group.IsDefault = true;
        await db.SaveChangesAsync(ct);
    }
}
