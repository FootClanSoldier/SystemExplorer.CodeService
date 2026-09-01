using System.Text;

namespace SystemExplorer.CodeService;

internal sealed class BoundedTextCapture
{
    private readonly object _sync = new();
    private readonly int _maxBytes;
    private readonly Queue<string> _chunks = new();
    private int _retainedBytes;
    private bool _truncated;

    public BoundedTextCapture(int maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        _maxBytes = maxBytes;
    }

    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        string chunk = text.ToString();
        int bytes = Encoding.UTF8.GetByteCount(chunk);

        lock (_sync)
        {
            _chunks.Enqueue(chunk);
            _retainedBytes += bytes;

            while (_retainedBytes > _maxBytes && _chunks.Count > 0)
            {
                string removed = _chunks.Dequeue();
                _retainedBytes -= Encoding.UTF8.GetByteCount(removed);
                _truncated = true;
            }
        }
    }

    public BoundedTextCaptureSnapshot Capture()
    {
        lock (_sync)
        {
            StringBuilder builder = new(Math.Min(_retainedBytes, _maxBytes));
            foreach (string chunk in _chunks)
            {
                builder.Append(chunk);
            }

            return new BoundedTextCaptureSnapshot(builder.ToString(), _truncated);
        }
    }
}

internal readonly record struct BoundedTextCaptureSnapshot(string Text, bool Truncated);
