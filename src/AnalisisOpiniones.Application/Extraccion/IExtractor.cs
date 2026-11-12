namespace AnalisisOpiniones.Application.Extraccion;

public interface IExtractor
{
    string CodigoFuente { get; }
    string TipoFuente { get; }
    IAsyncEnumerable<RegistroExtraido> ExtraerAsync(CancellationToken cancellationToken);
}
