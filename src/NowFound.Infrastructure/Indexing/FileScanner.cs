using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NowFound.Application.Indexing;

namespace NowFound.Infrastructure.Indexing;

/// <summary>指定フォルダ配下を走査してファイル／フォルダ情報を列挙する。</summary>
public class FileScanner(ILogger<FileScanner>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    private readonly EnumerationOptions _enumerationOptions = new()
    {
        // 読めないディレクトリを黙って飛ばさず、例外として受けて警告ログに残すため false にする（再帰しないので影響は対象ディレクトリのみ）。
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <param name="cache">指定すると、前回と更新日時が同じディレクトリの直下は読み直さずサブディレクトリだけ辿る。</param>
    /// <param name="onError">読み取りエラー（IOException / UnauthorizedAccessException）発生時に呼び出されるコールバック。</param>
    public IEnumerable<ScannedEntry> Scan(IndexTarget target, IScanCache? cache = null, Action<string, Exception>? onError = null)
    {
        var root = new DirectoryInfo(target.Path);
        if (!root.Exists)
        {
            yield break;
        }

        var matcher = new ExcludeMatcher(target.ExcludePatterns);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            if (cache is not null && cache.TryGetUnchanged(dir.FullName, dir.LastWriteTimeUtc, out var knownSubdirectories))
            {
                foreach (var path in knownSubdirectories)
                {
                    ScannedEntry? subEntry;
                    DirectoryInfo? sub;
                    try
                    {
                        sub = new DirectoryInfo(path);
                        if (!sub.Exists)
                        {
                            continue;
                        }

                        // 親を読み直さなくても、サブディレクトリ自身の時刻は更新して返す。
                        // （サブ直下のファイル追加などで時刻が変わっていると、次回以降も毎回再走査になってしまう）
                        subEntry = new ScannedEntry(
                            sub.Name, dir.FullName, sub.FullName, true, null, string.Empty, Owner: null,
                            sub.LastWriteTimeUtc, sub.CreationTimeUtc);

                        // シンボリックリンク等によるループを避けるため、リンク先は辿らない。
                        if (sub.LinkTarget is null)
                        {
                            pending.Push(sub);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogSkipped(path, ex);
                        onError?.Invoke(path, ex);
                        continue;
                    }

                    yield return subEntry;
                }

                // ハイブリッド差分スキャン: ディレクトリの mtime が一致していても、直下ファイルの上書き編集（mtime/size変更）を検知するため直下ファイルのみ走査する
                IEnumerable<FileInfo> files;
                try
                {
                    files = dir.EnumerateFiles("*", _enumerationOptions);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogSkipped(dir.FullName, ex);
                    onError?.Invoke(dir.FullName, ex);
                    continue;
                }

                foreach (var file in files)
                {
                    ScannedEntry? entry;
                    try
                    {
                        entry = CreateEntry(file, dir, matcher, target.ExcludeHidden, pending);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogSkipped(file.FullName, ex);
                        onError?.Invoke(file.FullName, ex);
                        continue;
                    }

                    if (entry is not null)
                    {
                        yield return entry;
                    }
                }

                continue;
            }

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", _enumerationOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSkipped(dir.FullName, ex);
                onError?.Invoke(dir.FullName, ex);
                continue;
            }

            foreach (var child in children)
            {
                ScannedEntry? entry;
                try
                {
                    entry = CreateEntry(child, dir, matcher, target.ExcludeHidden, pending);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 走査中の削除・権限変更などで 1 件だけ読めない場合は、その項目だけ飛ばして続行する。
                    LogSkipped(child.FullName, ex);
                    onError?.Invoke(child.FullName, ex);
                    continue;
                }

                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>1 件分のエントリを作る。除外対象なら null。読み取りに失敗した場合は IOException / UnauthorizedAccessException を投げる。</summary>
    protected virtual ScannedEntry? CreateEntry(
        FileSystemInfo child, DirectoryInfo dir, ExcludeMatcher matcher, bool excludeHidden, Stack<DirectoryInfo> pending)
    {
        var isDirectory = child is DirectoryInfo;
        if (matcher.IsExcluded(child.Name, isDirectory) || (excludeHidden && IsHidden(child)))
        {
            return null;
        }

        if (isDirectory)
        {
            // NTFS では、列挙で得られるディレクトリの時刻（親のインデックスの写し）が実際より遅れて反映される。
            // 枝刈りの判定に使うため、ディレクトリだけは取り直して最新の時刻を使う。
            child.Refresh();
            if (child.LinkTarget is null)
            {
                pending.Push((DirectoryInfo)child);
            }
        }

        return new ScannedEntry(
            child.Name,
            dir.FullName,
            child.FullName,
            isDirectory,
            isDirectory ? null : ((FileInfo)child).Length,
            isDirectory ? string.Empty : child.Extension.TrimStart('.').ToLowerInvariant(),
            Owner: null,
            child.LastWriteTimeUtc,
            child.CreationTimeUtc);
    }

    private void LogSkipped(string path, Exception ex) =>
        _logger.LogWarning(ex, "Skipped because it could not be accessed: {Path} ({Reason})", path, ex.Message);

    /// <summary>オーナー名を取得する。Windows 以外、または取得できない場合は null。</summary>
    public virtual string? GetOwner(string fullPath, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return GetWindowsOwner(fullPath, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IdentityNotMappedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsHidden(FileSystemInfo info) =>
        info.Name.StartsWith('.') || (info.Attributes & FileAttributes.Hidden) != 0;

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsOwner(string fullPath, bool isDirectory)
    {
        var sid = isDirectory
            ? new DirectoryInfo(fullPath).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
            : new FileInfo(fullPath).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));

        return (sid as SecurityIdentifier)?.Translate(typeof(NTAccount)).Value;
    }
}
