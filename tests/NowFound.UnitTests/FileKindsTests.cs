using NowFound.Application.FileKinds;

namespace NowFound.UnitTests;

public class FileKindsTests
{
    private static readonly FileKindCatalog Catalog = FileKindCatalog.Default;

    /// <summary>
    /// 仕様として提示された種類と拡張子の一覧（書式の揺れ・重複も含め、そのまま）。
    /// 「css ,js ,」のような余分な空白、m / xz / m4a の重複、大文字の Z を含む。
    /// </summary>
    private static readonly (string KindId, string Label, string Extensions)[] Specification =
    [
        ("text", "テキストファイル", "txt,log,conf,config"),
        ("iso", "ISOイメージ", "iso"),
        ("executable", "実行ファイル", "exe,com,cmd,bat"),
        ("library", "ライブラリ", "dll "),
        ("archive", "圧縮アーカイブ", "zip,7z,rar,tar,gz,tgz,bz2,xz,lzh,cab,Z,lz4,xz,lzo,sz,zst,br,lzma"),
        ("code", "プログラム言語ファイル", "html,htm,css ,js ,ts ,jsx ,tsx ,py ,class ,c ,h ,cpp ,hpp ,cc ,cs ,go ,rs ,php ,rb ,kt ,swift ,dart ,m ,r ,ipynb ,m ,sql ,sh ,ps1 ,json ,yaml ,yml"),
        ("video", "動画", "mp4,m4v,mkv,webm,mov,qt,avi,wmv,mts,m2ts,vob,flv,f4v,mpg,mpeg,3gp,3g2"),
        ("image", "画像", "bmp,jpg,jpeg,png,gif,webp,avif,heic,heif,raw,dng,cr3,nef,arw,tif,tiff,svg,psd,ai,clip,jxl"),
        ("audio", "音楽", "mp3,m4a,aac,wma,ogg,oga,flac,wav,aif,aiff,alac,m4a,dsf,dff,amr,opus,mid,midi"),
        ("pdf", "PDFファイル", "pdf"),
        ("markdown", "マークダウン", "md"),
        ("database", "データベース", "db"),
        ("xml", "XMLファイル", "xml"),
        ("installer", "インストーラ", "ms,msi,msp,msm,msu,msix,msixbundle,appx,appxbundle"),
        ("registry", "レジストリ", "reg"),
    ];

    /// <summary>オフィスファイルの代表的な拡張子（提供元別）。一覧の全体は FileKinds にあり、ここでは主要なものを固定して確認する。</summary>
    public static TheoryData<string, string> OfficeExtensions() => new()
    {
        // Microsoft Word / Excel / PowerPoint
        { "doc", "Word" }, { "docx", "Word" }, { "docm", "Word" }, { "dotx", "Word" }, { "dotm", "Word" }, { "rtf", "Word" },
        { "xls", "Excel" }, { "xlsx", "Excel" }, { "xlsm", "Excel" }, { "xlsb", "Excel" }, { "xltx", "Excel" }, { "xlam", "Excel" },
        { "ppt", "PowerPoint" }, { "pptx", "PowerPoint" }, { "pptm", "PowerPoint" }, { "pps", "PowerPoint" }, { "ppsx", "PowerPoint" }, { "potx", "PowerPoint" },
        // Microsoft Access / Visio / Project / OneNote
        { "mdb", "Access" }, { "accdb", "Access" }, { "vsd", "Visio" }, { "vsdx", "Visio" }, { "mpp", "Project" }, { "one", "OneNote" },
        // LibreOffice / OpenDocument
        { "odt", "Writer" }, { "ott", "Writer" }, { "odm", "Writer" }, { "ods", "Calc" }, { "ots", "Calc" }, { "odp", "Impress" },
        { "otp", "Impress" }, { "odg", "Draw" }, { "odf", "Math" }, { "odb", "Base" }, { "fodt", "Flat XML" }, { "fods", "Flat XML" }, { "fodp", "Flat XML" },
        // Apple iWork / WPS Office / 一太郎
        { "pages", "iWork" }, { "numbers", "iWork" }, { "wps", "WPS" }, { "et", "WPS" }, { "dps", "WPS" }, { "jtd", "一太郎" },
    };

