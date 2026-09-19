using Marshal.Application.Abstractions;

namespace Marshal.Infrastructure.Data;

/// <inheritdoc cref="ISygnalZapisu"/>
/// <remarks>
/// Cała treść to jedno zdarzenie. Wywołanie jest oddzielone od zgłoszenia lokalną kopią,
/// bo odbiorca może się odpiąć między jednym a drugim — a wtedy pole byłoby już puste.
/// </remarks>
public sealed class SygnalZapisu : IWriteSignal
{
    public event Action? Saved;

    public void Report() => Saved?.Invoke();
}
