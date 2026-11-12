using AnalisisOpiniones.Api;
using AnalisisOpiniones.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AgregarInfraestructura(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<ComprobacionSqlServer>("sql_server", timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();
app.MapHealthChecks("/health");
await app.RunAsync();