    [Theory]
    [MemberData(nameof(OfficeExtensions))]
    public void OfficeSuites_AreOfficeFiles(string extension, string suite)
    {
        var kind = Catalog.For(extension, false);

        Assert.True(kind?.Id == "office", $".{extension}（{suite}）がオフィスファイルとして扱われていない");
        Assert.Equal("オフィスファイル", kind!.Label);
    }

    /// <summary>Visual Studio のファイル（用途別）。</summary>
    public static TheoryData<string, string> VisualStudioExtensions() => new()
    {
        // ソリューション
        { "sln", "ソリューション" }, { "slnx", "ソリューション（新形式）" }, { "slnf", "ソリューションフィルター" },
        // プロジェクト
        { "csproj", "C#" }, { "vbproj", "VB" }, { "fsproj", "F#" }, { "vcxproj", "C++" }, { "vcproj", "C++（旧形式）" },
        { "vcxitems", "C++ 共有項目" }, { "sqlproj", "SQL" }, { "shproj", "共有プロジェクト" }, { "projitems", "共有項目" },
        { "wapproj", "パッケージング" }, { "pyproj", "Python" }, { "njsproj", "Node.js" }, { "vdproj", "セットアップ" },
        // 付随ファイル
        { "filters", "vcxproj.filters" }, { "props", "MSBuild 共通設定" }, { "targets", "MSBuild ターゲット" },
        { "suo", "ソリューション設定" }, { "vsix", "拡張機能" }, { "vssettings", "環境設定" }, { "vsconfig", "ワークロード構成" },
        { "pubxml", "発行プロファイル" }, { "ruleset", "コード分析ルール" },
    };

    [Theory]
    [MemberData(nameof(VisualStudioExtensions))]
    public void VisualStudioFiles_AreRecognised(string extension, string description)
    {
        var kind = Catalog.For(extension, false);

        Assert.True(kind?.Id == "visualstudio", $".{extension}（{description}）が Visual Studio ファイルとして扱われていない");
        Assert.Equal("Visual Studio ファイル", kind!.Label);
    }

    [Theory]
    [InlineData("SLN", "visualstudio")]
    [InlineData(".VcxProj", "visualstudio")]
    public void VisualStudioFiles_IgnoreCaseAndLeadingDots(string extension, string expectedKindId)
    {
        Assert.Equal(expectedKindId, Catalog.For(extension, false)!.Id);
    }

    [Theory]
    [InlineData("user")]    // .vcxproj.user など。他のアプリの設定ファイルにも使われる
    [InlineData("xaml")]
    [InlineData("resx")]
    public void GenericExtensionsUsedOutsideVisualStudio_AreNotVisualStudioFiles(string extension)
    {
        Assert.Same(Catalog.Generic, Catalog.For(extension, false));
    }

    [Theory]
    [InlineData("key")]     // 鍵ファイル（Apple Keynote と紛らわしい）
    [InlineData("pub")]     // SSH 公開鍵（Microsoft Publisher と紛らわしい）
    [InlineData("dot")]     // Graphviz（Word 97-2003 テンプレートと紛らわしい）
    [InlineData("pot")]     // gettext の翻訳テンプレート（PowerPoint 97-2003 テンプレートと紛らわしい）
    [InlineData("csv")]     // 区切りテキスト
    [InlineData("tsv")]
    public void AmbiguousExtensions_AreDeliberatelyNotOfficeFiles(string extension)
    {
        Assert.Same(Catalog.Generic, Catalog.For(extension, false));
    }

    [Theory]
    [InlineData("conf")]
    [InlineData("config")]
    [InlineData("CONFIG")]
    [InlineData(".Conf")]
    public void ConfigurationFiles_AreTextFiles(string extension)
    {
        var kind = Catalog.For(extension, false);

        Assert.Equal("text", kind!.Id);
        Assert.Same(Catalog.For("txt", false), kind);   // テキストファイルと同じ種類（同じアイコン）
    }

    [Theory]
    [InlineData("ms")]          // 指定どおり
    [InlineData("msi")]         // Windows インストーラ（ms は msi の意味と解釈）
    [InlineData("MSI")]
    [InlineData("msp")]         // パッチ
    [InlineData("msm")]         // マージモジュール
    [InlineData("msu")]         // Windows Update スタンドアロン
    [InlineData("msix")]
    [InlineData("msixbundle")]
    [InlineData("appx")]
    [InlineData("appxbundle")]
    public void InstallerPackages_AreInstallers(string extension)
    {
        Assert.Equal("installer", Catalog.For(extension, false)!.Id);
    }

