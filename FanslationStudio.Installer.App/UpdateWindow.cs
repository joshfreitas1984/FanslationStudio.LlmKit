using Avalonia.Controls;
using Avalonia.Layout;
using FanslationStudio.Installer.Core;

namespace FanslationStudio.Installer.App;

/// <summary>Small progress window for the headless-style --apply-update flow launched by the game.</summary>
class UpdateWindow : Window
{
    public UpdateWindow(InstallerConfig config, UpdateCommand command, string gameDir)
    {
        Title = $"{config.GameName} - updating";
        Icon = AppIcon.LoadWindowIcon();
        Width = 420;
        Height = 150;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var status = new TextBlock { Text = "Waiting for the game to close...", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = true };
        var close = new Button { Content = "Close", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel { Margin = new(16), Spacing = 12, Children = { status, bar, close } };

        Opened += async (_, _) =>
        {
            var outcome = await UpdateRunner.RunAsync(command, gameDir);
            bar.IsIndeterminate = false;
            bar.Value = outcome.Success ? 100 : 0;

            status.Text = outcome.Success && !outcome.Relaunched
                ? $"{outcome.Message} Update complete, please start the game."
                : outcome.Message;

            if (outcome.Success && outcome.Relaunched)
                Close();
            else
                close.IsVisible = true;
        };
    }
}
