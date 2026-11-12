using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace AnalisisOpiniones.Application.Extraccion;

public sealed class EjecutarExtraccion(
    IEnumerable<IExtractor> extractores,
    IAlmacenExtraccion almacen,
    ILogger<EjecutarExtraccion> logger)
{
    public async Task<ResultadoExtraccion> EjecutarAsync(CancellationToken cancellationToken)
    {
        var reloj = Stopwatch.StartNew();
        logger.LogInformation("Iniciando ejecucion de extraccion.");
        var ejecucionId = await almacen.IniciarAsync(cancellationToken);
        logger.LogInformation("Ejecucion {EjecucionId} registrada.", ejecucionId);

        var totalLeidos = 0;
        var totalGuardados = 0;
        var fuentesCompletadas = 0;
        var errores = new List<string>();
        foreach (var extractor in extractores)
        {
            var relojFuente = Stopwatch.StartNew();
            var leidos = 0;
            var guardados = 0;
            logger.LogInformation("Ejecucion {EjecucionId}: iniciando fuente {Fuente}.",
                ejecucionId, extractor.CodigoFuente);

            try
            {
                guardados = await almacen.GuardarFuenteAsync(
                    ejecucionId, extractor, ContarAsync(cancellationToken), cancellationToken);
                fuentesCompletadas++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                errores.Add($"{extractor.CodigoFuente}: cancelada.");
                break;
            }
            catch (Exception exception)
            {
                var detalle = exception switch
                {
                    IOException or InvalidDataException or UnauthorizedAccessException => exception.Message,
                    DbException db => $"{db.GetType().Name}, codigo {db.ErrorCode}, estado {db.SqlState}.",
                    _ => exception.GetType().Name
                };
                errores.Add($"{extractor.CodigoFuente}: {detalle}");
                logger.LogError("Ejecucion {EjecucionId}: error en {Fuente}: {Detalle}",
                    ejecucionId, extractor.CodigoFuente, detalle);
            }
            finally
            {
                totalLeidos += leidos;
                totalGuardados += guardados;
                logger.LogInformation(
                    "Ejecucion {EjecucionId}: fuente {Fuente} finalizada. Leidos {Leidos}, guardados {Guardados}, {Milisegundos} ms.",
                    ejecucionId, extractor.CodigoFuente, leidos, guardados, relojFuente.ElapsedMilliseconds);
            }

            async IAsyncEnumerable<RegistroExtraido> ContarAsync(
                [EnumeratorCancellation] CancellationToken token)
            {
                await foreach (var registro in extractor.ExtraerAsync(token).WithCancellation(token))
                {
                    leidos++;
                    yield return registro;
                }
            }
        }

        var estado = cancellationToken.IsCancellationRequested ? "Cancelada"
            : errores.Count == 0 ? "Completada"
            : fuentesCompletadas > 0 ? "Parcial"
            : "Fallida";

        // La auditoria de cierre debe completarse aunque se cancele la extraccion.
        await almacen.FinalizarAsync(ejecucionId, estado, totalLeidos,
            errores.Count > 0 ? string.Join(Environment.NewLine, errores) : null, CancellationToken.None);

        logger.LogInformation(
            "Ejecucion {EjecucionId} {Estado}. Leidos {Leidos}, guardados {Guardados}, {Milisegundos} ms.",
            ejecucionId, estado, totalLeidos, totalGuardados, reloj.ElapsedMilliseconds);
        return new ResultadoExtraccion(ejecucionId, estado, totalLeidos, totalGuardados);
    }
}

public sealed record ResultadoExtraccion(long EjecucionId, string Estado, int Leidos, int Guardados);
