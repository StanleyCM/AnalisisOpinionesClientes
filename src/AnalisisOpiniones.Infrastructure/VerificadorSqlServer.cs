using AnalisisOpiniones.Application;
using Microsoft.Data.SqlClient;

namespace AnalisisOpiniones.Infrastructure;

internal sealed class VerificadorSqlServer(string cadenaConexion) : IVerificadorBaseDatos
{
    public async Task VerificarConexionAsync(CancellationToken cancellationToken)
    {
        await using var conexion = new SqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);

        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT TOP (1) tabla.Nombre
            FROM (VALUES
                (N'config.FuenteDatos'),
                (N'origen.ResenaWeb'),
                (N'staging.Ejecucion'),
                (N'staging.RegistroExtraido'),
                (N'dw.DimFecha'),
                (N'dw.DimProducto'),
                (N'dw.DimCliente'),
                (N'dw.DimFuente'),
                (N'dw.DimClasificacion'),
                (N'dw.FactOpinion')
            ) AS tabla(Nombre)
            WHERE OBJECT_ID(tabla.Nombre, N'U') IS NULL
            ORDER BY tabla.Nombre;
            """;

        if (await comando.ExecuteScalarAsync(cancellationToken) is string tablaNoDisponible)
        {
            throw new InvalidOperationException(
                $"La tabla {tablaNoDisponible} no esta disponible en {conexion.Database}.");
        }
    }
}
