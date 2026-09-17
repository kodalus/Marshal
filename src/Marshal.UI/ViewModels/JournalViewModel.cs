using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;

namespace Marshal.UI.ViewModels;

/// <summary>Jeden wiersz dziennika, gotowy do pokazania.</summary>
/// <remarks>
/// Gotowe napisy i gotowy pędzel zamiast konwerterów: konwerter to trzecie miejsce,
/// do którego trzeba zajrzeć przy czytaniu jednego wiersza XAML-a.
/// </remarks>
public sealed record JournalRow(
    string When, string Operation, string Outcome, string? Detail, bool IsProblem)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>Pasek z lewej: czerwony przy problemie, szary przy wyniku.</summary>
    public IBrush Mark => IsProblem
        ? new SolidColorBrush(Color.FromRgb(0xC0, 0x4A, 0x4A))
        : new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));
}

/// <summary>
/// Ekran „Co się działo" (spec 12).
/// </summary>
/// <remarks>
/// Istnieje po to, żeby pytanie „dlaczego nic nie widać" dało się rozstrzygnąć bez
/// podłączania czegokolwiek kablem. Pusta siatka kalendarza ma trzy przyczyny
/// wyglądające identycznie i to jest jedyne miejsce, gdzie się rozchodzą.
/// </remarks>
public sealed partial class JournalViewModel(IActivityLog log, IClock clock) : ObservableObject
{
    public ObservableCollection<JournalRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool HasRows => Rows.Count > 0;

    public async Task LoadAsync()
    {
        var strefa = clock.Now.Offset;
        var wpisy = await log.RecentAsync();

        Rows.Clear();
        foreach (var wpis in wpisy)
        {
            Rows.Add(new JournalRow(
                wpis.At.ToOffset(strefa).ToString("MM-dd HH:mm:ss"),
                wpis.Operation,
                wpis.Outcome,
                wpis.Detail,
                wpis.Level == ActivityLevel.Problem));
        }

        var problemy = Rows.Count(w => w.IsProblem);

        Summary = Rows.Count == 0
            ? "Pusto. Aplikacja nic jeszcze nie zapisała albo dziennik został wyczyszczony."
            : $"Wpisów {Rows.Count}, w tym problemów {problemy}.";

        // Nieudany zapis do dziennika też jest cichą awarią — i akurat ta zjadłaby
        // dowody na pozostałe. Dlatego licznik jest na wierzchu, a nie w dzienniku.
        if (log.Dropped > 0)
        {
            Summary += $" Nie udało się zapisać {log.Dropped} wpisów.";
        }

        OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private async Task ClearAsync()
    {
        await log.ClearAsync();
        await LoadAsync();
    }
}
