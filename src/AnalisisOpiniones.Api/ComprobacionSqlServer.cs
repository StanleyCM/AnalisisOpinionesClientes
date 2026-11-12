using AnalisisOpiniones.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnalisisOpiniones.Api;

internal sealed class ComprobacionSqlServer(IVerificadorBaseDatos verificador) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await verificador.VerificarConexionAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
