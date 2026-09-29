using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FanslationStudio.Installer.Core;
using FanslationStudio.LlmKit.Release;

namespace FanslationStudio.LlmKit.Tests;

public class InstallerCoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "installer-core-" + Guid.NewGuid().ToString("N"));

    string Game => Path.Combine(_root, "game");

    string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Builds a real patch zip with ReleasePackager from the given staged files.</summary>
    string BuildPatch(string version, Dictionary<string, string> files, params string[] seedOnly)
    {
        var mappings = files
            .Select(f => new ReleaseMapping(Write(Path.Combine("src-" + version, f.Key), f.Value), f.Key))
            .ToList();

        var result = ReleasePackager.Package(new ReleaseOptions
        {
            Version = version,
            StagingFolder = Path.Combine(_root, "stage-" + version),
            OutputFolder = Path.Combine(_root, "out-" + version),
            ZipPrefix = "Patch",
            Mappings = mappings,
            SeedOnly = [.. seedOnly],
        });
        return result.ZipPath;
    }

    string ReadGame(string relative) => File.ReadAllText(Path.Combine(Game, relative));

    [Fact]
    public void Apply_WritesFilesAndRecordsInstalledManifest()
    {
        var zip = BuildPatch("2026.01.01.00.00", new() { ["BepInEx/layouts/a.yaml"] = "a" });

        var result = PatchApplier.Apply(zip, Game);

        Assert.Equal(1, result.Written);
        Assert.Equal("a", ReadGame("BepInEx/layouts/a.yaml"));
        Assert.Equal("2026.01.01.00.00", PatchApplier.InstalledVersion(Game));
    }

    [Fact]
    public void Apply_SeedOnlyFile_IsWrittenWhenMissingButNeverOverwritten()
    {
        var zip1 = BuildPatch("2026.01.01.00.00", new() { ["BepInEx/config/g.cfg"] = "default" }, "BepInEx/config/g.cfg");
        PatchApplier.Apply(zip1, Game);
        Assert.Equal("default", ReadGame("BepInEx/config/g.cfg"));

        File.WriteAllText(Path.Combine(Game, "BepInEx/config/g.cfg"), "player edit");
        var zip2 = BuildPatch("2026.01.02.00.00", new() { ["BepInEx/config/g.cfg"] = "new default" }, "BepInEx/config/g.cfg");
        var result = PatchApplier.Apply(zip2, Game);

        Assert.Equal(1, result.SeedSkipped);
        Assert.Equal("player edit", ReadGame("BepInEx/config/g.cfg"));
    }

    [Fact]
    public void Apply_RemovesFilesThePreviousReleaseShippedButThisOneDoesNot()
    {
        PatchApplier.Apply(BuildPatch("2026.01.01.00.00", new()
        {
            ["BepInEx/layouts/old.yaml"] = "old",
            ["BepInEx/layouts/keep.yaml"] = "keep",
        }), Game);
        var untracked = Write("game/BepInEx/plugins/user-mod.dll", "not ours");

        var result = PatchApplier.Apply(BuildPatch("2026.01.02.00.00", new() { ["BepInEx/layouts/keep.yaml"] = "keep2" }), Game);

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(Path.Combine(Game, "BepInEx/layouts/old.yaml")));
        Assert.Equal("keep2", ReadGame("BepInEx/layouts/keep.yaml"));
        Assert.True(File.Exists(untracked));
    }

    [Fact]
    public void Apply_CorruptZip_ChangesNothing()
    {
        var zip = BuildPatch("2026.01.01.00.00", new()
        {
            ["a.txt"] = "good",
            ["b.txt"] = "will be corrupted",
        });

        // Rewrite b.txt inside the zip so its hash no longer matches the manifest.
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("b.txt")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("b.txt").Open());
            writer.Write("tampered");
        }

        Write("game/a.txt", "existing");
        Assert.Throws<InvalidDataException>(() => PatchApplier.Apply(zip, Game));

        Assert.Equal("existing", ReadGame("a.txt"));
        Assert.False(File.Exists(Path.Combine(Game, "b.txt")));
        Assert.Null(PatchApplier.InstalledVersion(Game));
    }

    [Fact]
    public void Apply_RejectsPathsThatEscapeTheGameFolder()
    {
        var zipPath = Path.Combine(_root, "evil.zip");
        Directory.CreateDirectory(_root);
        var bytes = Encoding.UTF8.GetBytes("x");
        var manifest = new ReleaseManifest
        {
            Version = "1",
            Files = [new ReleaseManifestFile { Path = "../escaped.txt", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = 1 }],
        };
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(ReleaseManifest.FileName).Open())) w.Write(manifest.ToJson());
            using var s = archive.CreateEntry("../escaped.txt").Open();
            s.Write(bytes);
        }

        Assert.Throws<InvalidDataException>(() => PatchApplier.Apply(zipPath, Game));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void Apply_RejectsNewerManifestSchema()
    {
        var zipPath = Path.Combine(_root, "future.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry(ReleaseManifest.FileName).Open());
            w.Write(new ReleaseManifest { SchemaVersion = 99, Version = "1" }.ToJson());
        }

        var ex = Assert.Throws<InvalidDataException>(() => PatchApplier.Apply(zipPath, Game));
        Assert.Contains("schema", ex.Message);
    }

    [Fact]
    public void Uninstall_RemovesListedFilesAndEmptyFoldersButNotOthers()
    {
        PatchApplier.Apply(BuildPatch("2026.01.01.00.00", new()
        {
            ["BepInEx/layouts/a.yaml"] = "a",
            ["BepInEx/config/g.cfg"] = "cfg",
        }, "BepInEx/config/g.cfg"), Game);
        var other = Write("game/BepInEx/plugins/other.dll", "other");

        var removed = PatchApplier.Uninstall(Game, keepSeedOnly: true);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(Game, "BepInEx/layouts")));
        Assert.True(File.Exists(Path.Combine(Game, "BepInEx/config/g.cfg")));
        Assert.True(File.Exists(other));
        Assert.Null(PatchApplier.InstalledVersion(Game));
    }

    [Fact]
    public async Task UpdateRunner_AppliesAfterGameHasExited_AndKeepsInstallOnFailure()
    {
        var zip = BuildPatch("2026.01.01.00.00", new() { ["a.txt"] = "a" });

        var ok = await UpdateRunner.RunAsync(new UpdateCommand(zip, null, null, null, false), Game);
        Assert.True(ok.Success);
        Assert.False(File.Exists(zip));
        Assert.Equal("a", ReadGame("a.txt"));

        var bad = await UpdateRunner.RunAsync(new UpdateCommand(Path.Combine(_root, "missing.zip"), null, null, null, false), Game);
        Assert.False(bad.Success);
        Assert.Equal("a", ReadGame("a.txt"));
    }

    [Fact]
    public void UpdateCommand_ParsesAndRoundTrips()
    {
        var parsed = UpdateCommand.Parse(["--apply-update", "p.zip", "--pid", "42", "--steam-app-id", "3202030"])!;

        Assert.Equal("p.zip", parsed.ZipPath);
        Assert.Equal(42, parsed.Pid);
        Assert.Equal(3202030, parsed.SteamAppId);
        Assert.Null(parsed.GameDir);
        Assert.Equal(parsed, UpdateCommand.Parse(parsed.ToArgs()));
        Assert.Null(UpdateCommand.Parse(["--something-else"]));
    }

    [Fact]
    public void SteamLocator_ParsesLibraryFoldersAndFindsNestedExe()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"G:\\\\SteamLibrary\"\n\t}\n}";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"G:\SteamLibrary"], SteamLocator.ParseLibraryPaths(vdf));

        Write("lib/steamapps/common/wanxiang/wanxiang/WXQXZ.exe", "exe");
        var game = SteamLocator.FindGameFolder("wanxiang", [Path.Combine(_root, "nope"), Path.Combine(_root, "lib")]);
        Assert.NotNull(game);
        Assert.Equal(Path.Combine(game!, "wanxiang"), SteamLocator.FindInstallRoot(game!, "WXQXZ.exe"));
        Assert.Null(SteamLocator.FindInstallRoot(game!, "Missing.exe"));
    }

    [Fact]
    public void InstallerAssets_UseAStableRollingPreReleaseUrl()
    {
        Assert.Equal("https://github.com/o/r/releases/download/installer/Installer-win-x64.exe", InstallerAssets.DownloadUrl("o/r", true));
        Assert.Equal("https://github.com/o/r/releases/download/installer/Installer-linux-x64", InstallerAssets.DownloadUrl("o/r", false));
        Assert.EndsWith("tag=installer&title=Installer&prerelease=1", InstallerAssets.NewReleaseUrl("o/r"));
        Assert.Equal("https://github.com/o/r/releases/edit/installer", InstallerAssets.EditReleaseUrl("o/r"));
    }

    [Fact]
    public void IsNewer_ComparesDottedVersionsNumerically()
    {
        Assert.True(ReleaseClient.IsNewer(null, "2026.01.01.00.00"));
        Assert.True(ReleaseClient.IsNewer("2026.09.28.16.01", "2026.09.29.18.07"));
        Assert.True(ReleaseClient.IsNewer("2026.9.9.1.1", "2026.09.10.0.0"));
        Assert.False(ReleaseClient.IsNewer("2026.09.29.18.07", "2026.09.29.18.07"));
        Assert.False(ReleaseClient.IsNewer("2026.09.29.18.07", "2026.09.28.16.01"));
    }

    [Fact]
    public async Task ReleaseClient_FindsPatchZipAsset_AndReturnsNullWhenNoReleases()
    {
        var json = """
            {"tag_name":"v2026.09.29.18.07","assets":[
              {"name":"Installer-win-x64.exe","browser_download_url":"https://x/installer.exe"},
              {"name":"EnglishPatch-2026.09.29.18.07.zip","browser_download_url":"https://x/patch.zip"}]}
            """;
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

        var latest = await ReleaseClient.GetLatestAsync(http, "o/r", "EnglishPatch");

        Assert.Equal("2026.09.29.18.07", latest!.Version);
        Assert.Equal("https://x/patch.zip", latest.ZipUrl);

        using var none = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await ReleaseClient.GetLatestAsync(none, "o/r", "EnglishPatch"));
    }

    [Fact]
    public async Task BepInExInstaller_VerifiesHash_ExtractsAndSkipsWhenAlreadyInstalled()
    {
        var bepinexZip = Path.Combine(_root, "bepinex.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(bepinexZip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("BepInEx/core/BepInEx.dll").Open()))
                w.Write("core");
            using (var w2 = new StreamWriter(archive.CreateEntry("winhttp.dll").Open()))
                w2.Write("doorstop");
        }

        string HashOf(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var downloader = new CopyDownloader(bepinexZip);

        InstallerConfig Config(string sha) => new()
        {
            GameName = "g", SteamAppId = 1, SteamFolderName = "f", ExeName = "g.exe", GitHubRepo = "o/r",
            BepInEx = new BepInExPin { Url = "https://example/bepinex.zip", Sha256 = sha },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => BepInExInstaller.InstallAsync(Config("00"), Game, downloader));
        Assert.False(File.Exists(Path.Combine(Game, "winhttp.dll")));

        Assert.True(await BepInExInstaller.InstallAsync(Config(HashOf(bepinexZip)), Game, downloader));
        Assert.Equal("core", ReadGame("BepInEx/core/BepInEx.dll"));
        Assert.Equal("doorstop", ReadGame("winhttp.dll"));

        Assert.False(await BepInExInstaller.InstallAsync(Config(HashOf(bepinexZip)), Game, downloader));
        Assert.Equal(2, downloader.Calls);
    }

    [Fact]
    public async Task InstallerService_InstallsBepInExAndLatestPatch_ThenReportsUpToDate()
    {
        var bepinexZip = Path.Combine(_root, "bepinex.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(bepinexZip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("winhttp.dll").Open()))
            w.Write("doorstop");
        var patchZip = BuildPatch("2026.01.01.00.00", new() { ["BepInEx/layouts/a.yaml"] = "a" });

        var api = """{"tag_name":"2026.01.01.00.00","assets":[{"name":"Patch-2026.01.01.00.00.zip","browser_download_url":"https://dl/patch.zip"}]}""";
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            var u when u.Contains("api.github.com") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(api) },
            "https://dl/patch.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(patchZip)) },
            "https://dl/bepinex.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(bepinexZip)) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var config = new InstallerConfig
        {
            GameName = "g", SteamAppId = 1, SteamFolderName = "f", ExeName = "g.exe", GitHubRepo = "o/r", PatchZipPrefix = "Patch",
            BepInEx = new BepInExPin
            {
                Url = "https://dl/bepinex.zip",
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(bepinexZip))),
            },
        };
        var messages = new List<string>();
        var log = new SyncProgress(messages);

        var first = await InstallerService.InstallOrUpdateAsync(config, Game, http, log);
        Assert.True(first.BepInExInstalled);
        Assert.False(first.AlreadyUpToDate);
        Assert.Equal("a", ReadGame("BepInEx/layouts/a.yaml"));
        Assert.Equal("doorstop", ReadGame("winhttp.dll"));

        var second = await InstallerService.InstallOrUpdateAsync(config, Game, http, log);
        Assert.False(second.BepInExInstalled);
        Assert.True(second.AlreadyUpToDate);
        Assert.Contains(messages, m => m.Contains("already up to date"));
    }

    [Fact]
    public void InstallerService_LoadConfig_PrefersFileNextToExeOverEmbedded()
    {
        var folder = Path.Combine(_root, "exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "installer.json"),
            """{"gameName":"override","steamAppId":1,"steamFolderName":"f","exeName":"g.exe","gitHubRepo":"o/r"}""");

        Assert.Equal("override", InstallerService.LoadConfig(typeof(InstallerCoreTests).Assembly, folder).GameName);
        Assert.Throws<FileNotFoundException>(() => InstallerService.LoadConfig(typeof(InstallerCoreTests).Assembly, _root));
    }

    [Fact]
    public void InstallerConfig_ParsesCommentedJson_AndRejectsMissingIdentity()
    {
        var config = InstallerConfig.FromJson("""
            {
              // comment
              "gameName": "Legend of Dragon Heir", "steamAppId": 3202030, "steamFolderName": "LongYinLiZhiZhuan",
              "exeName": "LongYinLiZhiZhuan.exe", "gitHubRepo": "joshfreitas1984/DragonHierOverLlm",
              "bepInEx": { "flavour": "il2Cpp", "architecture": "x64", "version": "6.0.0-be.785" }
            }
            """);

        Assert.Equal(BepInExFlavour.Il2Cpp, config.BepInEx.Flavour);
        Assert.Equal("EnglishPatch", config.PatchZipPrefix);
        Assert.Null(config.GhAccount);
        Assert.Throws<InvalidDataException>(config.ValidateBepInExPin);
        Assert.Throws<InvalidDataException>(() => InstallerConfig.FromJson("""{ "gameName": "x" }"""));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    // Progress<T> posts asynchronously; tests need messages recorded immediately.
    class SyncProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }

    class CopyDownloader(string source) : IFileDownloader
    {
        public int Calls { get; private set; }

        public Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            Calls++;
            File.Copy(source, destinationPath, true);
            return Task.CompletedTask;
        }
    }
}
