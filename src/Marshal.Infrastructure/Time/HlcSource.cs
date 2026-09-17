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
    private readonly Func<string> _deviceId;
    private readonly Func<Hlc?> _resumeFrom;
    private string? _id;
    private Hlc _last;

    public HlcSource(IClock clock, string deviceId, Hlc? resumeFrom = null)
        : this(clock, () => deviceId, () => resumeFrom)
    {
    }

    /// <summary>
    /// Tożsamość i wznowienie **odczytywane przy pierwszym użyciu**, nie przy tworzeniu.
    /// </summary>
    /// <remarks>
    /// Jedno i drugie leży w bazie, a zegar powstaje przy składaniu zależności — czyli
    /// zanim migracja tę bazę założy. Odczyt w konstruktorze znaczył wyjątek
    /// z rozwiązywania kontenera: okno pokazywało białe tło i natychmiast się zamykało,
    /// na obu platformach, przy każdym uruchomieniu. Fabryka usługi nie ma prawa robić
    /// wejścia-wyjścia — to nie jest styl, tylko warunek, żeby kolejność składania
    /// i kolejność przygotowania bazy nie musiały się o siebie opierać.
    /// </remarks>
    public HlcSource(IClock clock, Func<string> deviceId, Func<Hlc?> resumeFrom)
    {
        _clock = clock;
        _deviceId = deviceId;
        _resumeFrom = resumeFrom;
    }

    public string DeviceId
    {
        get { lock (_gate) { Rozstrzygnij(); return _id!; } }
    }

    /// <summary>Ostatni wydany znacznik — do zapisania przy zamykaniu aplikacji.</summary>
    public Hlc Last
    {
        get { lock (_gate) { Rozstrzygnij(); return _last; } }
    }

    /// <summary>Wołane wyłącznie pod blokadą.</summary>
    private void Rozstrzygnij()
    {
        if (_id is not null)
        {
            return;
        }

        var id = _deviceId();
        var wznowiony = _resumeFrom();

        if (wznowiony is { } znacznik && znacznik.DeviceId != id)
        {
            throw new InvalidOperationException(
                $"Wznowiony znacznik należy do urządzenia '{znacznik.DeviceId}', nie '{id}'.");
        }

        _last = wznowiony ?? Hlc.Zero(id);
        _id = id;
    }

    public Hlc Next()
    {
        lock (_gate)
        {
            Rozstrzygnij();
            _last = Hlc.Next(_last, PhysicalNowMs());
            return _last;
        }
    }

    public Hlc Observe(Hlc remote)
    {
        lock (_gate)
        {
            Rozstrzygnij();
            _last = Hlc.Merge(_last, remote, PhysicalNowMs());
            return _last;
        }
    }

    private long PhysicalNowMs() => _clock.Now.ToUnixTimeMilliseconds();
}