    [Theory]
    [InlineData("reg", "registry")]
    [InlineData("REG", "registry")]
    [InlineData("xml", "xml")]
    [InlineData(".XML", "xml")]
    public void RegistryAndXml_AreRecognised(string extension, string expectedKindId)
    {
        Assert.Equal(expectedKindId, Catalog.For(extension, false)!.Id);
    }

    [Theory]
    [InlineData("PDF", "pdf")]
    [InlineData("Md", "markdown")]
    [InlineData("DB", "database")]
    [InlineData(".XLSX", "office")]
    public void NewKinds_IgnoreCaseAndLeadingDots(string extension, string expectedKindId)
    {
        Assert.Equal(expectedKindId, Catalog.For(extension, false)!.Id);
    }

    public static TheoryData<string, string> EveryListedExtension()
    {
        var data = new TheoryData<string, string>();
        foreach (var (kindId, _, extensions) in Specification)
        {
            foreach (var extension in extensions.Split(',').Select(e => e.Trim()).Distinct())
            {
                data.Add(extension, kindId);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryListedExtension))]
    public void ListedExtension_MapsToItsKind(string extension, string expectedKindId)
    {
        var kind = Catalog.For(extension, isDirectory: false);

        Assert.NotNull(kind);
        Assert.Equal(expectedKindId, kind.Id);
    }

    [Fact]
    public void EveryKind_HasLabelAndIcon_AsInTheSpecification()
    {
        foreach (var (kindId, label, extensions) in Specification)
        {
            var kind = Catalog.For(extensions.Split(',')[0], isDirectory: false);

            Assert.Equal(kindId, kind.Id);
            Assert.Equal(label, kind.Label);
            Assert.False(string.IsNullOrWhiteSpace(kind.Icon));
        }
    }

    [Fact]
    public void Icons_AreDistinctPerKind_IncludingFolder()
    {
        var kinds = Specification.Select(s => Catalog.For(s.Extensions.Split(',')[0], false))
            .Append(Catalog.Folder)
            .Append(Catalog.Generic)
            .ToList();

        Assert.Equal(kinds.Count, kinds.Select(k => k.Icon).Distinct().Count());
        Assert.Equal(kinds.Count, kinds.Select(k => k.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("EXE", "executable")]
    [InlineData("Mp4", "video")]
    [InlineData("Z", "archive")]
    [InlineData("z", "archive")]
    [InlineData(".dll", "library")]
    [InlineData("  png  ", "image")]
    [InlineData("..jpg", "image")]
    public void Lookup_IgnoresCaseSpacesAndLeadingDots(string extension, string expectedKindId)
    {
        Assert.Equal(expectedKindId, Catalog.For(extension, false)!.Id);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("csv")]
    [InlineData("sqlite")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData(null)]
    public void UnlistedOrEmptyExtension_IsAGenericFile(string? extension)
    {
        Assert.Same(Catalog.Generic, Catalog.For(extension, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("txt")]
    [InlineData("exe")]
    [InlineData(null)]
    public void Directory_IsAlwaysAFolder_RegardlessOfExtension(string? extension)
    {
        Assert.Same(Catalog.Folder, Catalog.For(extension, isDirectory: true));
    }

    [Fact]
    public void GenericFile_HasItsOwnIconAndLabel()
    {
        Assert.Equal("file", Catalog.Generic.Id);
        Assert.Equal("ファイル", Catalog.Generic.Label);
        Assert.False(string.IsNullOrWhiteSpace(Catalog.Generic.Icon));
    }

    [Theory]
    [InlineData("LICENSE", "")]
    [InlineData("data.xyz", "xyz")]
    [InlineData("Makefile", null)]
    [InlineData("archive.unknownext", "unknownext")]
    public void FilesWithoutAKnownExtension_GetTheGenericIcon_NeverNothing(string name, string? extension)
    {
        Assert.NotNull(name);
        Assert.Same(Catalog.Generic, Catalog.For(extension, isDirectory: false));
    }

    [Fact]
    public void TheDefinitions_DoNotClaimOneExtensionForTwoKinds()
    {
        // 重複定義があると型の初期化時に例外になる。ここで触れて、定義の矛盾をテストで検出する。
        var exception = Record.Exception(() => Catalog.For("txt", false));

        Assert.Null(exception);
    }
}
