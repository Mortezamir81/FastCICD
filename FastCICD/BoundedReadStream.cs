namespace FastCICD;

public sealed class BoundedReadStream : Stream
{
	private readonly Stream _inner;
	private readonly long _length;
	private readonly Action<long> _progress;
	private long _read;

	public BoundedReadStream(Stream inner, long length, Action<long> progress)
	{
		_inner = inner;
		_length = length;
		_progress = progress;
	}

	public override bool CanRead => _inner.CanRead;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => _length;
	public override long Position { get => _read; set => throw new NotSupportedException(); }

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		var remaining = _length - _read;
		if (remaining <= 0)
			return 0;

		var count = (int)Math.Min(buffer.Length, remaining);
		var read = await _inner.ReadAsync(buffer[..count], cancellationToken);
		_read += read;
		_progress(_read);
		return read;
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		var remaining = _length - _read;
		if (remaining <= 0)
			return 0;

		var read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
		_read += read;
		_progress(_read);
		return read;
	}

	public override ValueTask DisposeAsync() => _inner.DisposeAsync();
	protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
	public override void Flush() => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
