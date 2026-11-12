using AnalisisOpiniones.Infrastructure;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AgregarInfraestructura(builder.Configuration);

await builder.Build().RunAsync();
