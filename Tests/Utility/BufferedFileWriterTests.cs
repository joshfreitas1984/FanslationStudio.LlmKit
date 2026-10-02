using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Tests.Utility;

public class BufferedFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bfw-{Guid.NewGuid():N}");

    public BufferedFileWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private string FilePath => Path.Combine(_dir, "out.yaml");

    [Fact]
    public void ItemDone_OnLastItem_WithoutChanges_DoesNotWrite()
    {
        var writer = new BufferedFileWriter(FilePath, () => "content", bufferThreshold: 2);
        writer.SetPendingItems(2);

        writer.ItemDone();
        writer.ItemDone();

        Assert.False(File.Exists(FilePath));
        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public void ItemDone_OnLastItem_AfterChange_WritesOnce()
    {
        var writer = new BufferedFileWriter(FilePath, () => "content", bufferThreshold: 100);
        writer.SetPendingItems(2);

        writer.RecordChange();
        writer.ItemDone();
        Assert.False(File.Exists(FilePath));

        writer.ItemDone();
        Assert.Equal("content", File.ReadAllText(FilePath));
        Assert.Equal(1, writer.WriteCount);
        Assert.False(writer.IsDirty);
    }

    [Fact]
    public void RecordChange_FlushesOnlyPastThresholdAndInterval()
    {
        var writer = new BufferedFileWriter(FilePath, () => "x", bufferThreshold: 2, minFlushInterval: TimeSpan.Zero);

        writer.RecordChange();
        writer.RecordChange();
        Assert.Equal(0, writer.WriteCount);

        writer.RecordChange();
        Assert.Equal(1, writer.WriteCount);
    }

    [Fact]
    public void RecordChange_WithinMinInterval_DefersToFinalFlush()
    {
        var writer = new BufferedFileWriter(FilePath, () => "x", bufferThreshold: 1, minFlushInterval: TimeSpan.FromHours(1));

        for (var i = 0; i < 10; i++)
            writer.RecordChange();

        Assert.Equal(0, writer.WriteCount);

        writer.Flush();
        Assert.Equal(1, writer.WriteCount);
    }

    [Fact]
    public void Flush_BeforeFlushReportingChange_MarksDirtyAndWrites()
    {
        var writer = new BufferedFileWriter(FilePath, () => "x", bufferThreshold: 10, beforeFlush: () => true);

        writer.Flush();

        Assert.Equal(1, writer.WriteCount);
    }

    [Fact]
    public void Flush_ReportsWriteDuration()
    {
        var writes = 0;
        var writer = new BufferedFileWriter(FilePath, () => "x", bufferThreshold: 10, onWrite: _ => writes++);

        writer.RecordChange();
        writer.Flush();
        writer.Flush();

        Assert.Equal(1, writes);
    }
}
