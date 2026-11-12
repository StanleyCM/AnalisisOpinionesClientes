using System.Data;
using AnalisisOpiniones.Application.Extraccion;
using Microsoft.Data.SqlClient;

namespace AnalisisOpiniones.Infrastructure.Extraccion;

internal sealed class AlmacenExtraccionSql(string cadenaConexion) : IAlmacenExtraccion
{
    public async Task<long> IniciarAsync(CancellationToken cancellationToken)
    {
        await using var conexion = new SqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);
        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO staging.Ejecucion (Estado)
            OUTPUT inserted.EjecucionId
            VALUES (N'Iniciada');
            """;
        cancellationToken.ThrowIfCancellationRequested();
        return (long)(await comando.ExecuteScalarAsync(CancellationToken.None))!;
    }

    public async Task<int> GuardarFuenteAsync(
        long ejecucionId,
        IExtractor extractor,
        IAsyncEnumerable<RegistroExtraido> registros,
        CancellationToken cancellationToken)
    {
        await using var conexion = new SqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);
        await using var transaccion = (SqlTransaction)await conexion.BeginTransactionAsync(cancellationToken);

        using var fuente = new SqlCommand("""
            SELECT FuenteId FROM config.FuenteDatos WHERE Codigo = @codigo AND Tipo = @tipo AND Activa = 1;
            """, conexion, transaccion);
        fuente.Parameters.Add("@codigo", SqlDbType.NVarChar, 50).Value = extractor.CodigoFuente;
        fuente.Parameters.Add("@tipo", SqlDbType.NVarChar, 20).Value = extractor.TipoFuente;
        if (await fuente.ExecuteScalarAsync(cancellationToken) is not int fuenteId)
        {
            throw new InvalidDataException($"La fuente {extractor.CodigoFuente} de tipo {extractor.TipoFuente} no esta activa o registrada.");
        }

        using var insertar = new SqlCommand("""
            INSERT INTO staging.RegistroExtraido
                (EjecucionId, FuenteId, RegistroExternoId, ClienteExternoId, ProductoExternoId,
                 FechaOriginal, ComentarioOriginal, PuntajeOriginal, ClasificacionOriginal, CanalOriginal,
                 DatosOriginalesJson)
            VALUES
                (@ejecucion, @fuente, @externo, @cliente, @producto, @fecha, @comentario, @puntaje,
                 @clasificacion, @canal, @json);
            """, conexion, transaccion);
        insertar.Parameters.Add("@ejecucion", SqlDbType.BigInt).Value = ejecucionId;
        insertar.Parameters.Add("@fuente", SqlDbType.Int).Value = fuenteId;
        var externo = insertar.Parameters.Add("@externo", SqlDbType.NVarChar, -1);
        var cliente = insertar.Parameters.Add("@cliente", SqlDbType.NVarChar, -1);
        var producto = insertar.Parameters.Add("@producto", SqlDbType.NVarChar, -1);
        var fecha = insertar.Parameters.Add("@fecha", SqlDbType.NVarChar, -1);
        var comentario = insertar.Parameters.Add("@comentario", SqlDbType.NVarChar, -1);
        var puntaje = insertar.Parameters.Add("@puntaje", SqlDbType.NVarChar, -1);
        var clasificacion = insertar.Parameters.Add("@clasificacion", SqlDbType.NVarChar, -1);
        var canal = insertar.Parameters.Add("@canal", SqlDbType.NVarChar, -1);
        var json = insertar.Parameters.Add("@json", SqlDbType.NVarChar, -1);

        var guardados = 0;
        await foreach (var registro in registros.WithCancellation(cancellationToken))
        {
            externo.Value = (object?)registro.RegistroExternoId ?? DBNull.Value;
            cliente.Value = (object?)registro.ClienteExternoId ?? DBNull.Value;
            producto.Value = (object?)registro.ProductoExternoId ?? DBNull.Value;
            fecha.Value = (object?)registro.FechaOriginal ?? DBNull.Value;
            comentario.Value = (object?)registro.ComentarioOriginal ?? DBNull.Value;
            puntaje.Value = (object?)registro.PuntajeOriginal ?? DBNull.Value;
            clasificacion.Value = (object?)registro.ClasificacionOriginal ?? DBNull.Value;
            canal.Value = (object?)registro.CanalOriginal ?? DBNull.Value;
            json.Value = registro.DatosOriginalesJson;
            await insertar.ExecuteNonQueryAsync(cancellationToken);
            guardados++;
        }

        // El contador confirmado se guarda junto con las filas de la fuente.
        using var actualizar = new SqlCommand("""
            UPDATE staging.Ejecucion
            SET RegistrosLeidos = RegistrosLeidos + @guardados,
                RegistrosGuardados = RegistrosGuardados + @guardados
            WHERE EjecucionId = @ejecucion;
            """, conexion, transaccion);
        actualizar.Parameters.Add("@ejecucion", SqlDbType.BigInt).Value = ejecucionId;
        actualizar.Parameters.Add("@guardados", SqlDbType.Int).Value = guardados;
        await actualizar.ExecuteNonQueryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaccion.CommitAsync(CancellationToken.None);
        return guardados;
    }

    public async Task FinalizarAsync(
        long ejecucionId,
        string estado,
        int leidos,
        string? detalle,
        CancellationToken cancellationToken)
    {
        await using var conexion = new SqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);
        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            UPDATE staging.Ejecucion
            SET FechaFinUtc = SYSUTCDATETIME(), Estado = @estado, RegistrosLeidos = @leidos,
                RegistrosConError = @leidos - RegistrosGuardados, MensajeError = @detalle
            WHERE EjecucionId = @ejecucion;
            """;
        comando.Parameters.Add("@ejecucion", SqlDbType.BigInt).Value = ejecucionId;
        comando.Parameters.Add("@estado", SqlDbType.NVarChar, 30).Value = estado;
        comando.Parameters.Add("@leidos", SqlDbType.Int).Value = leidos;
        comando.Parameters.Add("@detalle", SqlDbType.NVarChar, -1).Value = (object?)detalle ?? DBNull.Value;
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }
}
