using System.Diagnostics;

namespace FanslationStudio.LlmKit.Release;

/// <summary>Runs `dotnet publish` for a runtime and copies the single-file result under a release asset name.</summary>
public static class DotnetPublisher
{
    /// <summary>
    /// Publishes <paramref name="projectPath"/> for <paramref name="runtimeId"/> (e.g. win-x64) and copies the exe named
    /// <paramref name="exeBaseName"/> to <paramref name="outputFolder"/>/<paramref name="outputName"/>. Returns the copied path.
    /// </summary>
    public static string PublishSingleFile(string projectPath, string runtimeId, string outputFolder, string exeBaseName, string outputName)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"publish-{Guid.NewGuid():N}");
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "publish", Path.GetFullPath(projectPath), "-c", "Release", "-r", runtimeId, "-o", temp })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"dotnet publish ({runtimeId}) failed:{Environment.NewLine}{stdout}{stderr.Result}");

            var exe = runtimeId.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? exeBaseName + ".exe" : exeBaseName;
            var published = Path.Combine(temp, exe);
            if (!File.Exists(published))
                throw new FileNotFoundException($"dotnet publish did not produce {exe}", published);

            Directory.CreateDirectory(outputFolder);
            var target = Path.Combine(Path.GetFullPath(outputFolder), outputName);
            File.Copy(published, target, true);
            return target;
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }
}
