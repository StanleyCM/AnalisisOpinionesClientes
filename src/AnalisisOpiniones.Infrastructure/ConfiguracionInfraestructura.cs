using AnalisisOpiniones.Application;
using AnalisisOpiniones.Application.Extraccion;
using AnalisisOpiniones.Infrastructure.Extraccion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AnalisisOpiniones.Infrastructure;

public static class ConfiguracionInfraestructura
{
    public static IServiceCollection AgregarInfraestructura(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IVerificadorBaseDatos>(new VerificadorSqlServer(ObtenerCadenaConexion(configuration)));

        services.AddHttpClient(ApiExtractor.NombreCliente, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            .RemoveAllLoggers();

        return services;
    }

    public static IServiceCollection AgregarExtraccion(
        this IServiceCollection services,
        IConfiguration configuration,
        string rutaEncuestas)
    {
        var cadenaConexion = ObtenerCadenaConexion(configuration);
        services.AddSingleton<IExtractor>(new CsvExtractor(rutaEncuestas));
        services.AddSingleton<IExtractor>(new DatabaseExtractor(cadenaConexion));
        services.AddSingleton<IExtractor>(provider => new ApiExtractor(
            provider.GetRequiredService<IHttpClientFactory>(),
            configuration.GetSection("Fuentes:ComentariosSociales"),
            provider.GetRequiredService<ILogger<ApiExtractor>>()));
        services.AddSingleton<IAlmacenExtraccion>(new AlmacenExtraccionSql(cadenaConexion));
        return services;
    }

    private static string ObtenerCadenaConexion(IConfiguration configuration)
    {
        const string nombreBaseDatos = "AnalisisOpinionesETL";
        var cadenaConexion = configuration.GetConnectionString(nombreBaseDatos);
        if (string.IsNullOrWhiteSpace(cadenaConexion))
        {
            throw new InvalidOperationException(
                $"Falta la configuracion ConnectionStrings:{nombreBaseDatos}.");
        }

        var conexionSql = new SqlConnectionStringBuilder(cadenaConexion);
        if (!string.Equals(conexionSql.InitialCatalog, nombreBaseDatos, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{nombreBaseDatos} debe indicar Database={nombreBaseDatos}.");
        }

        return conexionSql.ConnectionString;
    }
}
