namespace NowFound.Application;

/// <summary>画面から入力された日付（開始日・終了日）を、指定されたタイムゾーンに基づいて UTC の日時に変換する。</summary>
public static class DateTimeRange
{
    /// <summary>
    /// 日付範囲を UTC の (From, To) に変換する。
    /// to は指定された日の翌日 00:00:00（未満判定用）とする。
    /// timeZone が未指定の場合は TimeZoneInfo.Local を使用する。
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) ToUtc(DateTime? from, DateTime? to, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        DateTime? fromUtc = from.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Unspecified), tz)
            : null;
        DateTime? toUtc = to.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(to.Value.Date.AddDays(1), DateTimeKind.Unspecified), tz)
            : null;
        return (fromUtc, toUtc);
    }
}
