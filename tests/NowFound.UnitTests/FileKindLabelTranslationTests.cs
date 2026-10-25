using Microsoft.Extensions.Localization;
using NowFound.Application.FileKinds;
using NowFound.Web.Resources;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class FileKindLabelTranslationTests
{
    private sealed class FakeLocalizer(Dictionary<string, string> texts) : IStringLocalizer<MessagesResource>
    {
        public LocalizedString this[string name] =>
            texts.TryGetValue(name, out var value) ? new(name, value, false) : new(name, name, true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private static MessageTranslator Translator() =>
        new(new FakeLocalizer(new() { ["FileKind.office"] = "Office files", ["FileKind.folder"] = "Folder" }));

    [Fact]
    public void BuiltInKind_IsTranslated_ByItsLabelKey()
    {
        var kind = FileKindCatalog.Default.For("docx", false);

        Assert.Equal("Office files", Translator().FileKindLabel(kind));
        Assert.Equal("Folder", Translator().FileKindLabel(FileKindCatalog.Default.Folder));
    }

    [Fact]
    public void CustomName_IsShownAsIs()
    {
        var kind = new FileKind("office", "事務ファイル", "📄");

        Assert.Equal("事務ファイル", Translator().FileKindLabel(kind));
    }
}
