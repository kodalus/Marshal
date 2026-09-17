using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Marshal.UI.Views;

/// <summary>
/// Ekran awarii startu — treść wyjątku, nie zniknięcie okna.
/// </summary>
/// <remarks>
/// <para>
/// Powstał po 17.09, kiedy aplikacja nie wstawała ani na Androidzie, ani na Windowsie,
/// a jedynym objawem było białe tło i natychmiastowe zamknięcie. Wyjątek leciał
/// z <c>async void</c> i zabijał proces, więc jedyną drogą do treści błędu był kabel
/// i <c>logcat</c> — czyli w praktyce nic.
/// </para>
/// <para>
/// Bez powiązań, bez modelu widoku i bez bazy: to jest ekran na wypadek, gdy właśnie
/// te rzeczy zawiodły. Tekst da się zaznaczyć, żeby dało się go przepisać albo
/// sfotografować.
/// </para>
/// </remarks>
internal static class StartupFailure
{
    public static Control Build(Exception blad) =>
        new ScrollViewer
        {
            Padding = new Avalonia.Thickness(20),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Marshal nie wstał",
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = "Aplikacja zatrzymała się przy uruchamianiu. Poniżej jest to, "
                            + "co dokładnie poszło nie tak.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                    },
                    new SelectableTextBlock
                    {
                        Text = blad.ToString(),
                        FontFamily = FontFamily.Parse("monospace"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    },
                },
            },
        };
}
