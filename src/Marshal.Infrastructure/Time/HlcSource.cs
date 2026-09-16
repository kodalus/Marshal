using Marshal.Application.Abstractions;
using Marshal.Domain.Primitives;

namespace Marshal.Infrastructure.Time;

/// <summary>
/// Zegar logiczny urządzenia (spec 3.5). Trzyma ostatni wydany znacznik i pilnuje,
/// żeby każdy kolejny był ściśle większy, także przy współbieżnych zapisach.
/// </summary>
public sealed class HlcSource : IHlcSource
{
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private Hlc _last;

    public HlcSource(IClock clock, string deviceId, Hlc? resumeFrom = null)
    {
        _clock = clock;
        DeviceId = deviceId;
        _last = resumeFrom ?? Hlc.Zero(deviceId);

        if (_last.DeviceId != deviceId)
        {
            throw new ArgumentException(
                $"Wznowiony znacznik należy do urządzenia '{_last.DeviceId}', nie '{deviceId}'.",
                nameof(resumeFrom));
        }
    }

    public string DeviceId { get; }

    /// <summary>Ostatni wydany znacznik — do zapisania przy zamykaniu aplikacji.</summary>
    public Hlc Last
    {
        get { lock (_gate) return _last; }
    }

    public Hlc Next()
    {
        lock (_gate)
        {
            _last = Hlc.Next(_last, PhysicalNowMs());
            return _last;
        }
    }

    public Hlc Observe(Hlc remote)
    {
        lock (_gate)
        {
            _last = Hlc.Merge(_last, remote, PhysicalNowMs());
            return _last;
        }
    }

    private long PhysicalNowMs() => _clock.Now.ToUnixTimeMilliseconds();
}
