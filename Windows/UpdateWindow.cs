using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Threading;

namespace FlankNote;

class UpdateWindow : Window
{
    readonly TextBlock _statusText = new();
    readonly TextBlock _releaseNotes = new();
    readonly Border _action = new();
    GitHubRelease? _release;
    bool _isDownloading;
    CancellationTokenSource? _downloadCancellation;
    public UpdateWindow()
    {
        Title = Loc.T("Check for Updates", "检查更新");
        Width = 480;
        Height = 390;
        MinWidth = 440;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = UiTheme.Window;
        Foreground = UiTheme.Text;
        FontFamily = UiTheme.Font;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var root = new Grid { Margin = new Thickness(28, 24, 28, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = Loc.T("Check for Updates", "检查更新"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        });
        heading.Children.Add(new TextBlock
        {
            Text = Loc.T("update service", "更新服务"),
            FontSize = 12,
            Foreground = UiTheme.Muted,
            Margin = new Thickness(0, 3, 0, 20),
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        var status = new Border
        {
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(16, 13, 16, 13),
            Child = StatusContent(version),
        };
        Grid.SetRow(status, 1);
        root.Children.Add(status);

        var notes = new StackPanel { Margin = new Thickness(2, 20, 2, 0) };
        notes.Children.Add(new TextBlock
        {
            Text = Loc.T("RELEASE NOTES", "更新内容"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.Muted,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _releaseNotes.Text = Loc.T("Checking GitHub Releases…", "正在检查 GitHub Releases…");
        _releaseNotes.FontSize = 13;
        _releaseNotes.Foreground = UiTheme.Muted;
        _releaseNotes.TextWrapping = TextWrapping.Wrap;
        notes.Children.Add(_releaseNotes);
        Grid.SetRow(notes, 2);
        root.Children.Add(notes);

        _action = new Border
        {
            Background = UiTheme.Accent,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = Loc.T("Close", "关闭"),
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
            },
        };
        _action.MouseLeftButtonUp += OnAction;
        Grid.SetRow(_action, 3);
        root.Children.Add(_action);

        Content = UiTheme.WithWindowChrome(this, Title, root);
        Closed += (_, _) =>
        {
            _downloadCancellation?.Cancel();
            App.UpdateWin = null;
        };
        DisplayService.CenterOnSelected(this);
        Show();
        Activate();
        Dispatcher.BeginInvoke(LoadRelease, DispatcherPriority.Background);
    }

    UIElement StatusContent(string version)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock
        {
            Text = Loc.T("Current version", "当前版本"),
            FontSize = 12,
            Foreground = UiTheme.Muted,
        });
        _statusText.Text = Loc.T("Checking GitHub Releases...", "正在检查 GitHub Releases...");
        _statusText.FontSize = 14;
        _statusText.FontWeight = FontWeights.SemiBold;
        _statusText.Margin = new Thickness(0, 2, 0, 0);
        _statusText.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(_statusText);
        grid.Children.Add(labels);

        var value = new TextBlock
        {
            Text = $"v{version}",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = UiTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    async void LoadRelease()
    {
        try
        {
            _release = await App.GetLatestReleaseAsync(forceRefresh: true);
            if (_release == null)
            {
                _statusText.Text = Loc.T("No published release found", "没有找到已发布版本");
                _releaseNotes.Text = Loc.T("No published release was found.", "没有找到已发布的版本。");
                SetAction(Loc.T("Close", "关闭"), enabled: true);
                return;
            }
            bool newer = GitHubUpdateService.IsNewer(_release.TagName);
            _releaseNotes.Text = string.IsNullOrWhiteSpace(_release.Body)
                ? Loc.T($"Latest release: {_release.TagName}", $"最新版本：{_release.TagName}")
                : _release.Body.Trim();
            if (newer)
            {
                SetAction(Loc.T("Download and install", "下载并安装"), enabled: true);
                _statusText.Text = Loc.T($"New version available: {_release.TagName}", $"发现新版本：{_release.TagName}");
            }
            else
            {
                SetAction(Loc.T("Close", "关闭"), enabled: true);
                _statusText.Text = Loc.T("You are up to date", "当前已是最新版本");
            }
        }
        catch (Exception ex)
        {
            App.ReportError($"Update check failed: {ex}");
            _statusText.Text = Loc.T("Update check failed", "更新检查失败");
            _releaseNotes.Text = Loc.T("Could not check GitHub Releases. Check your network connection.", "无法检查 GitHub Releases，请检查网络连接。");
            SetAction(Loc.T("Close", "关闭"), enabled: true);
        }
    }

    void SetAction(string text, bool enabled)
    {
        if (_action.Child is TextBlock label) label.Text = text;
        _action.IsHitTestVisible = enabled;
        _action.Opacity = enabled ? 1 : 0.55;
    }

    async void OnAction(object sender, MouseButtonEventArgs e)
    {
        if (_isDownloading) return;
        if (_release != null && GitHubUpdateService.IsNewer(_release.TagName))
        {
            try
            {
                _isDownloading = true;
                using var cancellation = new CancellationTokenSource();
                _downloadCancellation = cancellation;
                _statusText.Text = Loc.T($"Downloading {_release.TagName}…", $"正在下载 {_release.TagName}…");
                SetAction(Loc.T("Downloading 0%", "正在下载 0%"), enabled: false);
                var progress = new Progress<double>(value =>
                    SetAction(
                        Loc.T($"Downloading {value:P0}", $"正在下载 {value:P0}"),
                        enabled: false));
                var installer = await GitHubUpdateService.DownloadInstallerAsync(
                    _release, progress, cancellation.Token);
                _statusText.Text = Loc.T("Opening the installer…", "正在打开安装程序…");
                SetAction(Loc.T("Opening installer…", "正在打开安装程序…"), enabled: false);
                Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                System.Windows.Application.Current.Shutdown();
            }
            catch (OperationCanceledException) when (_downloadCancellation?.IsCancellationRequested == true)
            {
                return;
            }
            catch (Exception ex)
            {
                _isDownloading = false;
                App.ReportError($"Update download failed: {ex}");
                if (ex is IOException or UnauthorizedAccessException)
                {
                    _statusText.Text = Loc.T("Could not save the installer", "无法保存安装程序");
                    _releaseNotes.Text = Loc.T(
                        "The download completed, but Windows could not save the installer. Close any installer already open and try again.",
                        "下载已完成，但 Windows 无法保存安装程序。请关闭已打开的安装程序后重试。");
                }
                else
                {
                    _statusText.Text = Loc.T("Update download failed", "更新下载失败");
                    _releaseNotes.Text = Loc.T(
                        "The installer could not be downloaded. Check your network connection and try again.",
                        "无法下载安装程序，请检查网络连接后重试。");
                }
                SetAction(Loc.T("Try again", "重试"), enabled: true);
            }
            finally
            {
                _downloadCancellation = null;
            }
            return;
        }
        Close();
    }
}
