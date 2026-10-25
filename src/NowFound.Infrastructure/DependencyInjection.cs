using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NowFound.Application.FileKinds;
using NowFound.Application.Information;
using NowFound.Application.Logging;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;
using NowFound.Infrastructure.Information;
using NowFound.Infrastructure.Logging;
using NowFound.Infrastructure.Search;
using NowFound.Infrastructure.Settings;

namespace NowFound.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string mainDbConnectionString, string filesDbConnectionString = "Data Source=files.db",
        string operationLogDbConnectionString = "Data Source=ope.db")
    {
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseSqlite(mainDbConnectionString)
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));

        services.AddDbContext<FilesDbContext>((sp, options) => options
            .UseSqlite(filesDbConnectionString)
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));

        services.AddDbContext<OperationLogDbContext>((sp, options) => options
            .UseSqlite(operationLogDbConnectionString)
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));

        // パスワードの規則（最小文字数・複雑性）とロック回数は、管理者が実行中に変えられるアカウントポリシーで決める。
        // そのため Identity 標準のパスワード規則は無効にし、PolicyPasswordValidator で検証する。
        services.AddSingleton<AccountPolicyStore>();
        services.AddSingleton<IAccountPolicyStore>(sp => sp.GetRequiredService<AccountPolicyStore>());
        services.AddScoped<IAccountPolicyService, AccountPolicyService>();

        services.AddIdentity<AppUser, IdentityRole>(options =>
            {
                // ActiveDirectory のユーザーは Email 欄に sAMAccountName を入れるため、Identity による形式検証は行わない。
                // 標準ユーザーのメールアドレスの検証と重複防止は UserAdminService（UserName の一意性）が担う。
                options.User.RequireUniqueEmail = false;
                // 'ad:' 接頭辞や '$' など、既定の許可文字に無い文字を UserName に使う。形式は UserAdminService が検証する。
                options.User.AllowedUserNameCharacters = string.Empty;
                options.Password.RequiredLength = 1;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.DefaultLockoutTimeSpan = AccountPolicy.LockoutDuration;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<AppIdentityErrorDescriber>()
            .AddPasswordValidator<PolicyPasswordValidator>();

        // ロック回数だけは実行中に変わるため、リクエスト（スコープ）ごとに現在のポリシーを反映した設定を作る。
        services.AddScoped<IOptions<IdentityOptions>>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsFactory<IdentityOptions>>().Create(Options.DefaultName);
            var attempts = sp.GetRequiredService<IAccountPolicyStore>().Current.MaxFailedAttempts;
            options.Lockout.MaxFailedAccessAttempts = attempts > 0 ? attempts : int.MaxValue;
            return new OptionsWrapper<IdentityOptions>(options);
        });

        services.AddScoped<IPasswordHasher<AppUser>, Pbkdf2Sha256PasswordHasher>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IActiveDirectorySettingsService, ActiveDirectorySettingsService>();
        services.AddSingleton<IDirectoryAuthenticator, LdapDirectoryAuthenticator>();
        services.AddScoped<IAdminSettingsService, AdminSettingsService>();
        services.AddScoped<IFileKindService, FileKindService>();
        services.AddScoped<ISettingsTransferService, SettingsTransferService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ISavedSearchService, SavedSearchService>();
        services.AddSingleton<IOperationLogQueue, OperationLogQueue>();
        services.AddHostedService<OperationLogBackgroundService>();
        services.AddScoped<IOperationLogService, OperationLogService>();
        services.AddScoped<IHostInfoService, HostInfoService>();
        services.AddMemoryCache();
        services.AddSingleton<IAccountStatusCache, MemoryAccountStatusCache>();
        services.AddScoped<IFooterInfoService, FooterInfoService>();
        return services;
    }
}
