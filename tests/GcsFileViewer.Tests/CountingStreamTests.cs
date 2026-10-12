using GcsFileViewer.Storage;

namespace GcsFileViewer.Tests;

public class CountingStreamTests
{
    private readonly MemoryStream _inner = new();
    private readonly CountingStream _stream;

    public CountingStreamTests() => _stream = new CountingStream(_inner);

    [Fact]
    public void Nothing_is_counted_before_any_write() => Assert.Equal(0, _stream.BytesWritten);

    [Fact]
    public void It_is_write_only()
    {
        Assert.True(_stream.CanWrite);
        Assert.False(_stream.CanRead);
        Assert.False(_stream.CanSeek);
    }

    [Fact]
    public void An_array_write_passes_through_only_the_requested_slice()
    {
        _stream.Write([1, 2, 3, 4, 5], 1, 3);

        Assert.Equal(3, _stream.BytesWritten);
        Assert.Equal([2, 3, 4], _inner.ToArray());
    }

    [Fact]
    public void A_span_write_passes_through_and_is_counted()
    {
        _stream.Write(new ReadOnlySpan<byte>([7, 8]));

        Assert.Equal(2, _stream.BytesWritten);
        Assert.Equal([7, 8], _inner.ToArray());
    }

    [Fact]
    public async Task An_async_array_write_passes_through_only_the_requested_slice()
    {
        await _stream.WriteAsync([1, 2, 3, 4, 5], 2, 2, CancellationToken.None);

        Assert.Equal(2, _stream.BytesWritten);
        Assert.Equal([3, 4], _inner.ToArray());
    }

    [Fact]
    public async Task An_async_memory_write_passes_through_and_is_counted()
    {
        await _stream.WriteAsync(new ReadOnlyMemory<byte>([9, 9, 9]), TestContext.Current.CancellationToken);

        Assert.Equal(3, _stream.BytesWritten);
        Assert.Equal([9, 9, 9], _inner.ToArray());
    }

    [Fact]
    public async Task Writes_of_every_kind_add_up_in_order()
    {
        _stream.Write([1], 0, 1);
        _stream.Write(new ReadOnlySpan<byte>([2, 3]));
        await _stream.WriteAsync([4, 5, 6], 0, 3, CancellationToken.None);
        await _stream.WriteAsync(new ReadOnlyMemory<byte>([7]), TestContext.Current.CancellationToken);
        _stream.WriteByte(8);

        Assert.Equal(8, _stream.BytesWritten);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], _inner.ToArray());
    }

    [Fact]
    public async Task Copying_another_stream_into_it_counts_every_byte()
    {
        var source = new MemoryStream(new byte[200_000]);

        await source.CopyToAsync(_stream, TestContext.Current.CancellationToken);

        Assert.Equal(200_000, _stream.BytesWritten);
        Assert.Equal(200_000, _inner.Length);
    }

    [Fact]
    public void An_empty_write_counts_nothing()
    {
        _stream.Write([], 0, 0);

        Assert.Equal(0, _stream.BytesWritten);
    }

    [Fact]
    public async Task A_write_the_inner_stream_rejects_is_not_counted()
    {
        var stream = new CountingStream(new FailingStream());

        Assert.Throws<IOException>(() => stream.Write([1, 2], 0, 2));
        Assert.Throws<IOException>(() => stream.Write(new ReadOnlySpan<byte>([1, 2])));
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync([1, 2], 0, 2, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () => await stream.WriteAsync(new ReadOnlyMemory<byte>([1, 2]), TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.BytesWritten);
    }

    [Fact]
    public async Task Flushing_flushes_the_inner_stream()
    {
        var inner = new FlushCountingStream();
        var stream = new CountingStream(inner);

        stream.Flush();
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, inner.Flushes);
    }

    [Fact]
    public void Reading_seeking_and_resizing_are_not_supported()
    {
        Assert.Throws<NotSupportedException>(() => _stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => _stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => _stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => _stream.Length);
        Assert.Throws<NotSupportedException>(() => _stream.Position);
        Assert.Throws<NotSupportedException>(() => _stream.Position = 0);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Disk full.");

        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Disk full.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Disk full."));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Disk full."));
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int Flushes { get; private set; }

        public override void Flush() => Flushes++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }
}
