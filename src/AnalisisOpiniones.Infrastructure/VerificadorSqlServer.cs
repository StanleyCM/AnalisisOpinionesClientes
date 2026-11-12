using AnalisisOpiniones.Application;
using Microsoft.Data.SqlClient;

namespace AnalisisOpiniones.Infrastructure;

internal sealed class VerificadorSqlServer(string cadenaConexion) : IVerificadorBaseDatos
{
    private readonly string _cadenaConexion = new SqlConnectionStringBuilder(cadenaConexion).ConnectionString;

    public async Task VerificarConexionAsync(CancellationToken cancellationToken)
    {
        await using var conexion = new SqlConnection(_cadenaConexion);
        await conexion.OpenAsync(cancellationToken);
    }
}
