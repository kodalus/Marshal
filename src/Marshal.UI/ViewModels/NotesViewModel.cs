using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application;
using Marshal.Application.UseCases;
using Marshal.Domain.Notes;
using Marshal.Infrastructure.Notes;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Notatki: lista, edytor i podgląd (spec 11).
/// </summary>
/// <remarks>
/// Edytor i podgląd obok siebie, nie na przemian. Przełącznik „pisz / oglądaj" każe
/// pamiętać, w którym trybie się jest, a przy notatce, którą pisze się raz i czyta
/// dziesięć razy, to jest pytanie zadawane bez potrzeby.
/// </remarks>
/// <summary>
/// Notatka gotowa do pokazania na kafelku.
/// </summary>
/// <remarks>
/// Osobno od samej notatki, bo początek tekstu jest rzeczą widoku, a nie notatki:
/// zależy od tego, ile mieści się na kafelku, i liczy się go przy składaniu listy,
/// a nie przy zapisie. Notatka jedzie w środku, żeby otwarcie miało co otworzyć.
/// </remarks>
public sealed record NoteCard(Note Note, string Title, string Beginning)
{
    public bool IsPinned => Note.IsPinned;

    /// <summary>Notatka bez treści nie ma czego pokazać, a pusta linijka zajmuje miejsce.</summary>
    public bool HasBeginning => Beginning.Length > 0;
}

public sealed partial class NotesViewModel(NoteService notes) : ObservableObject
{
    private Guid _openId;

    /// <summary>Szukanie rusza na każdy znak, a kontekst bazy jest jeden — zob. LatestOnly.</summary>
    private readonly LatestOnly _queue = new();

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    public ObservableCollection<NoteCard> Items { get; } = [];

    /// <summary>
    /// Szerokość kafelka, policzona z szerokości, którą ma lista.
    /// </summary>
    /// <remarks>
    /// Liczona tutaj, nie w oknie: to samo pytanie, co przy kolumnach kalendarza —
    /// ile ich się mieści i po ile. Stała szerokość znaczyłaby na telefonie jeden
    /// kafelek i pas pustego miejsca obok, a na szerokim oknie kafelki po dwieście
    /// punktów z przerwami jak po zębach.
    /// </remarks>
    [ObservableProperty]
    public partial double CardWidth { get; set; } = 240;

    /// <summary>Najwęższy kafelek, jaki jeszcze coś mówi.</summary>
    /// <remarks>
    /// Poniżej tego początek tekstu schodzi do dwóch słów w linijce i przestaje być
    /// początkiem tekstu, a zaczyna być szumem. Przy oknie węższym niż to zostaje
    /// jedna kolumna na całą szerokość.
    /// </remarks>
    private const double NarrowestCard = 260;

    /// <summary>Odstęp między kafelkami — ten sam, co margines w szablonie.</summary>
    private const double CardGap = 10;

    public void SetAvailableWidth(double whole)
    {
        if (whole <= 0)
        {
            return;
        }

        var columns = Math.Max(1, (int)((whole + CardGap) / (NarrowestCard + CardGap)));

        // Pół punktu w dół, bo szerokość zaokrągloną w górę ostatni kafelek w wierszu
        // spycha do następnego — i zostaje po nim dziura na całą kolumnę.
        CardWidth = Math.Max(
            NarrowestCard / 2, ((whole - (CardGap * (columns - 1))) / columns) - 0.5);
    }

