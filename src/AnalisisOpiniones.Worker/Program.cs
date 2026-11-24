using System.Data.Common;
using AnalisisOpiniones.Application.Dimensiones;
using AnalisisOpiniones.Application.Extraccion;
using AnalisisOpiniones.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string opcionDimensiones = "--cargar-dimensiones";
var cargarDimensiones = args.Contains(opcionDimensiones, StringComparer.OrdinalIgnoreCase);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(arg => !string.Equals(arg, opcionDimensiones, StringComparison.OrdinalIgnoreCase)).ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});

var carpetaCsv = builder.Configuration["Fuentes:Encuestas:CarpetaCsv"];
if (string.IsNullOrWhiteSpace(carpetaCsv))
{
    Console.Error.WriteLine("Falta la configuracion Fuentes:Encuestas:CarpetaCsv.");
    return 1;
}

var rutaCatalogos = Path.GetFullPath(carpetaCsv, BuscarRaizSolucion());
if (cargarDimensiones)
{
    builder.Services.AgregarCargaDimensiones(builder.Configuration, rutaCatalogos);
}
else
{
    builder.Services.AgregarInfraestructura(builder.Configuration);
    builder.Services.AgregarExtraccion(builder.Configuration, Path.Combine(rutaCatalogos, "surveys_part1.csv"));
    builder.Services.AddSingleton<EjecutarExtraccion>();
}

using var host = builder.Build();
await host.StartAsync();
var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger(cargarDimensiones ? "Dimensiones" : "Extraccion");
var detener = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
try
{
    if (cargarDimensiones)
    {
        await host.Services.GetRequiredService<CargarDimensiones>().EjecutarAsync(detener);
        return 0;
    }

    var casoDeUso = host.Services.GetRequiredService<EjecutarExtraccion>();
    var resultado = await casoDeUso.EjecutarAsync(detener);
    return resultado.Estado == "Completada" ? 0 : 1;
}
catch (OperationCanceledException) when (detener.IsCancellationRequested)
{
    logger.LogWarning(cargarDimensiones
        ? "Carga de dimensiones cancelada."
        : "Extraccion cancelada antes de registrar la ejecucion.");
    return 1;
}
catch (DbException exception)
{
    logger.LogError(cargarDimensiones
        ? "No se pudo cargar las dimensiones en SQL Server. Codigo {Codigo}, estado {Estado}."
        : "No se pudo registrar o cerrar la ejecucion en SQL Server. Codigo {Codigo}, estado {Estado}.",
        exception.ErrorCode, exception.SqlState);
    return 1;
}
catch (Exception exception) when (cargarDimensiones &&
    exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
{
    logger.LogError("No se completaron las dimensiones. {Detalle}", exception.Message);
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
