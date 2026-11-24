using Microsoft.Extensions.Logging;

namespace AnalisisOpiniones.Application.Dimensiones;

public interface IAlmacenDimensiones
{
    Task<IReadOnlyList<ResultadoDimension>> CargarAsync(CancellationToken cancellationToken);
}

public sealed record ResultadoDimension(string Nombre, int Insertados, int Actualizados, int Existentes);

public sealed class CargarDimensiones(IAlmacenDimensiones almacen, ILogger<CargarDimensiones> logger)
{
    public async Task EjecutarAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Iniciando carga de dimensiones desde los catalogos CSV y staging.");
        foreach (var resultado in await almacen.CargarAsync(cancellationToken))
        {
            logger.LogInformation(
                "{Dimension}: {Insertados} insertados, {Actualizados} actualizados, {Existentes} existentes al finalizar.",
                resultado.Nombre, resultado.Insertados, resultado.Actualizados, resultado.Existentes);
        }
    }
}