    public ObservableCollection<BlockView> Preview { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string Empty => string.IsNullOrWhiteSpace(Query)
        ? "Jeszcze nic tu nie ma. Notatka to rzecz, której nie trzeba robić, tylko mieć pod ręką."
        : "Nic nie pasuje do szukanego.";

    public async Task LoadAsync() => await SearchAsync();

    [RelayCommand]
    private Task SearchAsync() => _queue.RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        Items.Clear();
        foreach (var note in await notes.SearchAsync(Query))
        {
            Items.Add(new NoteCard(note, note.Title, Beginning(note.Content)));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Empty));
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            return;
        }

        var note = await notes.CreateAsync(NewTitle);
        NewTitle = string.Empty;

        await SearchAsync();
        Open(note);
    }

    [RelayCommand]
    private void Open(Note? note)
    {
        if (note is null)
        {
            return;
        }

        _openId = note.Id;
        Title = note.Title;
        Content = note.Content;
        IsPinned = note.IsPinned;
        IsEditing = true;
        RenderPreview();
    }

    /// <summary>
    /// Początek tekstu notatki — tyle, ile mieści się na kafelku.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bez znaków zapisu: notatka pisana markdownem zaczyna się od „# " albo „- ",
    /// a kafelek pokazujący „# Zakupy — - mleko" mówi o zapisie, nie o treści. Zdejmowane
    /// są tylko znaki wiodące i te, które otaczają słowa — pełne rozłożenie markdownu
    /// jest tu pracą na kilkadziesiąt notatek naraz, a odpowiada na pytanie, którego
    /// przy kafelku nikt nie zadaje.
    /// </para>
    /// <para>
    /// Odstępy zbite do jednego, bo notatka ma akapity i puste linie, a kafelek ma
    /// cztery linijki. Bez tego pierwsze zdanie spychała pusta linia pod tytułem.
    /// </para>
    /// </remarks>
    internal static string Beginning(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var text = new StringBuilder(Math.Min(content.Length, Glance * 2));
        var space = true;

        foreach (var sign in content)
        {
            if (char.IsWhiteSpace(sign))
            {
                space = true;
                continue;
            }

            // Znak zapisu na początku słowa jest zapisem; w środku słowa bywa treścią
            // (gwiazdka w „2*3", kreska w „biało-czerwony"), więc zostaje.
            if (space && Markup.Contains(sign))
            {
                continue;
            }

            if (space && text.Length > 0)
            {
                text.Append(' ');
            }

            space = false;
            text.Append(sign);

            if (text.Length >= Glance)
            {
                text.Append('…');
                break;
            }
        }

        return text.ToString();
    }

    /// <summary>Ile znaków początku tekstu mieści się na kafelku.</summary>
    private const int Glance = 160;

    private static readonly SearchValues<char> Markup = SearchValues.Create("#-*>_`+=~");

    [RelayCommand]
    private void Close() => IsEditing = false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        await notes.SaveAsync(_openId, Title, Content, IsPinned);
        IsEditing = false;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await notes.DeleteAsync(_openId);
        IsEditing = false;
        await SearchAsync();
    }

    /// <summary>
    /// Podgląd przeliczany przy każdej zmianie treści.
    /// </summary>
    /// <remarks>
    /// Przy notatce osobistej to kilkadziesiąt bloków, więc przeliczanie na każdy znak
    /// jest tańsze niż jakikolwiek mechanizm odkładania go w czasie — a odkładanie
    /// znaczyłoby podgląd chwilami nieaktualny, czyli dokładnie to, przed czym podgląd
    /// obok edytora ma chronić.
    /// </remarks>
    private void RenderPreview()
    {
        Preview.Clear();
        foreach (var block in MarkdownReader.Read(Content).Blocks)
        {
            Preview.Add(BlockView.From(block));
        }

        OnPropertyChanged(nameof(HasMarkup));
    }

    /// <summary>
    /// Czy notatka ma cokolwiek do pokazania w podglądzie.
    /// </summary>
    /// <remarks>
    /// Podgląd stał dotąd zawsze i przy zwykłej notatce był dosłownie drugą kopią tego
    /// samego tekstu — dwie połówki okna z tą samą treścią. Podgląd ma sens dopiero
    /// wtedy, gdy pokazuje coś, czego w polu edycji nie widać: nagłówek, punkt listy,
    /// cytat, wyróżnienie, odnośnik. Bez tego zabiera połowę szerokości za nic.
    /// </remarks>
    public bool HasMarkup =>
        Preview.Any(b =>
            b.IsListItem || b.IsQuote || b.IsCode || b.Indent > 0
            || b.Weight != FontWeight.Normal
            || b.Spans.Any(w => w.IsLink || w.IsCode
                || w.Weight != FontWeight.Normal
                || w.Style != FontStyle.Normal));

    partial void OnContentChanged(string value) => RenderPreview();

    partial void OnQueryChanged(string value) => _ = SearchAsync();
}
