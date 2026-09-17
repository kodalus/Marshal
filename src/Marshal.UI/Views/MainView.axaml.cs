using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Marshal.UI.ViewModels;

namespace Marshal.UI.Views;

public partial class MainView : UserControl
{
    /// <summary>
    /// Szerokość, poniżej której nawigacja schodzi na dół.
    /// </summary>
    /// <remarks>
    /// Telefon w pionie to około 360–430 jednostek, więc próg mógłby być niższy —
    /// ale wąskie okno na pulpicie ma ten sam problem co telefon, a nie ma powodu,
    /// żeby rozstrzygało o tym urządzenie zamiast miejsca, które faktycznie jest.
    /// </remarks>
    private const double WidokWaski = 720;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePicker();

        // Układ dobierany z faktycznej szerokości, nie z platformy: obrót telefonu
        // i zwężenie okna to ta sama zmiana.
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.IsNarrow = e.NewSize.Width < WidokWaski;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ScrollViewer? _siatka;

    /// <summary>Przesunięcie, które czeka na zmierzenie siatki. Null, gdy nic nie czeka.</summary>
    private double? _docelowe;

    /// <summary>
    /// Podpięcie przewijania siatki kalendarza pod prośby modelu widoku.
    /// </summary>
    /// <remarks>
    /// Odpięcie przed podpięciem, bo podstawienie modelu potrafi się powtórzyć,
    /// a druga subskrypcja przewijałaby siatkę dwa razy — przy trzeciej i czwartej
    /// przestaje to być niewidoczne.
    /// </remarks>
    private void WireCalendar(MainViewModel model)
    {
        _siatka ??= this.FindControl<ScrollViewer>("SiatkaKalendarza");

        model.Calendar.ScrollRequested -= NaProsbeOPrzewiniecie;
        model.Calendar.ScrollRequested += NaProsbeOPrzewiniecie;

        if (_siatka is not null)
        {
            _siatka.LayoutUpdated -= NaUkladzie;
            _siatka.LayoutUpdated += NaUkladzie;
        }
    }

    private void NaProsbeOPrzewiniecie(double punkty)
    {
        _docelowe = punkty;
        Przewin();
    }

    private void NaUkladzie(object? nadawca, EventArgs e) => Przewin();

    /// <summary>
    /// Ustawienie przesunięcia, gdy jest już czym przesuwać.
    /// </summary>
    /// <remarks>
    /// Prośba przychodzi po wczytaniu danych, a więc **przed** złożeniem układu:
    /// ekran kalendarza bywa w tym momencie dopiero odsłaniany i siatka ma zerową
    /// wysokość. Przesunięcie zostałoby wtedy przycięte do zera bez śladu, że
    /// cokolwiek się nie udało. Dlatego prośba czeka na pierwszy układ, w którym
    /// siatka ma już rozmiar, i dopiero wtedy jest realizowana — raz.
    /// </remarks>
    private void Przewin()
    {
        if (_docelowe is not { } cel || _siatka is null || _siatka.Extent.Height <= 0)
        {
            return;
        }

        var zapas = Math.Max(0, _siatka.Extent.Height - _siatka.Viewport.Height);

        _siatka.Offset = new Vector(_siatka.Offset.X, Math.Clamp(cel, 0, zapas));
        _docelowe = null;
    }

    /// <summary>
    /// Wybór pliku kopii. Robi go okno, nie model widoku.
    /// </summary>
    /// <remarks>
    /// Na Androidzie „zapisz plik" to dialog systemowy podpięty do bieżącego ekranu,
    /// a wynikiem jest uchwyt do treści, nie ścieżka na dysku — ścieżki w rozumieniu
    /// pulpitu tam po prostu nie ma. Dlatego model widoku dostaje gotowy strumień
    /// i wie, **co** zapisać, a nie **gdzie**.
    /// </remarks>
    private void WirePicker()
    {
        if (DataContext is not MainViewModel model || TopLevel.GetTopLevel(this) is not { } okno)
        {
            return;
        }

        // Pierwsze rozstrzygnięcie układu: zdarzenie rozmiaru potrafi wypaść przed
        // podstawieniem modelu, a wtedy nie miałby go kto ustawić.
        model.IsNarrow = Bounds.Width > 0 && Bounds.Width < WidokWaski;

        WireCalendar(model);

        model.Settings.SaveRequested = async nazwa =>
        {
            var plik = await okno.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Kopia zapasowa Marshala",
                SuggestedFileName = nazwa,
                DefaultExtension = "json",
                FileTypeChoices = [Json],
            });

            return plik is null ? null : await plik.OpenWriteAsync();
        };

        model.Settings.OpenRequested = async () =>
        {
            var pliki = await okno.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Wczytaj kopię Marshala",
                AllowMultiple = false,
                FileTypeFilter = [Json],
            });

            return pliki.Count == 0 ? null : await pliki[0].OpenReadAsync();
        };
    }

    private static FilePickerFileType Json => new("Kopia Marshala")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };
}
