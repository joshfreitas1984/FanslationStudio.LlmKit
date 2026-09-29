using FanslationStudio.LlmKit.Utility;

namespace Tests;

public class EditorFileSplitterTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"splitter-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact(DisplayName = "Entries move into per-prefix files and the added file is emptied")]
    public void Split_MovesEntriesIntoPrefixFiles()
    {
        var folder = Folder("Resizers");
        File.WriteAllText(Path.Combine(folder, "zzAddedResizers.yaml"),
            "- path: Canvas/MainMenu/Title\n  idealFontSize: 20\n- path: Canvas/MainMenu/Start\n  idealFontSize: 18\n- path: Loose\n  idealFontSize: 10\n");

        EditorFileSplitter.SplitResizers(_root);

        var menu = File.ReadAllText(Path.Combine(folder, "Canvas_MainMenu.yaml"));
        Assert.Contains("Canvas/MainMenu/Title", menu);
        Assert.Contains("Canvas/MainMenu/Start", menu);
        Assert.True(menu.IndexOf("Canvas/MainMenu/Start", StringComparison.Ordinal) < menu.IndexOf("Canvas/MainMenu/Title", StringComparison.Ordinal));
        Assert.Contains("Loose", File.ReadAllText(Path.Combine(folder, "Loose.yaml")));
        Assert.DoesNotContain("path", File.ReadAllText(Path.Combine(folder, "zzAddedResizers.yaml")));
    }

    [Fact(DisplayName = "Entries merge into an existing prefix file and stay sorted")]
    public void Split_MergesIntoExistingFile()
    {
        var folder = Folder("Layouts");
        File.WriteAllText(Path.Combine(folder, "Canvas_Hud.yaml"), "- path: Canvas/Hud/B\n  x: 1\n");
        File.WriteAllText(Path.Combine(folder, "zzAddedLayouts.yaml"), "- path: Canvas/Hud/A\n  x: 2\n");

        EditorFileSplitter.SplitLayouts(_root);

        var merged = File.ReadAllText(Path.Combine(folder, "Canvas_Hud.yaml"));
        Assert.True(merged.IndexOf("Canvas/Hud/A", StringComparison.Ordinal) < merged.IndexOf("Canvas/Hud/B", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "A project without the folder is ignored")]
    public void Split_MissingFolder_DoesNothing()
    {
        EditorFileSplitter.SplitSprites(_root);

        Assert.False(Directory.Exists(Path.Combine(_root, "Sprites")));
    }

    [Fact(DisplayName = "Wildcards in the first two path segments become legal file names")]
    public void Split_WildcardPaths_UseSafeFileNames()
    {
        var folder = Folder("Resizers");
        File.WriteAllText(Path.Combine(folder, "zzAddedResizers.yaml"),
            "- path: MainMap/*/Title\n  idealFontSize: 20\n- path: /*\n  idealFontSize: 10\n");

        EditorFileSplitter.SplitResizers(_root);

        Assert.True(File.Exists(Path.Combine(folder, "MainMap__.yaml")));
        Assert.True(File.Exists(Path.Combine(folder, "zzzGlobalResizer.yaml")));
        Assert.All(Directory.GetFiles(folder), f => Assert.DoesNotContain('*', Path.GetFileName(f)));
    }
}
