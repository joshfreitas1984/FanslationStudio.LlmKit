using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FanslationStudio.LlmKit.Workflow;

public class SymlinkWorkflow
{
    /// <summary>
    /// Used to creat symlinks to your game files to avoid having to copy them into the working directory. 
    /// This allows you to check in files like resizers and autotranslator outputs without having to copy them
    /// back and forth. 
    /// </summary>
    /// <remarks>
    /// Must run IDE as administrator if you are running through a test. Only supported for Windows.
    /// </remarks>
    public static void CreateSymlink(string source, string destination)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            CreateSymlinkWindows(source, destination);
        }
        else
        {
            throw new NotSupportedException("Symlink creation is only supported on Windows in this workflow.");
        }
    }

    private static void CreateSymlinkWindows(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            Console.WriteLine("Output folder already exists. Deleting it...");
            Directory.Delete(destination, true);
        }

        // Run mklink command to create a symbolic link
        string command = $"/C mklink /D \"{destination}\" \"{source}\"";
        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Verb = "runas" // Run as administrator
        };

        Process process = new Process { StartInfo = psi };
        process.Start();

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // Display output or error
        if (!string.IsNullOrEmpty(output))
            Console.WriteLine("Success: " + output);
        if (!string.IsNullOrEmpty(error))
            throw new Exception("Error: " + error);
    }
}

