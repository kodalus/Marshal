using CommunityToolkit.Mvvm.ComponentModel;
using Marshal.Application.Abstractions;
using Marshal.Infrastructure.Time;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Etap 0. Pokazuje znacznik zegara logicznego wygenerowany przy starcie — to
/// najkrótszy dowód, że warstwy Domain, Application i Infrastructure są sięgalne
/// z interfejsu na obu platformach.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
        : this(new HlcSource(new SystemClock(), "etap0"))
    {
    }

    public MainViewModel(IHlcSource hlc)
    {
        DeviceId = hlc.DeviceId;
        Stamp = hlc.Next().ToString();
    }

    [ObservableProperty]
    public partial string DeviceId { get; set; }

    [ObservableProperty]
    public partial string Stamp { get; set; }
}
