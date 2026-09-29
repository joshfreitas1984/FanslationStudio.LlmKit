using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using FanslationStudio.Installer.Core;

namespace FanslationStudio.Installer.App;

/// <summary>
/// Entry point for a per-game installer host: <c>return InstallerHost.Run(args);</c>.
/// With <c>--apply-update</c> it shows a small progress window and applies the update; otherwise the installer window.
/// The host assembly embeds installer.json; a copy next to the exe overrides it.
/// </summary>
public static class InstallerHost
{
    internal static InstallerConfig Config = null!;
    internal static UpdateCommand? Update;
    internal static string GameDir = "";

    public static int Run(string[] args)
    {
        var hostAssembly = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("No entry assembly.");
        var exeFolder = AppContext.BaseDirectory;
        Config = InstallerService.LoadConfig(hostAssembly, exeFolder);
        Update = UpdateCommand.Parse(args);

        if (Update != null)
        {
            // The updater ships inside <game>/BepInEx/updater, so the game folder is two levels up unless told.
            GameDir = Update.GameDir ?? Path.GetFullPath(Path.Combine(exeFolder, "..", ".."));

            // Windows can't overwrite a running exe and the patch replaces this one: run from a temp copy.
            if (UpdateRunner.RelocateIfNeeded(Update, GameDir))
                return 0;
        }

        return AppBuilder.Configure<InstallerApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

class InstallerApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = InstallerHost.Update != null
                ? new UpdateWindow(InstallerHost.Config, InstallerHost.Update, InstallerHost.GameDir)
                : new InstallerWindow(InstallerHost.Config);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
