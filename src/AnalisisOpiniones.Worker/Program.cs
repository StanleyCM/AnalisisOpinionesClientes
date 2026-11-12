using System.Data.Common;
using AnalisisOpiniones.Application.Extraccion;
using AnalisisOpiniones.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AgregarInfraestructura(builder.Configuration);

var carpetaCsv = builder.Configuration["Fuentes:Encuestas:CarpetaCsv"];
if (string.IsNullOrWhiteSpace(carpetaCsv))
{
    Console.Error.WriteLine("Falta la configuracion Fuentes:Encuestas:CarpetaCsv.");
    return 1;
}

var rutaEncuestas = Path.GetFullPath(
    Path.Combine(carpetaCsv, "surveys_part1.csv"), BuscarRaizSolucion());

builder.Services.AgregarExtraccion(builder.Configuration, rutaEncuestas);
builder.Services.AddSingleton<EjecutarExtraccion>();

using var host = builder.Build();
await host.StartAsync();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Extraccion");
var detener = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
try
{
    var casoDeUso = host.Services.GetRequiredService<EjecutarExtraccion>();
    var resultado = await casoDeUso.EjecutarAsync(detener);
    return resultado.Estado == "Completada" ? 0 : 1;
}
catch (OperationCanceledException) when (detener.IsCancellationRequested)
{
    logger.LogWarning("Extraccion cancelada antes de registrar la ejecucion.");
    return 1;
}
catch (DbException exception)
{
    logger.LogError("No se pudo registrar o cerrar la ejecucion en SQL Server. Codigo {Codigo}, estado {Estado}.",
        exception.ErrorCode, exception.SqlState);
    return 1;
}
finally
{
    await host.StopAsync();
}

static string BuscarRaizSolucion()
{
    for (var directorio = new DirectoryInfo(AppContext.BaseDirectory);
         directorio is not null;
         directorio = directorio.Parent)
    {
        if (File.Exists(Path.Combine(directorio.FullName, "AnalisisOpinionesClientes.sln")))
        {
            return directorio.FullName;
        }
    }

    return AppContext.BaseDirectory;
}
