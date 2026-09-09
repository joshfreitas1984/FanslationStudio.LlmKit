using FanslationStudio.LlmKit.Utility;

namespace Tests;

/// <summary>
/// Covers <see cref="FileHelper"/>'s retry behavior for the transient "file locked by another
/// process" failure (an IDE/antivirus/sync tool briefly holding the output `.yaml` open) that
/// otherwise takes down an entire multi-hour translation/QC run - see
/// docs/plans/quality-review-pass.md (DragonHierOverLlm repo) for the background.
/// </summary>
public class FileHelperTests
{
    [Fact(DisplayName = "Write succeeds immediately when the file isn't locked")]
    public async Task WriteAllTextWithRetryAsync_NoLock_WritesImmediately()
    {
        var path = Path.GetTempFileName();
        try
        {
            await FileHelper.WriteAllTextWithRetryAsync(path, "hello");

            Assert.Equal("hello", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Write retries past a transient lock and succeeds once it clears")]
    public async Task WriteAllTextWithRetryAsync_TransientLock_RetriesAndSucceeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var writeTask = FileHelper.WriteAllTextWithRetryAsync(path, "hello");

            // Release the lock partway through the retry window - the write should still succeed
            // via retry rather than throwing on the first attempt.
            await Task.Delay(150);
            lockHandle.Dispose();

            await writeTask;
            Assert.Equal("hello", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Write throws after exhausting retries against a lock that never clears")]
    public async Task WriteAllTextWithRetryAsync_PersistentLock_ThrowsAfterRetries()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            await Assert.ThrowsAsync<IOException>(() => FileHelper.WriteAllTextWithRetryAsync(path, "hello"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
