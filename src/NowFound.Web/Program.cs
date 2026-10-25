using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Infrastructure;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;
using NowFound.Web.Logging;
using NowFound.Web.Security;
using NowFound.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.ModelMetadataDetailsProviders.Add(new KeepEmptyStringsMetadataProvider());
});

// Windows サービス / systemd 配下で動かす。コンソール起動では何もしないため run.bat / run.sh と共存できる。
builder.Host.UseWindowsService(options => options.ServiceName = "NowFound");
builder.Host.UseSystemd();

// 起動・停止・未処理の例外を logs ディレクトリへ出力する（Logging:File:Enabled=false で無効化できる）。
NLog.LogFactory? nlogFactory = null;
if (builder.Configuration.GetValue(WebLogging.ConfigurationKey, true))
{
    nlogFactory = new NLog.LogFactory
    {
        Configuration = WebLogging.CreateConfiguration(WebLogging.LogDirectory(builder.Environment.ContentRootPath)),
    };
    builder.Logging.AddProvider(new NLogLoggerProvider(new NLogProviderOptions(), nlogFactory));
}

// 相対パスの DB はコンテンツルート基準で解決する（サービス実行時は作業ディレクトリが不定のため）。
string ResolveDb(string name, string fallback) =>
    SqliteDataSource.Resolve(builder.Configuration.GetConnectionString(name) ?? fallback, builder.Environment.ContentRootPath);

builder.Services.AddInfrastructure(
    ResolveDb("MainDb", "Data Source=main.db"),
    ResolveDb("FilesDb", "Data Source=files.db"),
    ResolveDb("OperationLogDb", "Data Source=ope.db"));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RequestLogger>();
builder.Services.AddSingleton<ExportLimiter>();

// 多言語化。言語の決め方は LanguageHelper を参照（ユーザー設定 → Cookie → Accept-Language → 既定）。
builder.Services.AddLocalization();
builder.Services.AddScoped<IMessageTranslator, MessageTranslator>();
builder.Services.AddScoped<IdentityErrorDescriber, LocalizedIdentityErrorDescriber>();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var cultures = AppLanguages.All.Select(l => new System.Globalization.CultureInfo(l.Id)).ToList();
    options.DefaultRequestCulture = new RequestCulture(AppLanguages.Default);
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
    options.RequestCultureProviders =
    [
        new UserLanguageRequestCultureProvider(),
        new LanguageCookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider(),
    ];
});

builder.Services.AddScoped<IUserClaimsPrincipalFactory<AppUser>, AppUserClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
    var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

    if (knownProxies.Length > 0 || knownNetworks.Length > 0)
    {
        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.All;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in knownProxies)
        {
            if (System.Net.IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }

        foreach (var network in knownNetworks)
        {
            if (System.Net.IPNetwork.TryParse(network, out var parsedNetwork))
            {
                options.KnownIPNetworks.Add(parsedNetwork);
            }
        }
    }
    else
    {
        // 信頼するプロキシまたはネットワークが明示されていない場合は、転送ヘッダーを無効化する。
        // （KnownProxies/Networks が空の状態で ForwardedHeaders.All を設定すると、
        // 任意の直接接続クライアントからの X-Forwarded-* が無条件に信頼されてしまうため）
        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None;
    }
});

builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var permitLimit = builder.Configuration.GetValue<int?>("RateLimiting:Login:PermitLimit") ?? 10;
    var windowSeconds = builder.Configuration.GetValue<int?>("RateLimiting:Login:WindowSeconds") ?? 60;
    options.AddPolicy("login", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ip, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
        });
    });
});

try
{
    var app = builder.Build();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NowFound.Web");
    app.Lifetime.ApplicationStarted.Register(() => logger.LogInformation("NowFound.Web started (environment: {Environment})", app.Environment.EnvironmentName));
    app.Lifetime.ApplicationStopping.Register(() => logger.LogInformation("NowFound.Web is stopping"));
    app.Lifetime.ApplicationStopped.Register(() => logger.LogInformation("NowFound.Web stopped"));

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        // files.db のスキーマのみ用意する（中身は Indexer が作る）。
        await scope.ServiceProvider.GetRequiredService<FilesDbContext>().Database.MigrateAsync();

        await scope.ServiceProvider.GetRequiredService<OperationLogDbContext>().Database.MigrateAsync();

        // アカウントポリシー（パスワード規則・ロック回数）を DB から読み込む。
        await scope.ServiceProvider.GetRequiredService<IAccountPolicyService>().GetAsync();

        var seed = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:SeedData");
        if (seed)
        {
            await DataSeeder.SeedAsync(
                scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>());
            await DataSeeder.SeedDefaultGroupAsync(db);
        }
    }

    app.UseForwardedHeaders();
    app.UseMiddleware<SecurityHeadersMiddleware>();

    // サブディレクトリ方式（例: /nowfound）。未設定ならドメイン方式。
    if (PathBaseSettings.Normalize(app.Configuration[PathBaseSettings.ConfigurationKey]) is { } pathBase)
    {
        app.UsePathBase(pathBase);
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseRequestLocalization();
    app.UseMiddleware<AccountStatusMiddleware>();
    app.UseMiddleware<ForcePasswordChangeMiddleware>();
    app.UseAuthorization();

    app.MapStaticAssets();
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // 起動中の失敗（DB 移行など）や異常終了を記録する。リクエスト中の未処理例外は ASP.NET Core が Error として記録する。
    (nlogFactory?.GetLogger("NowFound.Web"))?.Fatal(ex, "NowFound.Web terminated unexpectedly");
    throw;
}
finally
{
    nlogFactory?.Shutdown();
}

public partial class Program;
