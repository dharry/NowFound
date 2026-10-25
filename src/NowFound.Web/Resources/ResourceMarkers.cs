namespace NowFound.Web.Resources;

// IStringLocalizer<T> の T に使う目印のクラス。同じフォルダの <名前>.resx（日本語＝既定）と <名前>.en.resx（英語）が対応する。
// キーは「領域.意味」の形（例: Logs.Title）にする。画面の領域ごとにファイルを分けている。

/// <summary>レイアウト（ナビ・ヘッダー）、共通のボタン・ラベル、ページ移動。</summary>
public sealed class CommonResource;

/// <summary>JavaScript（site.js）が使う文言。レイアウトが現在の言語の全キーを JSON で埋め込み、site.js の t('キー') で引く。</summary>
public sealed class JsResource;

/// <summary>ログイン・パスワード変更・ユーザー設定。</summary>
public sealed class AccountResource;

/// <summary>ファイル検索の画面と検索条件のラベル。</summary>
public sealed class SearchResource;

/// <summary>保存した検索。</summary>
public sealed class SavedSearchesResource;

/// <summary>ユーザー管理。</summary>
public sealed class UsersResource;

/// <summary>グループ管理。</summary>
public sealed class GroupsResource;

/// <summary>検索対象フォルダ。</summary>
public sealed class FoldersResource;

/// <summary>操作ログ。</summary>
public sealed class LogsResource;

/// <summary>システム設定（ページサイズ、既定の除外パターン、ファイルの種類）。</summary>
public sealed class SettingsResource;

/// <summary>稼働ホスト・.NET・データベースの情報画面。</summary>
public sealed class InformationResource;

/// <summary>サービス層・コントローラーが返すメッセージ（エラー、操作ログの内容、ID から引く表示名など）。</summary>
public sealed class MessagesResource;
