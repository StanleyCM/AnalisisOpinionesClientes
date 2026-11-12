namespace AnalisisOpiniones.Application.Extraccion;

public interface IAlmacenExtraccion
{
    Task<long> IniciarAsync(CancellationToken cancellationToken);

    Task<int> GuardarFuenteAsync(
        long ejecucionId,
        IExtractor extractor,
        IAsyncEnumerable<RegistroExtraido> registros,
        CancellationToken cancellationToken);

    Task FinalizarAsync(
        long ejecucionId,
        string estado,
        int leidos,
        string? detalle,
        CancellationToken cancellationToken);
}
