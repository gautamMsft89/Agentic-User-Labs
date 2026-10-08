using System.Net;

namespace WorkIqFiles;

internal sealed class WorkIqEnvelopeLimitException(long observedBytes, bool atLeast, int limitBytes)
    : HttpRequestException("WorkIQ blob response exceeds local envelope limit.")
{
    internal long ObservedBytes { get; } = observedBytes;
    internal bool AtLeast { get; } = atLeast;
    internal int LimitBytes { get; } = limitBytes;
}

internal sealed class BoundedBlobResponse : HttpContent
{
    internal const int MaxEnvelopeBytes = 256 * 1024;
    private readonly HttpContent inner;
    private readonly int limitBytes;
    private readonly WorkIqResponseEvidence? evidence;
    internal BoundedBlobResponse(HttpContent inner, int limitBytes = MaxEnvelopeBytes, WorkIqResponseEvidence? evidence = null)
    {
        this.inner = inner;
        this.limitBytes = limitBytes;
        this.evidence = evidence;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
    {
        if (evidence is not null) evidence.Stage = "response stream acquisition";
        try { return new LimitedStream(await inner.ReadAsStreamAsync(ct), limitBytes, evidence); }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        { evidence?.Failure(error); throw; }
    }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        using Stream source = await CreateContentReadStreamAsync();
        await source.CopyToAsync(stream);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    private sealed class LimitedStream(Stream inner, int limitBytes, WorkIqResponseEvidence? evidence) : Stream
    {
        private long read;
        private int Count(int count)
        {
            read += count;
            evidence?.Read(read, complete: count == 0);
            if (read > limitBytes)
            {
                WorkIqEnvelopeLimitException error = new(read, atLeast: true, limitBytes);
                evidence?.Failure(error);
                throw error;
            }
            return count;
        }
        private int Allowed(int requested) => (int)Math.Min(requested, limitBytes - read + 1);
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            try { return Count(inner.Read(buffer, offset, Allowed(count))); }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            { evidence?.Failure(error); throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            try { return Count(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], ct)); }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            { evidence?.Failure(error); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
