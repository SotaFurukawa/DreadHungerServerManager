using System.Diagnostics;
using System.Runtime.InteropServices;
using DreadHungerServerManager.App.ViewModels;
using DreadHungerServerManager.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace DreadHungerServerManager.App.Services;

/// <summary><see cref="IUserInteraction"/> のWinUI実装（ContentDialog・トースト・ファイル選択・クリップボード）。</summary>
internal sealed class UserInteractionService : IUserInteraction
{
    // ContentDialogは同時に1つしか開けないため、順番に表示する。
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private MainWindow? _window;

    public void Attach(MainWindow window) => _window = window;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "キャンセル")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = CreateMessage(message),
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "キャンセル")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = CreateMessage(message),
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => DialogChoice.Primary,
            ContentDialogResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.Cancel,
        };
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 24,
            Foreground = ViewHelpers.Brush("SystemFillColorCriticalBrush"),
            VerticalAlignment = VerticalAlignment.Top,
        });
        var text = CreateMessage(message);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        await ShowAsync(new ContentDialog { Title = title, Content = content, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close });
    }

    public async Task<string?> PromptTextAsync(string title, string message, string initialText, Func<string, string?> validate)
    {
        var input = new TextBox { Text = initialText, PlaceholderText = "例: 身内用 8人", MaxLength = 100 };
        var error = new TextBlock
        {
            Foreground = ViewHelpers.Brush("SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel { Spacing = 8, Children = { CreateMessage(message), input, error } },
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var problem = validate(input.Text);
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    public async Task<string?> PickFileAsync(string? currentPath, IReadOnlyList<string> extensions)
    {
        if (_window is null)
        {
            return null;
        }

        // Windows App SDKのピッカーは、未パッケージ・管理者権限（Windows Serverの既定）でも使える。
        var picker = new FileOpenPicker(_window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var folder = TryGetExistingDirectory(currentPath);
        if (folder is not null)
        {
            picker.SuggestedFolder = folder;
        }

        try
        {
            var result = await picker.PickSingleFileAsync();
            return result?.Path;
        }
        catch (COMException ex)
        {
            await ShowErrorAsync("ファイルを選択できませんでした", ex.Message);
            return null;
        }
    }

    public void ShowToast(string message, NoticeSeverity severity = NoticeSeverity.Success) =>
        _window?.DispatcherQueue.TryEnqueue(() => _window.ShowToast(message, severity));

    public bool CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (COMException ex)
        {
            ShowToast($"クリップボードにコピーできませんでした: {ex.Message}", NoticeSeverity.Error);
            return false;
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowToast($"フォルダーを開けませんでした: {ex.Message}", NoticeSeverity.Error);
        }
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_window?.Content is not FrameworkElement root)
        {
            return ContentDialogResult.None;
        }

        await _dialogGate.WaitAsync();
        try
        {
            dialog.XamlRoot = root.XamlRoot;
            dialog.RequestedTheme = root.ActualTheme;
            dialog.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static TextBlock CreateMessage(string message) => new() { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private static string? TryGetExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(path.Trim());
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
