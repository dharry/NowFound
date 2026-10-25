namespace NowFound.Application.FileKinds;

/// <summary>組み込みの既定の種類。管理者が編集するまで（または「既定に戻す」の後）は、これを使う。</summary>
public static class DefaultFileKinds
{
    public const string FolderKey = "folder";
    public const string GenericKey = "file";

    /// <summary>
    /// オフィスファイル: Microsoft Office、LibreOffice / OpenDocument、Apple iWork、WPS Office、一太郎の一般的な拡張子。
    /// 別の用途と紛らわしいものは含めない（.key = 鍵ファイル、.pub = SSH 公開鍵、.dot = Graphviz、
    /// .pot = gettext の翻訳テンプレート、.csv / .tsv = 区切りテキスト）。
    /// </summary>
    private const string OfficeExtensions =
        "doc,docx,docm,dotx,dotm,rtf," +                       // Word
        "xls,xlsx,xlsm,xlsb,xlt,xltx,xltm,xla,xlam," +           // Excel
        "ppt,pptx,pptm,potx,potm,pps,ppsx,ppsm," +               // PowerPoint
        "mdb,accdb,vsd,vsdx,vsdm,mpp,one," +                     // Access / Visio / Project / OneNote
        "odt,ott,odm,ods,ots,odp,otp,odg,otg,odf,odb," +         // LibreOffice / OpenDocument（Writer, Calc, Impress, Draw, Math, Base）
        "fodt,fods,fodp,fodg," +                                 // OpenDocument（フラット XML 形式）
        "pages,numbers," +                                       // Apple iWork（.key は鍵ファイルと紛らわしいため含めない）
        "wps,wpt,et,ett,dps,dpt," +                              // WPS Office
        "jtd";                                                   // 一太郎

    /// <summary>
    /// Visual Studio のソリューション・プロジェクト・設定ファイル。
    /// 別の用途と紛らわしいものは含めない（.user は他のアプリの設定にも使われる。.xaml / .resx は
    /// Visual Studio 以外でも使われるため対象外）。
    /// </summary>
    private const string VisualStudioExtensions =
        "sln,slnx,slnf," +                                            // ソリューション（.slnx は新形式、.slnf はソリューションフィルター）
        "csproj,vbproj,fsproj,vcxproj,vcproj,vcxitems,sqlproj," +      // プロジェクト（C# / VB / F# / C++ / SQL）
        "shproj,projitems,wapproj,pyproj,njsproj,dbproj,vdproj," +     // 共有プロジェクト・パッケージング・Python・Node.js・セットアップ
        "filters,props,targets," +                                    // C++ のフィルター、MSBuild の共通設定（.vcxproj.filters / .props / .targets）
        "suo,vsix,vssettings,vsconfig,pubxml,ruleset";                // ソリューション設定・拡張機能・環境設定・発行プロファイル・コード分析ルール

    /// <summary>種類ごとの拡張子。1 つの拡張子は 1 つの種類にだけ属する。</summary>
    public static readonly IReadOnlyList<FileKindDefinition> Definitions =
    [
        new(FolderKey, "フォルダ", "📁", [], IsSystem: true, LabelKey: LabelKeyOf(FolderKey)),
        new(GenericKey, "ファイル", "📃", [], IsSystem: true, LabelKey: LabelKeyOf(GenericKey)),
        Kind("text", "テキストファイル", "📝", "txt,log,conf,config"),
        Kind("iso", "ISOイメージ", "💿", "iso"),
        Kind("executable", "実行ファイル", "⚙️", "exe,com,cmd,bat"),
        Kind("library", "ライブラリ", "🧩", "dll"),
        Kind("archive", "圧縮アーカイブ", "📦", "zip,7z,rar,tar,gz,tgz,bz2,xz,lzh,cab,z,lz4,lzo,sz,zst,br,lzma"),
        Kind("code", "プログラム言語ファイル", "💻",
            "html,htm,css,js,ts,jsx,tsx,py,class,c,h,cpp,hpp,cc,cs,go,rs,php,rb,kt,swift,dart,m,r,ipynb,sql,sh,ps1,json,yaml,yml"),
        Kind("video", "動画", "🎬", "mp4,m4v,mkv,webm,mov,qt,avi,wmv,mts,m2ts,vob,flv,f4v,mpg,mpeg,3gp,3g2"),
        Kind("image", "画像", "🖼️", "bmp,jpg,jpeg,png,gif,webp,avif,heic,heif,raw,dng,cr3,nef,arw,tif,tiff,svg,psd,ai,clip,jxl"),
        Kind("audio", "音楽", "🎵", "mp3,m4a,aac,wma,ogg,oga,flac,wav,aif,aiff,alac,dsf,dff,amr,opus,mid,midi"),
        Kind("pdf", "PDFファイル", "📕", "pdf"),
        Kind("office", "オフィスファイル", "📄", OfficeExtensions),
        Kind("markdown", "マークダウン", "Ⓜ️", "md"),
        Kind("database", "データベース", "🗄️", "db"),
        Kind("visualstudio", "Visual Studio ファイル", "🛠️", VisualStudioExtensions),
        Kind("xml", "XMLファイル", "🏷️", "xml"),
        // ms は msi の意味で指定された。Windows インストーラと、その関連パッケージ。
        Kind("installer", "インストーラ", "📥", "ms,msi,msp,msm,msu,msix,msixbundle,appx,appxbundle"),
        Kind("registry", "レジストリ", "🔑", "reg"),
    ];

    /// <summary>組み込みの名前の翻訳キー（Messages リソース）。</summary>
    public static string LabelKeyOf(string key) => "FileKind." + key;

    /// <summary>
    /// 組み込みの種類で、名前が既定（日本語）のままなら翻訳キー。管理者が名前を変えていれば null（そのまま表示する）。
    /// 名前は DB に日本語の文字列として保存されるため、一致で判定する。
    /// </summary>
    public static string? LabelKeyFor(string key, string label) =>
        Definitions.FirstOrDefault(d => d.Key == key) is { } builtIn && builtIn.Label == label ? builtIn.LabelKey : null;

    private static FileKindDefinition Kind(string key, string label, string icon, string extensions) =>
        new(key, label, icon, [.. extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], IsSystem: false, LabelKey: LabelKeyOf(key));
}
