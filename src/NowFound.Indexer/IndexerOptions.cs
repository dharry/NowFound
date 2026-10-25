namespace NowFound.Indexer;

/// <summary>コマンドライン引数。解析に失敗した内容は <see cref="Errors"/> に入る。</summary>
public sealed record IndexerOptions(
    string MainDb, string FilesDb, bool Full, bool ShowHelp, IReadOnlyList<string> Errors)
{
    public const string DefaultMainDb = "main.db";
    public const string DefaultFilesDb = "files.db";

    private static readonly string[] HelpFlags = ["-h", "--help", "-help", "-?", "/?", "/h", "/help"];

    public static IndexerOptions Parse(IReadOnlyList<string> args)
    {
        var mainDb = DefaultMainDb;
        var filesDb = DefaultFilesDb;
        var full = false;
        var help = false;
        var errors = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            // --main-db=path 形式も受け付ける。
            var (name, inlineValue) = SplitInlineValue(arg);

            if (HelpFlags.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                help = true;
            }
            else if (name.Equals("--full", StringComparison.OrdinalIgnoreCase) && inlineValue is null)
            {
                full = true;
            }
            else if (name.Equals("--main-db", StringComparison.OrdinalIgnoreCase))
            {
                mainDb = ReadValue(name, inlineValue, args, ref i, errors) ?? mainDb;
            }
            else if (name.Equals("--files-db", StringComparison.OrdinalIgnoreCase))
            {
                filesDb = ReadValue(name, inlineValue, args, ref i, errors) ?? filesDb;
            }
            else
            {
                errors.Add($"Unknown argument: {arg}");
            }
        }

        return new IndexerOptions(mainDb, filesDb, full, help, errors);
    }

    /// <summary>ヘルプ（使い方）の本文。</summary>
    public static string Usage(string programName) => $"""
        {programName} - NowFound file information collector

        Scans files according to the "search folders" configured in the Web console's main.db,
        and creates/updates the search database files.db. File contents are never modified.

        Usage:
          {programName} [options]

        Options:
          --main-db <path>    Web console settings DB (opened read-only)
                              Default: {DefaultMainDb} (current directory)
          --files-db <path>   Output search DB. Created if missing; the schema is upgraded to the latest
                              Default: {DefaultFilesDb} (current directory)
          --full              Rescan everything (full mode)
          -h, --help          Show this help and exit

        Modes:
          Default (fast mode) Folders whose modified time is unchanged since the last run are not re-read;
                              only changes are applied. Owners are looked up only for new or changed files.
          --full              Scans every folder and file. Also picks up files that were merely overwritten
                              and owner-only changes. It is slow, so it suits periodic runs (e.g. weekly).
          The first run for a search folder, or a run after its path/exclude settings changed,
          automatically scans that folder in full even without --full.

        Examples:
          {programName}
          {programName} --main-db D:\NowFound\main.db --files-db D:\NowFound\files.db
          {programName} --main-db D:\NowFound\main.db --files-db D:\NowFound\files.db --full

        Notes:
          - Specify the same main.db that the Web console uses with --main-db.
          - Do not run multiple {programName} processes against the same files.db at once.
          - Ctrl+C interrupts safely (results gathered so far are kept).
          - Warnings and errors are written to the logs directory next to the executable.

        Exit codes:
          0  Success
          1  Invalid arguments, or main.db not found
          2  Interrupted by Ctrl+C
          3  Terminated by an unexpected error
        """;

    private static (string Name, string? Value) SplitInlineValue(string arg)
    {
        var index = arg.IndexOf('=');
        return arg.StartsWith("--", StringComparison.Ordinal) && index > 2
            ? (arg[..index], arg[(index + 1)..])
            : (arg, null);
    }

    private static string? ReadValue(
        string name, string? inlineValue, IReadOnlyList<string> args, ref int index, List<string> errors)
    {
        if (inlineValue is not null)
        {
            if (inlineValue.Length > 0)
            {
                return inlineValue;
            }

            errors.Add($"{name} requires a value.");
            return null;
        }

        // 次の引数が値。ただし別のオプション（-- や -h で始まるもの）は値として扱わない。
        if (index + 1 < args.Count && !LooksLikeOption(args[index + 1]))
        {
            return args[++index];
        }

        errors.Add($"{name} requires a value.");
        return null;
    }

    private static bool LooksLikeOption(string value) =>
        value.StartsWith("--", StringComparison.Ordinal) || HelpFlags.Contains(value, StringComparer.OrdinalIgnoreCase);
}
