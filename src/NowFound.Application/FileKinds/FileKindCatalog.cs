namespace NowFound.Application.FileKinds;

/// <summary>拡張子から、表示するファイルの種類（アイコン）を引く。</summary>
public sealed class FileKindCatalog
{
    private readonly Dictionary<string, FileKind> _byExtension;

    private FileKindCatalog(FileKind folder, FileKind generic, IReadOnlyList<FileKind> kinds, Dictionary<string, FileKind> byExtension)
    {
        Folder = folder;
        Generic = generic;
        Kinds = kinds;
        _byExtension = byExtension;
    }

    public FileKind Folder { get; }

    /// <summary>定義にない拡張子（拡張子なしを含む）のファイル。</summary>
    public FileKind Generic { get; }

    /// <summary>フォルダと汎用のファイルを除く、拡張子に対応する種類。</summary>
    public IReadOnlyList<FileKind> Kinds { get; }

    /// <summary>組み込みの既定値によるカタログ。</summary>
    public static FileKindCatalog Default { get; } = Build(DefaultFileKinds.Definitions, strict: true);

    /// <summary>フォルダは拡張子に関係なくフォルダ。定義にない拡張子（拡張子なしを含む）は汎用のファイル。</summary>
    public FileKind For(string? extension, bool isDirectory)
    {
        if (isDirectory)
        {
            return Folder;
        }

        var key = FileKindRules.NormalizeExtension(extension);
        return key.Length > 0 && _byExtension.TryGetValue(key, out var kind) ? kind : Generic;
    }

    /// <param name="strict">true なら、同じ拡張子が複数の種類にあると例外にする（組み込みの既定値用）。
    /// false なら、先に定義された種類を優先し、画面が壊れないようにする（DB の手動編集などへの備え）。</param>
    public static FileKindCatalog Build(IEnumerable<FileKindDefinition> definitions, bool strict = false)
    {
        var list = definitions.ToList();
        var folderDefinition = list.FirstOrDefault(d => d.Key == DefaultFileKinds.FolderKey)
            ?? DefaultFileKinds.Definitions.First(d => d.Key == DefaultFileKinds.FolderKey);
        var genericDefinition = list.FirstOrDefault(d => d.Key == DefaultFileKinds.GenericKey)
            ?? DefaultFileKinds.Definitions.First(d => d.Key == DefaultFileKinds.GenericKey);

        var byExtension = new Dictionary<string, FileKind>(StringComparer.Ordinal);
        var kinds = new List<FileKind>();
        foreach (var definition in list.Where(d => !d.IsSystem && d.Key is not (DefaultFileKinds.FolderKey or DefaultFileKinds.GenericKey)))
        {
            var kind = new FileKind(definition.Key, definition.Label, definition.Icon, definition.LabelKey);
            kinds.Add(kind);
            foreach (var extension in definition.Extensions)
            {
                var key = FileKindRules.NormalizeExtension(extension);
                if (key.Length == 0)
                {
                    continue;
                }

                if (byExtension.TryGetValue(key, out var existing) && existing != kind)
                {
                    if (strict)
                    {
                        throw new InvalidOperationException($"拡張子 '{key}' が複数の種類（{existing.Id} / {kind.Id}）に定義されています。");
                    }

                    continue;
                }

                byExtension[key] = kind;
            }
        }

        return new FileKindCatalog(ToKind(folderDefinition), ToKind(genericDefinition), kinds, byExtension);
    }

    private static FileKind ToKind(FileKindDefinition definition) => new(definition.Key, definition.Label, definition.Icon, definition.LabelKey);
}
