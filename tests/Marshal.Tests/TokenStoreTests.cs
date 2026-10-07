using FluentAssertions;
using Marshal.Infrastructure.Sync.Google;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Składnica żetonów Google pod kilkoma żądaniami naraz.
/// </summary>
/// <remarks>
/// <b>Czego ten test nie dowodzi.</b> Objaw — „the process cannot access the file" przy
/// czterech kalendarzach odświeżanych równolegle — jest objawem windowsowym: tam dwa
/// równoczesne zapisy do jednego pliku kończą się odmową dostępu, a na Linuksie, gdzie
/// chodzi CI, przechodzą oba. Test pilnuje więc tego, co da się sprawdzić wszędzie:
/// że wejście równoległe jest obsłużone i że żeton wraca taki, jaki wszedł. Samego
/// wyścigu o uchwyt nie odtworzy — do tego trzeba Windowsa.
/// </remarks>
public sealed class TokenStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "marshal-zetony-" + Guid.CreateVersion7().ToString("N"));

    [Fact]
    public async Task Zeton_wraca_taki_jaki_wszedl()
    {
        var store = new TokenStore(_folder);

        await store.StoreAsync("marshal-kalendarz-zapis", "żeton");

        (await store.GetAsync<string>("marshal-kalendarz-zapis")).Should().Be("żeton");

        await store.DeleteAsync<string>("marshal-kalendarz-zapis");

        (await store.GetAsync<string>("marshal-kalendarz-zapis")).Should().BeNull();
    }

    [Fact]
    public async Task Kilka_zapisow_naraz_pod_ten_sam_klucz_nie_wywraca_zadnego()
    {
        // Tyle, ile kalendarzy odświeża się równolegle — i tyle odświeżeń żetonu
        // wypada naraz w chwili, gdy ten wygaśnie.
        var store = new TokenStore(_folder);

        var writes = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => store.StoreAsync("marshal-kalendarz-zapis", $"żeton {i}")))
            .ToArray();

        var all = async () => await Task.WhenAll(writes);

        await all.Should().NotThrowAsync();

        (await store.GetAsync<string>("marshal-kalendarz-zapis")).Should().StartWith("żeton ");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
