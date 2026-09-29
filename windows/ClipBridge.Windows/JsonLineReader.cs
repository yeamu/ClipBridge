using System.IO;

namespace ClipBridge.Windows;

internal sealed class JsonLineReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position, _length;

    internal async Task<ReadOnlyMemory<byte>?> ReadAsync(int maxBytes, CancellationToken cancellationToken)
    {
        MemoryStream? line = new();
        IDisposable? activity = null;
        try
        {
            while (true)
            {
                if (_position == _length)
                {
                    _length = await stream.ReadAsync(_buffer, cancellationToken);
                    _position = 0;
                    if (_length == 0)
                    {
                        if (line.Length == 0) return default(ReadOnlyMemory<byte>?);
                        return Content(line);
                    }
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _length - _position);
                var end = newline < 0 ? _length : newline;
                var count = end - _position;
                if (line.Length + count > maxBytes) throw new InvalidDataException("同步消息超过大小限制");
                if (activity is null && line.Length + count >= TransferMemory.LargeTransferBytes)
                    activity = TransferMemory.BeginActivity();
                line.Write(_buffer, _position, count);
                _position = newline < 0 ? _length : end + 1;
                if (newline >= 0) return Content(line);
            }
        }
        finally
        {
            var released = line?.Capacity ?? 0;
            line?.Dispose(); line = null;
            activity?.Dispose(); activity = null;
            TransferMemory.Released(released);
        }
    }

    private static ReadOnlyMemory<byte> Content(MemoryStream stream)
    {
        var length = (int)stream.Length;
        var buffer = stream.GetBuffer();
        if (length > 0 && buffer[length - 1] == '\r') length--;
        return buffer.AsMemory(0, length);
    }
}
