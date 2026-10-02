using System.Diagnostics;
using System.Text;

namespace Neuroset.Application;

public sealed class TextDeltaBatcher(TimeSpan minimumFlushInterval)
{
    private readonly StringBuilder _pending = new();
    private long _lastFlushTimestamp = Stopwatch.GetTimestamp();

    public bool HasPending => _pending.Length > 0;
    public char? LastCharacter => _pending.Length == 0 ? null : _pending[^1];

    public void Append(string value)
    {
        if (!string.IsNullOrEmpty(value)) _pending.Append(value);
    }

    public bool TryDrain(out string value, bool force = false)
    {
        if (_pending.Length == 0 || (!force && Stopwatch.GetElapsedTime(_lastFlushTimestamp) < minimumFlushInterval))
        {
            value = string.Empty;
            return false;
        }

        value = _pending.ToString();
        _pending.Clear();
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
        return true;
    }
}
