using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using FanslationStudio.Installer.Core;

namespace FanslationStudio.Installer.App;

class InstallerWindow : Window
{
    readonly InstallerConfig _config;
    readonly TextBox _folder = new() { Watermark = "Folder containing the game exe" };
    readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, MinHeight = 160, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly ProgressBar _bar = new() { IsIndeterminate = true, IsVisible = false };
    readonly Button _install = new() { Content = "Install / Update" };
    readonly Button _uninstall = new() { Content = "Uninstall patch" };
    readonly HttpClient _http = new();

    public InstallerWindow(InstallerConfig config)
    {
        _config = config;
        Title = $"{config.GameName} - English patch installer";
        Icon = AppIcon.LoadWindowIcon();
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) => await BrowseAsync();
        _install.Click += async (_, _) => await RunAsync(InstallAsync);
        _uninstall.Click += async (_, _) => await RunAsync(UninstallAsync);

        var folderRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);

        var panel = new StackPanel { Margin = new(16), Spacing = 10 };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new Image { Source = AppIcon.LoadBitmap(), Width = 48, Height = 48 },
                new TextBlock
                {
                    Text = config.GameName, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });
        panel.Children.Add(new TextBlock { Text = "Game folder (the folder with the game exe):" });
        panel.Children.Add(folderRow);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { _install, _uninstall },
        });
        panel.Children.Add(_bar);
        panel.Children.Add(_log);

        if (!OperatingSystem.IsWindows())
            panel.Children.Add(BuildLaunchOptionPanel());

        Content = panel;
        Opened += async (_, _) => await DetectAsync();
    }

    Control BuildLaunchOptionPanel()
    {
        var box = new TextBox { Text = _config.WineLaunchOption, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(_config.WineLaunchOption);
        };

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Linux: paste this into the game's Steam Properties > Launch Options, so BepInEx loads under Proton/Wine:",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                box,
                copy,
            },
        };
    }

    async Task DetectAsync()
    {
        var found = await Task.Run(() => InstallerService.FindInstallRoot(_config));
        if (found != null)
        {
            _folder.Text = found;
            Log($"Found the game at {found}");
        }
        else
        {
            Log("Could not find the game through Steam. Use Browse to pick the folder that contains " + _config.ExeName + ".");
        }
    }

    async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the game folder" });
        if (folders.Count > 0)
            _folder.Text = folders[0].TryGetLocalPath();
    }

    async Task RunAsync(Func<string, Task> action)
    {
        var root = _folder.Text?.Trim() ?? "";
        if (!File.Exists(Path.Combine(root, _config.ExeName)))
        {
            Log($"{_config.ExeName} was not found in '{root}'. Pick the folder that contains the game exe.");
            return;
        }

        SetBusy(true);
        try
        {
            await action(root);
        }
        catch (Exception ex)
        {
            Log($"Failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task InstallAsync(string root)
    {
        var summary = await InstallerService.InstallOrUpdateAsync(_config, root, _http, new Progress<string>(Log));
        if (summary.AlreadyUpToDate)
            return;

        Log(_config.BepInEx.Flavour == BepInExFlavour.Il2Cpp
            ? "Done. Start the game once and let it reach the main menu, so BepInEx can generate its files."
            : "Done. You can start the game.");
    }

    Task UninstallAsync(string root)
    {
        InstallerService.Uninstall(root, new Progress<string>(Log));
        return Task.CompletedTask;
    }

    void SetBusy(bool busy)
    {
        _bar.IsVisible = busy;
        _install.IsEnabled = !busy;
        _uninstall.IsEnabled = !busy;
    }

    void Log(string message)
    {
        _log.Text += (string.IsNullOrEmpty(_log.Text) ? "" : Environment.NewLine) + message;
        _log.CaretIndex = _log.Text.Length;
    }
}
