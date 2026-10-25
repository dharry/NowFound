namespace NowFound.Web.Security;

/// <summary>アカウントの有効期限の判定に使う「今日」。サーバーのローカル日付（有効期限はその日の終わりまで有効）。</summary>
public static class AccountClock
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
