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

try
{
    using var archivo = File.OpenRead(rutaEncuestas);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"No se pudo abrir el CSV requerido '{rutaEncuestas}': {exception.Message}");
    return 1;
}

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Inicio")
    .LogInformation("CSV de encuestas disponible en {RutaEncuestas}", rutaEncuestas);
await host.RunAsync();
return 0;

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
