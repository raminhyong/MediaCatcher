using System.Diagnostics;

namespace MediaCatcher.Core.Http;

public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _rate;
    private readonly double _capacity;
    private double _tokens;
    private long _lastTimestamp;

    public TokenBucket(long bytesPerSecond)
    {
        _rate = Math.Max(0, bytesPerSecond);
        _capacity = Math.Max(_rate, 256 * 1024);
        _tokens = _capacity;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public bool IsEnabled => _rate > 0;

    public async ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (!IsEnabled || bytes <= 0)
        {
            return;
        }

        var charge = Math.Min(bytes, _capacity);

        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                Refill();
                if (_tokens >= charge)
                {
                    _tokens -= charge;
                    return;
                }

                var seconds = (charge - _tokens) / _rate;
                delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.005, 0.5));
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        if (elapsedSeconds <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsedSeconds * _rate));
    }
}

public sealed class SpeedMeter
{
    private long _lastBytes;
    private long _lastTimestamp;
    private double _ewma;

    public double AddSample(long totalBytes)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastTimestamp == 0)
        {
            _lastTimestamp = now;
            _lastBytes = totalBytes;
            return 0;
        }

        var elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        if (elapsed < 0.15)
        {
            return _ewma;
        }

        var delta = Math.Max(0, totalBytes - _lastBytes);
        var instant = delta / elapsed;
        _ewma = _ewma <= 0 ? instant : (_ewma * 0.7) + (instant * 0.3);
        _lastTimestamp = now;
        _lastBytes = totalBytes;
        return _ewma;
    }

    public void Reset()
    {
        _lastTimestamp = 0;
        _lastBytes = 0;
        _ewma = 0;
    }
}
