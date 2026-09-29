using System.IO.Compression;
using FanslationStudio.LlmKit.Release;

namespace FanslationStudio.LlmKit.Tests;

public class ReleasePackagerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "release-packager-" + Guid.NewGuid().ToString("N"));

    string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    ReleaseOptions Options(Action<ReleaseOptions>? configure = null)
    {
        var options = new ReleaseOptions
        {
            Version = "2026.01.02.03.04",
            StagingFolder = Path.Combine(_root, "out", "Files"),
            OutputFolder = Path.Combine(_root, "out"),
            ZipPrefix = "Patch",
            Mappings = [new ReleaseMapping(Path.Combine(_root, "src", "Layouts"), "BepInEx/layouts")],
            OwnedFolders = ["BepInEx/layouts"],
        };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void Package_ClearsOnlyOwnedFolders_AndKeepsPostBuildFiles()
    {
        Write("src/Layouts/new.yaml", "new");
        Write("out/Files/BepInEx/layouts/stale.yaml", "stale");
        Write("out/Files/BepInEx/plugins/Plugin.dll", "dll");

        var result = ReleasePackager.Package(Options());

        var paths = result.Manifest.Files.Select(f => f.Path).ToList();
        Assert.Contains("BepInEx/layouts/new.yaml", paths);
        Assert.Contains("BepInEx/plugins/Plugin.dll", paths);
        Assert.DoesNotContain("BepInEx/layouts/stale.yaml", paths);
    }

    [Fact]
    public void Package_WritesManifestWithHashesSeedOnlyAndZip()
    {
        Write("src/Layouts/a.yaml", "hello");
        Write("out/Files/BepInEx/config/game.cfg", "cfg");

        var result = ReleasePackager.Package(Options(o => o.SeedOnly.Add("BepInEx/config/**")));

        var manifest = ReleaseManifest.FromJson(File.ReadAllText(result.ManifestPath));
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("2026.01.02.03.04", manifest.Version);
        Assert.Equal(["BepInEx/config/game.cfg"], manifest.SeedOnly);

        var layout = manifest.Files.Single(f => f.Path == "BepInEx/layouts/a.yaml");
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", layout.Sha256);
        Assert.DoesNotContain(manifest.Files, f => f.Path == ReleaseManifest.FileName);

        Assert.Equal(Path.Combine(_root, "out", "Patch-2026.01.02.03.04.zip"), result.ZipPath);
        using var zip = ZipFile.OpenRead(result.ZipPath);
        Assert.Contains(zip.Entries, e => e.FullName == ReleaseManifest.FileName);
        Assert.Contains(zip.Entries, e => e.FullName == "BepInEx/layouts/a.yaml");
    }

    [Fact]
    public void Package_RemovesConfiguredFiles()
    {
        Write("src/Layouts/a.yaml", "a");
        Write("src/Layouts/zzAdded.yaml", "z");

        var result = ReleasePackager.Package(Options(o => o.RemoveAfterStaging.Add("BepInEx/layouts/zzAdded.yaml")));

        Assert.DoesNotContain(result.Manifest.Files, f => f.Path.EndsWith("zzAdded.yaml"));
    }

    [Fact]
    public void Package_RejectsPathsOutsideStaging_BeforeDeletingAnything()
    {
        Write("src/Layouts/a.yaml", "a");
        var keep = Write("out/Files/BepInEx/layouts/keep.yaml", "keep");

        Assert.Throws<ArgumentException>(() =>
            ReleasePackager.Package(Options(o => o.OwnedFolders.Add("../.."))));

        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void Package_FailsWhenSeedOnlyPatternMatchesNothing()
    {
        Write("src/Layouts/a.yaml", "a");

        Assert.Throws<InvalidOperationException>(() =>
            ReleasePackager.Package(Options(o => o.SeedOnly.Add("BepInEx/config/**"))));
    }

    [Fact]
    public void BuildReleaseUrl_PrefillsTagAndTitle()
    {
        Assert.Equal(
            "https://github.com/o/r/releases/new?tag=2026.01.02.03.04&title=2026.01.02.03.04",
            ReleasePublisher.BuildReleaseUrl("o/r", "2026.01.02.03.04"));
    }

    [Fact]
    public void Package_WritesReleaseNotesBesideZip_AndPrefillsUrlBody()
    {
        Write("src/Layouts/a.yaml", "a");

        var result = ReleasePackager.Package(WithNotes());

        Assert.Equal(Path.Combine(_root, "out", "release-notes.md"), result.NotesPath);
        Assert.Equal("**Game version: 1.2**", File.ReadAllText(result.NotesPath!));
        Assert.EndsWith("&body=%2A%2AGame%20version%3A%201.2%2A%2A",
            ReleasePublisher.BuildReleaseUrl("o/r", "v", "**Game version: 1.2**"));
    }

    [Fact]
    public void GitReleaseNotes_ListsCommitsSinceLastTag()
    {
        Directory.CreateDirectory(_root);
        void Git(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        Git("init");
        Write("a.txt", "1");
        Git("add", ".");
        Git("commit", "-m", "old change");
        Git("tag", "2026.01.01.00.00");
        Write("a.txt", "2");
        Git("commit", "-am", "new change");

        var notes = GitReleaseNotes.Generate(_root)!;

        Assert.StartsWith("## Changes", notes);
        Assert.DoesNotContain("2026.01.01.00.00", notes);
        Assert.Contains("- new change (", notes);
        Assert.DoesNotContain("old change", notes);
    }

    ReleaseOptions WithNotes() => new()
    {
        Version = "v",
        StagingFolder = Path.Combine(_root, "out", "Files"),
        OutputFolder = Path.Combine(_root, "out"),
        ZipPrefix = "Patch",
        Mappings = [new ReleaseMapping(Path.Combine(_root, "src", "Layouts"), "BepInEx/layouts")],
        ReleaseNotes = "**Game version: 1.2**",
    };

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;

        // git marks object files read-only, which blocks Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
