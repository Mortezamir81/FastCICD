using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace FastCICD;

// Helper class to track HTTP upload progress
public class ProgressStreamContent : HttpContent
{
	private readonly Stream _content;
	private readonly int _bufferSize;
	private readonly Action<long, long> _progressCallback;

	public ProgressStreamContent(Stream content, Action<long, long> progressCallback, int bufferSize = 8192)
	{
		_content = content ?? throw new ArgumentNullException(nameof(content));
		_progressCallback = progressCallback ?? throw new ArgumentNullException(nameof(progressCallback));
		_bufferSize = bufferSize;
	}

	protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
	{
		var buffer = new byte[_bufferSize];
		long totalBytes = _content.Length;
		long uploadedBytes = 0;

		int bytesRead;
		while ((bytesRead = await _content.ReadAsync(buffer, 0, buffer.Length)) != 0)
		{
			await stream.WriteAsync(buffer, 0, bytesRead);
			uploadedBytes += bytesRead;

			// Trigger the callback with current progress
			_progressCallback(uploadedBytes, totalBytes);
		}
	}

	protected override bool TryComputeLength(out long length)
	{
		length = _content.Length;
		return true;
	}
}