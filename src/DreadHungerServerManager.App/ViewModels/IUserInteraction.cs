namespace DreadHungerServerManager.App.ViewModels;

public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel,
}

public enum NoticeSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// ViewModelからダイアログ・通知・ファイル選択などを行うための窓口。
/// ViewModelがWinUIの型に直接依存しないよう、実装はViews側（UserInteractionService）に置く。
/// </summary>
public interface IUserInteraction
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "キャンセル");

    Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "キャンセル");

    Task ShowErrorAsync(string title, string message);

    /// <param name="validate">入力値が正しければnull、誤りなら表示するエラー文を返す。</param>
    Task<string?> PromptTextAsync(string title, string message, string initialText, Func<string, string?> validate);

    Task<string?> PickFileAsync(string? currentPath, IReadOnlyList<string> extensions);

    void ShowToast(string message, NoticeSeverity severity = NoticeSeverity.Success);

    bool CopyToClipboard(string text);

    void OpenFolder(string path);
}
