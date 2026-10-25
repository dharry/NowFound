using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NowFound.Application.FileKinds;
using NowFound.Application.Logging;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Controllers;
using NowFound.Web.Models;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class HomeControllerTests
{
    private readonly Mock<ISearchService> _searchService = new();
    private readonly Mock<ISavedSearchService> _savedSearchService = new();
    private readonly Mock<IFileKindService> _fileKindService = new();
    private readonly Mock<IAdminSettingsService> _adminSettings = new();
    private readonly Mock<IOperationLogService> _logService = new();

    public HomeControllerTests()
    {
        _adminSettings.Setup(a => a.GetExportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ExportSettings.Default);
    }

    private HomeController CreateController(bool isIncremental = false, bool isAdmin = false, string userId = "test-user")
    {
        var httpContext = new DefaultHttpContext();
        if (isIncremental)
        {
            httpContext.Request.Headers[HomeController.IncrementalHeader] = "1";
        }

        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));
        if (isAdmin)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, AppRoles.Admin));
        }
        httpContext.User = new ClaimsPrincipal(identity);

        var translatorMock = new Mock<IMessageTranslator>();
        translatorMock.Setup(t => t.Translate(It.IsAny<string>(), It.IsAny<object?[]>()))
            .Returns<string, object?[]>((key, args) => $"{key}:{string.Join(',', args ?? [])}");

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IMessageTranslator)))
            .Returns(translatorMock.Object);
        httpContext.RequestServices = serviceProviderMock.Object;

        var accessorMock = new Mock<IHttpContextAccessor>();
        accessorMock.Setup(a => a.HttpContext).Returns(httpContext);

        var requestLogger = new RequestLogger(_logService.Object, accessorMock.Object);
        var localizer = SearchLocalizerTestHelper.Create();

        var controller = new HomeController(
            _searchService.Object,
            _savedSearchService.Object,
            _fileKindService.Object,
            _adminSettings.Object,
            new ExportLimiter(),
            requestLogger,
            localizer)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                httpContext,
                Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>()),
        };

        _fileKindService.Setup(f => f.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileKindCatalog.Default);
        _searchService.Setup(s => s.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchPage([], 1, 100, 0));
        _searchService.Setup(s => s.ResolvePageSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);

        return controller;
    }

    [Fact]
    public async Task Index_WhenIncrementalHeaderPresent_ReturnsPartialViewResult_AndSkipsMasterDataLoading()
    {
        var controller = CreateController(isIncremental: true);
        var model = new SearchViewModel
        {
            Conditions = [new SearchCondition { Field = SearchField.FileName, Text = "test" }],
        };

        var result = await controller.Index(model, CancellationToken.None);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_SearchResultsPartial", partial.ViewName);
        Assert.Same(model, partial.Model);

        // Folders, SavedSearches, IncrementalSettings の取得がスキップされていること
        _searchService.Verify(s => s.GetAccessibleFoldersAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _savedSearchService.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _adminSettings.Verify(a => a.GetIncrementalSearchAsync(It.IsAny<CancellationToken>()), Times.Never);

        // アイコン判定のための FileKinds と検索自体は行われること
        _fileKindService.Verify(f => f.GetCatalogAsync(It.IsAny<CancellationToken>()), Times.Once);
        _searchService.Verify(s => s.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Index_WhenNormalRequest_ReturnsFullViewResult_AndLoadsAllMasterData()
    {
        var controller = CreateController(isIncremental: false);
        var model = new SearchViewModel
        {
            Conditions = [new SearchCondition { Field = SearchField.FileName, Text = "test" }],
        };

        _searchService.Setup(s => s.GetAccessibleFoldersAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _savedSearchService.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _adminSettings.Setup(a => a.GetIncrementalSearchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(IncrementalSearchSettings.Default);

        var result = await controller.Index(model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.ViewName); // 既定の View("Index")
        Assert.Same(model, view.Model);

        // 通常リクエスト時はサイドバー・設定表示のためにマスターデータが取得されること
        _searchService.Verify(s => s.GetAccessibleFoldersAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _savedSearchService.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _adminSettings.Verify(a => a.GetIncrementalSearchAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Index_WhenIncrementalAndHasSavedId_LoadsSingleSavedSearch()
    {
        var controller = CreateController(isIncremental: true);
        var model = new SearchViewModel
        {
            Saved = 42,
            Conditions = [new SearchCondition { Field = SearchField.FileName, Text = "test" }],
        };

        var expectedSummary = new SavedSearchSummary(42, "Saved", null, false, false, true, "u@ex.com", DateTime.UtcNow);
        _savedSearchService.Setup(s => s.GetAsync(42, "test-user", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SavedSearchDetail(expectedSummary, null));

        var result = await controller.Index(model, CancellationToken.None);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_SearchResultsPartial", partial.ViewName);
        Assert.Equal(expectedSummary, model.CurrentSaved);

        _savedSearchService.Verify(s => s.GetAsync(42, "test-user", false, It.IsAny<CancellationToken>()), Times.Once);
        _savedSearchService.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Index_WhenIncrementalAndValidationError_ReturnsPartialViewWithWarnings()
    {
        var controller = CreateController(isIncremental: true);
        var model = new SearchViewModel
        {
            Conditions = [new SearchCondition { Field = SearchField.FileName, Text = new string('x', 201) }],
        };

        var result = await controller.Index(model, CancellationToken.None);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_SearchResultsPartial", partial.ViewName);
        Assert.NotEmpty(model.Warnings);
        Assert.Empty(model.Result!.Items);

        // 検索実行および操作ログ記録は抑制されること
        _searchService.Verify(s => s.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _logService.Verify(l => l.WriteAsync(It.IsAny<OperationLogEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
