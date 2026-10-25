using System.Globalization;
using System.Text.RegularExpressions;

namespace NowFound.Application.FileKinds;

/// <summary>管理者が入力した種類の設定の正規化と検証。</summary>
public static partial class FileKindRules
{
    public const int MaxKinds = 100;
    public const int MaxLabelLength = 50;

    /// <summary>アイコンの最大の長さ（見た目の 1 文字を 1 と数える。絵文字の合成列も 1 文字）。</summary>
    public const int MaxIconTextElements = 8;

    public const int MaxExtensionsPerKind = 500;
    public const int MaxExtensionLength = 20;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex KeyPattern();

    /// <summary>入力の区切り: カンマ（半角・全角・読点）、セミコロン、空白、改行。</summary>
    [GeneratedRegex(@"[,，、;；\s]+")]
    private static partial Regex ExtensionSeparator();

    /// <summary>拡張子に使えない文字: パスの区切り、ワイルドカード、引用符、制御文字など。</summary>
    [GeneratedRegex(@"[\\/:*?""<>|\p{C}]")]
    private static partial Regex InvalidExtensionCharacter();

    /// <summary>前後の空白と先頭のドットを除き、小文字にする。</summary>
    public static string NormalizeExtension(string? extension) =>
        (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// 入力された編集内容を検証し、保存できる定義にする。エラーがあるときは Definitions が null。
    /// 並び順は入力どおり。フォルダと汎用のファイル（システム種類）は、それぞれちょうど 1 行必要。
    /// </summary>
    public static FileKindValidation Validate(IReadOnlyList<FileKindEdit> edits)
    {
        var errors = new List<LocalizedMessage>();
        var definitions = new List<FileKindDefinition>();

        if (edits.Count > MaxKinds + 2)
        {
            errors.Add(new("Error.FileKind.TooMany", MaxKinds));
            return new FileKindValidation(null, errors);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensionOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var row = i + 1;
            var label = (edit.Label ?? string.Empty).Trim();
            var icon = (edit.Icon ?? string.Empty).Trim();
            var key = (edit.Key ?? string.Empty).Trim();
            var isSystem = key is DefaultFileKinds.FolderKey or DefaultFileKinds.GenericKey;

            ValidateLabel(label, row, labels, errors);
            ValidateIcon(icon, row, errors);

            if (key.Length == 0)
            {
                key = NewKey(keys);
            }
            else if (!isSystem && !KeyPattern().IsMatch(key))
            {
                errors.Add(new("Error.FileKind.Key.Invalid", row));
            }

            if (!keys.Add(key))
            {
                errors.Add(new("Error.FileKind.Key.Duplicate", row, key));
            }

            var extensions = isSystem
                ? []
                : ParseExtensions(edit.Extensions, label, row, errors);

            if (!isSystem)
            {
                foreach (var extension in extensions)
                {
                    if (extensionOwners.TryGetValue(extension, out var owner))
                    {
                        errors.Add(new("Error.FileKind.Extension.Duplicate", extension, owner, label));
                    }
                    else
                    {
                        extensionOwners[extension] = label;
                    }
                }
            }

            definitions.Add(new FileKindDefinition(key, label, icon, extensions, isSystem, DefaultFileKinds.LabelKeyFor(key, label)));
        }

        foreach (var required in new[] { (DefaultFileKinds.FolderKey, "Error.FileKind.Required.Folder"), (DefaultFileKinds.GenericKey, "Error.FileKind.Required.Generic") })
        {
            if (definitions.Count(d => d.Key == required.Item1) != 1)
            {
                errors.Add(new(required.Item2));
            }
        }

        return errors.Count == 0
            ? new FileKindValidation(definitions, [])
            : new FileKindValidation(null, [.. errors.DistinctBy(e => (e.Key, string.Join(',', e.FormatArgs())))]);
    }

    /// <summary>
    /// 入力文字列を拡張子の一覧にする（小文字・ドットなし・重複なし）。誤りは errors に追加する。
    /// ownerLabel が空でなければ「名前」で、空なら「n 行目」で、どの種類の誤りかを示す。
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string? raw, string? ownerLabel, int row, List<LocalizedMessage> errors)
    {
        var result = new List<string>();
        var named = !string.IsNullOrEmpty(ownerLabel);
        object owner = named ? ownerLabel! : row;
        string Key(string name) => $"Error.FileKind.Extension.{name}.{(named ? "Named" : "Row")}";

        foreach (var token in ExtensionSeparator().Split(raw ?? string.Empty))
        {
            // 「*.txt」のようなワイルドカード形式の入力は、先頭の「*.」だけ許して「txt」として扱う。
            var extension = NormalizeExtension(token.StartsWith("*.", StringComparison.Ordinal) ? token[2..] : token);
            if (extension.Length == 0 || result.Contains(extension))
            {
                continue;
            }

            if (InvalidExtensionCharacter().IsMatch(extension))
            {
                errors.Add(new(Key("InvalidChar"), owner, extension));
            }
            else if (extension.Contains('.'))
            {
                var last = extension[(extension.LastIndexOf('.') + 1)..];
                errors.Add(new(Key("Dotted"), owner, extension, last));
            }
            else if (extension.Length > MaxExtensionLength)
            {
                errors.Add(new(Key("TooLong"), owner, extension, MaxExtensionLength));
            }
            else
            {
                result.Add(extension);
            }
        }

        if (result.Count > MaxExtensionsPerKind)
        {
            errors.Add(new(Key("TooMany"), owner, MaxExtensionsPerKind));
        }

        return result;
    }

    private static void ValidateLabel(string label, int row, HashSet<string> labels, List<LocalizedMessage> errors)
    {
        if (label.Length == 0)
        {
            errors.Add(new("Error.FileKind.Label.Required", row));
        }
        else if (label.Length > MaxLabelLength)
        {
            errors.Add(new("Error.FileKind.Label.TooLong", row, MaxLabelLength));
        }
        else if (!labels.Add(label))
        {
            errors.Add(new("Error.FileKind.Label.Duplicate", label));
        }
    }

    private static void ValidateIcon(string icon, int row, List<LocalizedMessage> errors)
    {
        if (icon.Length == 0)
        {
            errors.Add(new("Error.FileKind.Icon.Required", row));
        }
        else if (icon.Any(char.IsControl))
        {
            errors.Add(new("Error.FileKind.Icon.InvalidChar", row));
        }
        else if (new StringInfo(icon).LengthInTextElements > MaxIconTextElements)
        {
            errors.Add(new("Error.FileKind.Icon.TooLong", row, MaxIconTextElements));
        }
    }

    private static string NewKey(HashSet<string> used)
    {
        string key;
        do
        {
            key = "custom-" + Guid.NewGuid().ToString("N")[..8];
        }
        while (used.Contains(key));

        return key;
    }
}
