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

        // Directory symlink at destination pointing to source (the equivalent of mklink /D);
        // source is stored as given, so a relative source stays relative to the link.
        try
        {
            Directory.CreateSymbolicLink(destination, source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Exception("Error: " + e.Message, e);
        }

        Console.WriteLine($"Success: symbolic link created for {destination} <<===>> {source}");
    }
}

