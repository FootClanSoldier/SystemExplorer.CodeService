using System.Text;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal sealed class BoundedTextCapture
{
    private readonly int _maxBytes;
    private readonly Queue<string> _chunks = new();
    private int _retainedBytes;

    public BoundedTextCapture(int maxBytes)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    public bool Truncated { get; private set; }

    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;

        string chunk = text.ToString();
        int bytes = Encoding.UTF8.GetByteCount(chunk);
        _chunks.Enqueue(chunk);
        _retainedBytes += bytes;

        while (_retainedBytes > _maxBytes && _chunks.Count > 0)
        {
            string removed = _chunks.Dequeue();
            _retainedBytes -= Encoding.UTF8.GetByteCount(removed);
            Truncated = true;
        }
    }

    public string GetText()
    {
        StringBuilder builder = new(Math.Min(_retainedBytes, _maxBytes));
        foreach (string chunk in _chunks)
            builder.Append(chunk);
        return builder.ToString();
    }
}
