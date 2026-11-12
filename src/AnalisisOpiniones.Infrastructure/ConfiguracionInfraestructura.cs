using AnalisisOpiniones.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AnalisisOpiniones.Infrastructure;

public static class ConfiguracionInfraestructura
{
    public static IServiceCollection AgregarInfraestructura(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var cadenaConexion = configuration.GetConnectionString("AnalisisOpinionesClientes");
        if (string.IsNullOrWhiteSpace(cadenaConexion))
        {
            throw new InvalidOperationException(
                "Falta la configuracion ConnectionStrings:AnalisisOpinionesClientes.");
        }

        services.AddSingleton<IVerificadorBaseDatos>(new VerificadorSqlServer(cadenaConexion));

        var url = configuration["Fuentes:ComentariosSociales:BaseUrl"];
        Uri? direccionBase = null;
        if (!string.IsNullOrWhiteSpace(url) &&
            (!Uri.TryCreate(url, UriKind.Absolute, out direccionBase) ||
             direccionBase.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException(
                "Fuentes:ComentariosSociales:BaseUrl debe ser una URL absoluta HTTP o HTTPS.");
        }

        services.AddHttpClient("ComentariosSociales", client => client.BaseAddress = direccionBase);

        return services;
    }
}
