namespace NowFound.Domain.Entities;

/// <summary>システム全体の設定（キー/値）。</summary>
public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public const string PageSizeKey = "PageSize";
    public const int DefaultPageSize = 100;

    /// <summary>操作ログ一覧のページサイズ。検索結果のページサイズとは別に設定する。</summary>
    public const string LogPageSizeKey = "LogPageSize";
    public const int DefaultLogPageSize = 100;

    /// <summary>ユーザー管理・グループ管理・検索対象フォルダの一覧のページサイズ。</summary>
    public const string AdminListPageSizeKey = "AdminListPageSize";

    /// <summary>管理画面の一覧と、ブックマークのページサイズの既定値（ブックマークはユーザー設定で変更できる）。</summary>
    public const int DefaultListPageSize = 50;

    /// <summary>新規フォルダの既定の除外パターン（改行区切り）。行が無ければ組み込みの既定値。</summary>
    public const string DefaultExcludePatternsKey = "DefaultExcludePatterns";

    /// <summary>インクリメンタルサーチを使うか（true / false）。</summary>
    public const string IncrementalSearchEnabledKey = "IncrementalSearchEnabled";

    /// <summary>インクリメンタルサーチを始める入力文字数。</summary>
    public const string IncrementalSearchMinCharsKey = "IncrementalSearchMinChars";

    /// <summary>インクリメンタルサーチで、最後の入力から検索を始めるまでの待ち時間（ミリ秒）。</summary>
    public const string IncrementalSearchDelayMsKey = "IncrementalSearchDelayMs";

    /// <summary>検索結果のエクスポートを一般ユーザーにも許可するか（true / false）。既定は管理者のみ。</summary>
    public const string ExportAllowAllUsersKey = "ExportAllowAllUsers";

    /// <summary>検索結果のエクスポートで書き出す最大行数。</summary>
    public const string ExportMaxRowsKey = "ExportMaxRows";

    /// <summary>パスワードの最小文字数（アカウントポリシー）。</summary>
    public const string PasswordMinLengthKey = "PasswordMinLength";

    /// <summary>パスワードの複雑性を要求するか（true / false）。</summary>
    public const string PasswordComplexityKey = "PasswordComplexity";

    /// <summary>ログイン失敗でロックするまでの回数。0 はロックしない。</summary>
    public const string MaxFailedAttemptsKey = "MaxFailedAttempts";

    /// <summary>パスワードの有効日数。0 は無期限。</summary>
    public const string PasswordMaxAgeDaysKey = "PasswordMaxAgeDays";

    /// <summary>再利用を禁止する直近のパスワードの数（現在のパスワードを含む）。0 は禁止しない。</summary>
    public const string PasswordHistoryCountKey = "PasswordHistoryCount";

    /// <summary>ActiveDirectory 認証を使うか（true / false）。</summary>
    public const string AdEnabledKey = "AdEnabled";

    /// <summary>ActiveDirectory（ドメインコントローラー）のホスト名または IP アドレス。</summary>
    public const string AdServerKey = "AdServer";

    public const string AdPortKey = "AdPort";

    /// <summary>接続の保護方式（DirectorySecurity の名前）。</summary>
    public const string AdSecurityKey = "AdSecurity";

    /// <summary>UPN のサフィックス（例: corp.example.com）。</summary>
    public const string AdDomainKey = "AdDomain";

    /// <summary>ユーザーの存在確認に使う確認用アカウント（読み取り専用）。</summary>
    public const string AdBindUserKey = "AdBindUser";

    /// <summary>確認用アカウントのパスワード。Data Protection で暗号化した値（平文では保存しない）。</summary>
    public const string AdBindPasswordKey = "AdBindPassword";

    /// <summary>存在確認で検索する起点の DN。空ならドメインから求める。</summary>
    public const string AdSearchBaseKey = "AdSearchBase";
}
