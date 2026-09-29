using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace FanslationStudio.Installer.App;

static class AppIcon
{
    const string ResourceName = "FanslationStudio.png";

    static Stream Open() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
        ?? throw new FileNotFoundException($"Embedded icon {ResourceName} is missing.");

    public static WindowIcon LoadWindowIcon()
    {
        using var stream = Open();
        return new WindowIcon(stream);
    }

    public static Bitmap LoadBitmap()
    {
        using var stream = Open();
        return new Bitmap(stream);
    }
}
