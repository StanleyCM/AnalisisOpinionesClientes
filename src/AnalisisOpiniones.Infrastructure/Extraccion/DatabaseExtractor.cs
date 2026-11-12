using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AnalisisOpiniones.Application.Extraccion;
using Microsoft.Data.SqlClient;

namespace AnalisisOpiniones.Infrastructure.Extraccion;

internal sealed class DatabaseExtractor(string cadenaConexion) : IExtractor
{
    public string CodigoFuente => "RESENAS_WEB";
    public string TipoFuente => "SQL";

    public async IAsyncEnumerable<RegistroExtraido> ExtraerAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var conexion = new SqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);
        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT ResenaId, ClienteIdOriginal, ProductoIdOriginal, Fecha, Comentario, Rating
            FROM origen.ResenaWeb
            ORDER BY ResenaId;
            """;

        await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
        while (await lector.ReadAsync(cancellationToken))
        {
            var id = lector.GetString(0);
            var cliente = lector.IsDBNull(1) ? null : lector.GetString(1);
            var producto = lector.IsDBNull(2) ? null : lector.GetString(2);
            var fecha = lector.IsDBNull(3) ? (DateOnly?)null : DateOnly.FromDateTime(lector.GetDateTime(3));
            var comentario = lector.IsDBNull(4) ? null : lector.GetString(4);
            var rating = lector.IsDBNull(5) ? (byte?)null : lector.GetByte(5);
            var datos = JsonSerializer.Serialize(new
            {
                ResenaId = id,
                ClienteIdOriginal = cliente,
                ProductoIdOriginal = producto,
                Fecha = fecha,
                Comentario = comentario,
                Rating = rating
            });

            yield return new RegistroExtraido(id, cliente, producto, fecha?.ToString("O", CultureInfo.InvariantCulture),
                comentario, rating?.ToString(CultureInfo.InvariantCulture), null, null, datos);
        }
    }
}
