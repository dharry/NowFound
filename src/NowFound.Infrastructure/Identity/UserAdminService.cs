using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NowFound.Application;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Identity;

public class UserAdminService(UserManager<AppUser> userManager, AppDbContext db, IAccountStatusCache? accountStatusCache = null) : IUserAdminService
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        var adminIds = (await AdminUserIdsQuery().ToListAsync(ct)).ToHashSet();
        var users = await userManager.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);
        return [.. users.Select(u => ToSummary(u, adminIds.Contains(u.Id)))];
    }

    public async Task<PagedResult<UserSummary>> ListPageAsync(
        int page, int pageSize, UserListQuery? query = null, CancellationToken ct = default)
    {
        query ??= new UserListQuery();
        var adminUserIds = AdminUserIdsQuery();

        var users = userManager.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            // アカウント（Email 列）と連絡先のメールアドレスの、どちらかに一致すればよい。
            users = users.Where(u => EF.Functions.Like(u.Email!, pattern, SqlLike.Escape)
                || (u.MailAddress != null && EF.Functions.Like(u.MailAddress, pattern, SqlLike.Escape)));
        }

        users = query.Role switch
        {
            "admin" => users.Where(u => adminUserIds.Contains(u.Id)),
            "user" => users.Where(u => !adminUserIds.Contains(u.Id)),
            _ => users,
        };

        var total = await users.CountAsync(ct);
        page = PagedResult<UserSummary>.ClampPage(page, total, pageSize);
        var ordered = (query.Sort, query.Descending) switch
        {
            (UserSortKeys.Role, false) => users.OrderBy(u => adminUserIds.Contains(u.Id)),
            (UserSortKeys.Role, true) => users.OrderByDescending(u => adminUserIds.Contains(u.Id)),
            (UserSortKeys.MustChange, false) => users.OrderBy(u => u.MustChangePassword),
            (UserSortKeys.MustChange, true) => users.OrderByDescending(u => u.MustChangePassword),
            (UserSortKeys.Email, true) => users.OrderByDescending(u => u.Email),
            _ => users.OrderBy(u => u.Email),
        };
        var items = await ordered.ThenBy(u => u.Email).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var pageUserIds = items.Select(u => u.Id).ToList();
        var pageAdminIds = (await adminUserIds.Where(id => pageUserIds.Contains(id)).ToListAsync(ct)).ToHashSet();

        return new PagedResult<UserSummary>(
            [.. items.Select(u => ToSummary(u, pageAdminIds.Contains(u.Id)))],
            page,
            pageSize,
            total);
    }

    private IQueryable<string> AdminUserIdsQuery() =>
        db.UserRoles.AsNoTracking()
            .Where(ur => db.Roles.AsNoTracking().Where(r => r.Name == AppRoles.Admin).Select(r => r.Id).Contains(ur.RoleId))
            .Select(ur => ur.UserId);

    public async Task<int?> GetBookmarkPageSizeAsync(string userId, CancellationToken ct = default) =>
        await userManager.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.BookmarkPageSize).SingleOrDefaultAsync(ct);

    public async Task<OperationResult> SetBookmarkPageSizeAsync(string userId, int? pageSize, CancellationToken ct = default)
    {
        if (pageSize is { } size && !PageSizeLimits.IsValid(size))
        {
            return OperationResult.Failure("Error.PageSize.Range", PageSizeLimits.Min, PageSizeLimits.Max);
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        user.BookmarkPageSize = pageSize;
        return ToResult(await userManager.UpdateAsync(user));
    }

    /// <summary>
    /// 複数の更新を 1 つのトランザクションにまとめる。失敗（<see cref="OperationResult"/> の失敗や例外）なら全体を取り消す。
    /// Serializable は SQLite では BEGIN IMMEDIATE になり、最初に書き込みロックを取る。
    /// 他の更新と競合する判定（最終管理者の確認など）を、ロックを取った後に行うために使う。
    /// </summary>
    private async Task<OperationResult> InTransactionAsync(Func<Task<OperationResult>> work, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await work();
        if (result.Succeeded)
        {
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    public Task<OperationResult> CreateAsync(CreateUserRequest request) =>
        InTransactionAsync(() => CreateCoreAsync(request), CancellationToken.None);

    private async Task<OperationResult> CreateCoreAsync(CreateUserRequest request)
    {
        if (!AuthSources.IsValid(request.AuthSource))
        {
            return OperationResult.Failure("Error.User.AuthSourceInvalid");
        }

        var isDirectory = request.AuthSource == AuthSources.ActiveDirectory;
        var loginName = request.Email.Trim();
        if (isDirectory ? !ActiveDirectoryRules.IsValidUserName(loginName) : !EmailRules.IsValid(loginName))
        {
            return OperationResult.Failure(isDirectory ? "Error.User.AdUserNameInvalid" : "Error.User.EmailInvalid");
        }

        var (mailAddress, mailError) = isDirectory ? NormalizeMailAddress(request.MailAddress) : (loginName, null);
        if (mailError is not null)
        {
            return mailError;
        }

        var (description, descriptionError) = NormalizeDescription(request.Description);
        if (descriptionError is not null)
        {
            return descriptionError;
        }

        var user = new AppUser
        {
            MailAddress = mailAddress,
            Description = description,
            // AD ユーザーは接頭辞を付け、標準ユーザーのメールアドレスと UserName が衝突しないようにする。
            UserName = isDirectory ? AuthSources.ActiveDirectoryUserNamePrefix + loginName : loginName,
            Email = loginName,
            EmailConfirmed = true,
            AuthSource = request.AuthSource,
            // AD ユーザーのパスワードは AD が管理するため、変更の強制も行わない。
            MustChangePassword = !isDirectory && request.RequirePasswordChange,
            IsEnabled = request.IsEnabled,
            ExpiresOn = request.ExpiresOn,
        };

        var result = isDirectory ? await userManager.CreateAsync(user) : await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return ToResult(result);
        }

        var roleResult = await userManager.AddToRoleAsync(user, request.IsAdmin ? AppRoles.Admin : AppRoles.User);
        if (!roleResult.Succeeded)
        {
            return ToResult(roleResult);
        }

        // 既定のグループ（デフォルトグループ）に自動で所属させる。
        var defaultGroupId = await db.Groups.Where(g => g.IsDefault).Select(g => (int?)g.Id).FirstOrDefaultAsync();
        if (defaultGroupId is { } groupId)
        {
            db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = groupId });
            await db.SaveChangesAsync();
        }

        return OperationResult.Success;
    }

    public async Task<UserEdit?> GetAsync(string userId, CancellationToken ct = default)
    {
        var user = await userManager.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return null;
        }

        return new UserEdit
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            IsAdmin = await userManager.IsInRoleAsync(user, AppRoles.Admin),
            IsEnabled = user.IsEnabled,
            ExpiresOn = user.ExpiresOn,
            IsLockedOut = IsLockedOut(user),
            AuthSource = AuthSources.Normalize(user.AuthSource),
            MailAddress = ContactAddress(user),
            Description = user.Description,
        };
    }

    public Task<OperationResult> UpdateAsync(UserEdit edit, string actingUserId, DateOnly today, CancellationToken ct = default) =>
        InTransactionAsync(() => UpdateCoreAsync(edit, actingUserId, today), ct);

    private async Task<OperationResult> UpdateCoreAsync(UserEdit edit, string actingUserId, DateOnly today)
    {
        var user = await userManager.FindByIdAsync(edit.Id);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        var wasAdmin = await userManager.IsInRoleAsync(user, AppRoles.Admin);
        var willBeActive = AccountStatusRules.Evaluate(edit.IsEnabled, edit.ExpiresOn, today) == AccountStatus.Active;

        // 自分自身を締め出す変更（無効化・期限切れ・管理者権限の剥奪）はさせない。
        if (edit.Id == actingUserId && (!willBeActive || wasAdmin != edit.IsAdmin))
        {
            return OperationResult.Failure("Error.User.CannotChangeSelf");
        }

        // 有効な管理者が 0 人になると、誰も管理画面に入れなくなる。
        if (wasAdmin && (!edit.IsAdmin || !willBeActive))
        {
            var otherActiveAdmins = (await userManager.GetUsersInRoleAsync(AppRoles.Admin))
                .Count(u => u.Id != user.Id && AccountStatusRules.Evaluate(u.IsEnabled, u.ExpiresOn, today) == AccountStatus.Active);
            if (otherActiveAdmins == 0)
            {
                return OperationResult.Failure("Error.User.LastAdmin");
            }
        }

        // 標準ユーザーの連絡先はアカウントと同じ（変更できない）。AD ユーザーだけ編集できる。
        if (user.IsDirectoryUser)
        {
            var (mailAddress, mailError) = NormalizeMailAddress(edit.MailAddress);
            if (mailError is not null)
            {
                return mailError;
            }

            user.MailAddress = mailAddress;
        }
        else
        {
            user.MailAddress = user.Email;
        }

        var (description, descriptionError) = NormalizeDescription(edit.Description);
        if (descriptionError is not null)
        {
            return descriptionError;
        }

        user.Description = description;
        user.IsEnabled = edit.IsEnabled;
        user.ExpiresOn = edit.ExpiresOn;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return ToResult(result);
        }

        if (wasAdmin != edit.IsAdmin)
        {
            // ユーザーのロールは作成時と同じく、Admin か User のどちらか 1 つ。
            var removed = await userManager.RemoveFromRoleAsync(user, wasAdmin ? AppRoles.Admin : AppRoles.User);
            if (!removed.Succeeded)
            {
                return ToResult(removed);
            }

            var added = await userManager.AddToRoleAsync(user, edit.IsAdmin ? AppRoles.Admin : AppRoles.User);
            if (!added.Succeeded)
            {
                return ToResult(added);
            }

            // Cookie に残っている旧ロールのセッションを失効させる（AccountStatusMiddleware がスタンプを照合する）。
            var stampResult = await userManager.UpdateSecurityStampAsync(user);
            accountStatusCache?.Invalidate(user.Id);
            return ToResult(stampResult);
        }

        accountStatusCache?.Invalidate(user.Id);
        return OperationResult.Success;
    }

    public Task<OperationResult> DeleteAsync(string userId, string actingUserId, DateOnly today, CancellationToken ct = default) =>
        InTransactionAsync(() => DeleteCoreAsync(userId, actingUserId, today), ct);

    private async Task<OperationResult> DeleteCoreAsync(string userId, string actingUserId, DateOnly today)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        // 有効な管理者が 0 人になると、誰も管理画面に入れなくなる。
        if (await userManager.IsInRoleAsync(user, AppRoles.Admin))
        {
            var otherActiveAdmins = (await userManager.GetUsersInRoleAsync(AppRoles.Admin))
                .Count(u => u.Id != user.Id && AccountStatusRules.Evaluate(u.IsEnabled, u.ExpiresOn, today) == AccountStatus.Active);
            if (otherActiveAdmins == 0)
            {
                return OperationResult.Failure("Error.User.CannotDeleteLastAdmin");
            }
        }

        if (userId == actingUserId)
        {
            return OperationResult.Failure("Error.User.CannotDeleteSelf");
        }

        // 所属グループ・ブックマーク・パスワード履歴は、外部キーのカスケード削除で一緒に消える。
        // 操作ログ（ope.db）は監査のため残す。
        var result = await userManager.DeleteAsync(user);
        if (result.Succeeded)
        {
            accountStatusCache?.Invalidate(userId);
        }
        return ToResult(result);
    }

    public async Task<OperationResult> UnlockAsync(string userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        var result = await userManager.SetLockoutEndDateAsync(user, null);
        if (!result.Succeeded)
        {
            return ToResult(result);
        }

        var reset = await userManager.ResetAccessFailedCountAsync(user);
        if (reset.Succeeded)
        {
            accountStatusCache?.Invalidate(userId);
        }
        return ToResult(reset);
    }

    public async Task<OperationResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        if (user.IsDirectoryUser)
        {
            // パスワードは ActiveDirectory 側で管理する。
            return OperationResult.Failure("Error.User.PasswordManagedByDirectory");
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return ToResult(result);
        }

        user.MustChangePassword = false;
        var update = await userManager.UpdateAsync(user);
        if (update.Succeeded)
        {
            accountStatusCache?.Invalidate(userId);
        }
        return ToResult(update);
    }

    public Task<OperationResult> ResetPasswordAsync(
        string userId, string newPassword, bool requireChange, string actingUserId, CancellationToken ct = default) =>
        InTransactionAsync(() => ResetPasswordCoreAsync(userId, newPassword, requireChange, actingUserId), ct);

    private async Task<OperationResult> ResetPasswordCoreAsync(string userId, string newPassword, bool requireChange, string actingUserId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        if (user.IsDirectoryUser)
        {
            // パスワードは ActiveDirectory 側で管理する。
            return OperationResult.Failure("Error.User.PasswordManagedByDirectory");
        }

        // 自分自身のパスワードは「ユーザー設定」で変更する（再設定するとセキュリティスタンプが変わり、自分が締め出される）。
        if (user.Id == actingUserId)
        {
            return OperationResult.Failure("Error.User.CannotResetOwnPassword");
        }

        // アカウントポリシーの検証と、セキュリティスタンプの更新（既存セッションの無効化）は ResetPasswordAsync が行う。
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, newPassword ?? string.Empty);
        if (!result.Succeeded)
        {
            return ToResult(result);
        }

        // パスワードを忘れた結果のロックも、ここで解除する。
        user.MustChangePassword = requireChange;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        var update = await userManager.UpdateAsync(user);
        if (update.Succeeded)
        {
            accountStatusCache?.Invalidate(userId);
        }

        return ToResult(update);
    }

    public async Task<int?> GetPageSizeAsync(string userId, CancellationToken ct = default) =>
        await userManager.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PageSize).SingleOrDefaultAsync(ct);

    public async Task<OperationResult> SetPageSizeAsync(string userId, int? pageSize, CancellationToken ct = default)
    {
        if (pageSize is { } size && !PageSizeLimits.IsValid(size))
        {
            return OperationResult.Failure("Error.PageSize.Range", PageSizeLimits.Min, PageSizeLimits.Max);
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        user.PageSize = pageSize;
        return ToResult(await userManager.UpdateAsync(user));
    }

    public async Task<string?> GetLanguageAsync(string userId, CancellationToken ct = default) =>
        await userManager.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Language).SingleOrDefaultAsync(ct);

    public async Task<OperationResult> SetLanguageAsync(string userId, string language, CancellationToken ct = default)
    {
        if (!AppLanguages.IsValid(language))
        {
            return OperationResult.Failure("Error.LanguageNotSupported");
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        user.Language = language;
        return ToResult(await userManager.UpdateAsync(user));
    }

    public async Task<string?> GetThemeAsync(string userId, CancellationToken ct = default) =>
        await userManager.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Theme).SingleOrDefaultAsync(ct);

    public async Task<OperationResult> SetThemeAsync(string userId, string theme, CancellationToken ct = default)
    {
        if (!AppThemes.IsValid(theme))
        {
            return OperationResult.Failure("Error.ThemeNotSupported");
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return OperationResult.Failure("Error.UserNotFound");
        }

        user.Theme = theme;
        return ToResult(await userManager.UpdateAsync(user));
    }

    private static bool IsLockedOut(AppUser user) => user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow;

    private static UserSummary ToSummary(AppUser user, bool isAdmin) =>
        new(user.Id, user.Email ?? string.Empty, isAdmin, user.MustChangePassword, user.IsEnabled, user.ExpiresOn, IsLockedOut(user),
            AuthSources.Normalize(user.AuthSource), ContactAddress(user), user.Description);

    /// <summary>標準ユーザーはアカウント（メールアドレス）、AD ユーザーは登録された連絡先（無ければ null）。</summary>
    private static string? ContactAddress(AppUser user) =>
        user.IsDirectoryUser ? user.MailAddress : user.MailAddress ?? user.Email;

    /// <summary>空なら null、入力されていれば形式を検証する。</summary>
    private static (string? Address, OperationResult? Error) NormalizeMailAddress(string? input)
    {
        var address = input?.Trim();
        if (string.IsNullOrEmpty(address))
        {
            return (null, null);
        }

        return EmailRules.IsValid(address) ? (address, null) : (null, OperationResult.Failure("Error.User.MailAddressInvalid"));
    }

    /// <summary>空なら null、長すぎればエラー。</summary>
    private static (string? Text, OperationResult? Error) NormalizeDescription(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        return text.Length <= UserLimits.DescriptionMaxLength
            ? (text, null)
            : (null, OperationResult.Failure("Error.User.DescriptionTooLong", UserLimits.DescriptionMaxLength));
    }

    private static OperationResult ToResult(IdentityResult result) =>
        result.Succeeded ? OperationResult.Success : OperationResult.Failure(result.Errors.Select(e => LocalizedMessage.Raw(e.Description)));
}
