namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Retry-wrapped file writes. Every workflow in this project periodically overwrites the same
/// output `.yaml` file from a background worker while it's still open elsewhere (an IDE holding it
/// open for indexing, an antivirus scan, OneDrive sync, `git status`), which throws a transient
/// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> that almost always clears up
/// on its own within a few hundred milliseconds. Without a retry here, that transient lock takes
/// down an entire multi-hour translation/QC run. Only those two exception types are retried - any
/// other exception (e.g. a genuinely bad path, disk full) is rethrown immediately since retrying
/// wouldn't help.
/// </summary>
public static class FileHelper
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)];

    public static void WriteAllTextWithRetry(string path, string contents) =>
        WithRetry(path, () => File.WriteAllText(path, contents));

    public static Task WriteAllTextWithRetryAsync(string path, string contents) =>
        WithRetryAsync(path, () => File.WriteAllTextAsync(path, contents));

    public static void WriteAllLinesWithRetry(string path, IEnumerable<string> contents) =>
        WithRetry(path, () => File.WriteAllLines(path, contents));

    public static Task WriteAllLinesWithRetryAsync(string path, IEnumerable<string> contents) =>
        WithRetryAsync(path, () => File.WriteAllLinesAsync(path, contents));

    private static void WithRetry(string path, Action write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < MaxAttempts)
            {
                Console.WriteLine($"File write to '{path}' failed (attempt {attempt}/{MaxAttempts}: {e.Message}) - retrying.");
                Thread.Sleep(RetryDelays[attempt - 1]);
            }
        }
    }

    private static async Task WithRetryAsync(string path, Func<Task> write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < MaxAttempts)
            {
                Console.WriteLine($"File write to '{path}' failed (attempt {attempt}/{MaxAttempts}: {e.Message}) - retrying.");
                await Task.Delay(RetryDelays[attempt - 1]);
            }
        }
    }
}
