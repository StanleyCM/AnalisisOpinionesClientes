using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnalisisOpiniones.Application.Extraccion;
using AnalisisOpiniones.Infrastructure;
using AnalisisOpiniones.Infrastructure.Extraccion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using Xunit;
using Xunit.Abstractions;

namespace AnalisisOpiniones.Tests;

public sealed class ApiExtractorTests(ITestOutputHelper salida)
{
    private const string SqlPruebas = "ETL_SQL_PRUEBAS";
    private const string Seccion = "Fuentes:ComentariosSociales:";
    private static readonly string Raiz = EncontrarRaiz();
    private static readonly string RutaSocial = Path.Combine(Raiz, "CSV", "social_comments.csv");

    [Theory]
    [InlineData(503, "", "HTTP 503")]
    [InlineData(200, "{", "JSON invalido")]
    [InlineData(200, """{"items":[],"next":"/comentarios"}""", "paginacion repetido")]
    [InlineData(200, """{"items":[],"next":"//otro-origen.invalid/comentarios"}""", "otro origen")]
    public async Task Api_rechaza_respuestas_invalidas_sin_repetir_solicitudes(int estado, string json, string mensaje)
    {
        var solicitudes = 0;
        await using var servidor = await IniciarServidorAsync(async contexto =>
        {
            Interlocked.Increment(ref solicitudes);
            contexto.Response.StatusCode = estado;
            await contexto.Response.WriteAsync(json);
        });
        using var servicios = CrearServicios(servidor.Urls.Single(), "/comentarios");
        await using var filas = ObtenerExtractor(servicios).ExtraerAsync(CancellationToken.None).GetAsyncEnumerator();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => filas.MoveNextAsync().AsTask());
        Assert.Contains(mensaje, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, solicitudes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Api_aplica_cancelacion_y_timeout_durante_el_cuerpo(bool cancelar)
    {
        var recibido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var servidor = await IniciarServidorAsync(async contexto =>
        {
            await contexto.Response.StartAsync(contexto.RequestAborted);
            recibido.TrySetResult();
            await liberar.Task.WaitAsync(contexto.RequestAborted);
        });
        using var servicios = CrearServicios(servidor.Urls.Single(), "/comentarios", cancelar ? "10" : "1");
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var filas = ObtenerExtractor(servicios).ExtraerAsync(cancelacion.Token).GetAsyncEnumerator();
        try
        {
            var lectura = filas.MoveNextAsync().AsTask();
            await recibido.Task.WaitAsync(cancelacion.Token);
            if (cancelar)
            {
                await cancelacion.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lectura);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => lectura);
                Assert.Contains("tiempo de espera", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            liberar.TrySetResult();
        }
    }

    [Theory]
    [InlineData(null, "/comentarios", "BaseUrl")]
    [InlineData("http://127.0.0.1", null, "Endpoint")]
    public async Task Api_informa_si_falta_configuracion(string? url, string? endpoint, string clave)
    {
        using var servicios = CrearServicios(url, endpoint);
        await using var filas = ObtenerExtractor(servicios).ExtraerAsync(CancellationToken.None).GetAsyncEnumerator();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => filas.MoveNextAsync().AsTask());
        Assert.Contains(Seccion + clave, error.Message, StringComparison.Ordinal);
    }

    [SqlWorkerFact]
    public async Task Worker_persiste_las_tres_fuentes_y_revierte_la_api_si_falla_otra_pagina()
    {
        var cadena = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(SqlPruebas));
        Assert.Equal("AnalisisOpinionesETL", cadena.InitialCatalog, ignoreCase: true);
        await using var conexion = new SqlConnection(cadena.ConnectionString);
        await conexion.OpenAsync();

        var originales = LeerComentarios();
        Assert.Equal(200, originales.Count);
        var mitad = originales.Count / 2;
        var primera = JsonSerializer.Serialize(new { items = originales.Take(mitad), next = "?pagina=2" });
        var segunda = JsonSerializer.Serialize(new
        {
            items = originales.Take(1).Concat(originales.Skip(mitad)),
            next = (string?)null
        });
        var token = Guid.NewGuid().ToString("N");
        var solicitudes = new ConcurrentQueue<string>();
        await using var servidor = await IniciarServidorAsync(async contexto =>
        {
            if (!string.Equals(contexto.Request.Headers.Authorization.ToString(), $"Bearer {token}", StringComparison.Ordinal))
            {
                contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var pagina = contexto.Request.Query["pagina"].ToString();
            solicitudes.Enqueue(pagina);
            if (pagina == "2" && contexto.Request.Path == "/comentarios-con-error")
            {
                contexto.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            contexto.Response.ContentType = "application/json";
            await contexto.Response.WriteAsync(pagina == "2" ? segunda : primera, contexto.RequestAborted);
        });

        await VerificarCasoAsync(false);
        await VerificarCasoAsync(true);

        async Task VerificarCasoAsync(bool parcial)
        {
            var inicio = solicitudes.Count;
            var endpoint = parcial ? "/comentarios-con-error" : "/comentarios";
            var (codigo, registro) = await EjecutarWorkerAsync(cadena.ConnectionString, servidor.Urls.Single(), endpoint, token);
            var coincidencia = Regex.Match(registro, @"Ejecucion ([0-9]+) registrada\.");
            Assert.True(coincidencia.Success, registro);
            var id = long.Parse(coincidencia.Groups[1].Value);
            Assert.True(id > 0 && id != 6);
            try
            {
                Assert.True(codigo == (parcial ? 1 : 0), registro);
                Assert.Equal(new[] { "", "2" }, solicitudes.Skip(inicio));
                await ComprobarStagingAsync(conexion, id, originales, parcial);
                salida.WriteLine(parcial
                    ? "Worker ejecucion {0}: Parcial, CSV 500, SQL 200, API 0, 100 registros REST revertidos."
                    : "Worker ejecucion {0}: Completada, CSV 500, SQL 200, API 200, total 900.", id);
            }
            finally
            {
                await ExtraccionTests.LimpiarEjecucionAsync(conexion, id);
            }
        }
    }

    private static async Task ComprobarStagingAsync(
        SqlConnection conexion, long id, List<Dictionary<string, string>> originales, bool parcial)
    {
        using var comando = new SqlCommand("""
            SELECT Estado, RegistrosLeidos, RegistrosGuardados, RegistrosConError, MensajeError
            FROM staging.Ejecucion WHERE EjecucionId = @id;

            SELECT f.Codigo, COUNT(*)
            FROM staging.RegistroExtraido r JOIN config.FuenteDatos f ON r.FuenteId = f.FuenteId
            WHERE r.EjecucionId = @id GROUP BY f.Codigo ORDER BY f.Codigo;

            SELECT r.RegistroExternoId, r.ClienteExternoId, r.ProductoExternoId, r.FechaOriginal, r.ComentarioOriginal,
                   r.CanalOriginal, r.DatosOriginalesJson
            FROM staging.RegistroExtraido r JOIN config.FuenteDatos f ON r.FuenteId = f.FuenteId
            WHERE r.EjecucionId = @id AND f.Codigo = N'REDES_SOCIALES';
            """, conexion);
        comando.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        Assert.Equal(parcial ? "Parcial" : "Completada", lector.GetString(0));
        Assert.Equal(parcial ? 800 : 900, lector.GetInt32(1));
        Assert.Equal(parcial ? 700 : 900, lector.GetInt32(2));
        Assert.Equal(parcial ? 100 : 0, lector.GetInt32(3));
        if (parcial)
            Assert.Contains("HTTP 503", lector.GetString(4), StringComparison.Ordinal);
        else
            Assert.True(lector.IsDBNull(4));

        Assert.True(await lector.NextResultAsync());
        var conteos = new List<(string Fuente, int Cantidad)>();
        while (await lector.ReadAsync())
            conteos.Add((lector.GetString(0), lector.GetInt32(1)));
        Assert.Equal(parcial ? [("ENCUESTAS", 500), ("RESENAS_WEB", 200)]
            : new[] { ("ENCUESTAS", 500), ("REDES_SOCIALES", 200), ("RESENAS_WEB", 200) }, conteos);

        Assert.True(await lector.NextResultAsync());
        var esperados = originales.ToDictionary(c => c["IdComment"], StringComparer.Ordinal);
        var campos = new[] { "IdComment", "IdCliente", "IdProducto", "Fecha", "Comentario", "Fuente" };
        var comparados = 0;
        while (await lector.ReadAsync())
        {
            Assert.True(esperados.Remove(lector.GetString(0), out var original));
            for (var i = 0; i < campos.Length; i++)
                Assert.True(original[campos[i]] == lector.GetString(i), $"El campo {campos[i]} difiere.");
            Assert.True(JsonSerializer.Serialize(original) == lector.GetString(6), "El JSON original difiere.");
            comparados++;
        }
        Assert.Equal(parcial ? 0 : originales.Count, comparados);
    }

    private static List<Dictionary<string, string>> LeerComentarios()
    {
        using var csv = new TextFieldParser(RutaSocial) { TrimWhiteSpace = false };
        csv.SetDelimiters(",");
        var encabezados = csv.ReadFields()!;
        var filas = new List<Dictionary<string, string>>();
        while (csv.ReadFields() is { } campos)
            filas.Add(encabezados.Zip(campos).ToDictionary(c => c.First, c => c.Second, StringComparer.Ordinal));
        return filas;
    }

    private static async Task<(int Codigo, string Salida)> EjecutarWorkerAsync(
        string cadena, string url, string endpoint, string token)
    {
        var configuracion = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var worker = Path.Combine(Raiz, "src", "AnalisisOpiniones.Worker", "bin", configuracion, "net8.0",
            "AnalisisOpiniones.Worker.dll");
        Assert.True(File.Exists(worker), "Falta la compilacion del Worker.");
        var inicio = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Raiz,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        inicio.ArgumentList.Add(worker);
        inicio.Environment["ConnectionStrings__AnalisisOpinionesETL"] = cadena;
        inicio.Environment["Fuentes__Encuestas__CarpetaCsv"] = "CSV";
        inicio.Environment["Fuentes__ComentariosSociales__BaseUrl"] = url;
        inicio.Environment["Fuentes__ComentariosSociales__Endpoint"] = endpoint;
        inicio.Environment["Fuentes__ComentariosSociales__TokenBearer"] = token;
        inicio.Environment["Fuentes__ComentariosSociales__TimeoutSegundos"] = "10";
        inicio.Environment["Fuentes__ComentariosSociales__MaxPaginas"] = "2";
        inicio.Environment["Logging__LogLevel__Default"] = "Information";
        using var proceso = Process.Start(inicio)!;
        var stdout = proceso.StandardOutput.ReadToEndAsync();
        var stderr = proceso.StandardError.ReadToEndAsync();
        using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            await proceso.WaitForExitAsync(plazo.Token);
        }
        catch (OperationCanceledException)
        {
            if (!proceso.HasExited)
                proceso.Kill(entireProcessTree: true);
            await proceso.WaitForExitAsync();
        }
        return (proceso.ExitCode, await stdout + await stderr);
    }

    private static async Task<WebApplication> IniciarServidorAsync(RequestDelegate responder)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(opciones => opciones.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(responder);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static ServiceProvider CrearServicios(string? url, string? endpoint, string timeout = "30")
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AnalisisOpinionesETL"] = "Database=AnalisisOpinionesETL",
            [Seccion + "BaseUrl"] = url,
            [Seccion + "Endpoint"] = endpoint,
            [Seccion + "TimeoutSegundos"] = timeout
        }).Build();
        return new ServiceCollection().AgregarInfraestructura(configuracion)
            .AgregarExtraccion(configuracion, "").BuildServiceProvider();
    }

    private static ApiExtractor ObtenerExtractor(ServiceProvider servicios) =>
        servicios.GetServices<IExtractor>().OfType<ApiExtractor>().Single();

    private static string EncontrarRaiz()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
        {
            if (File.Exists(Path.Combine(carpeta.FullName, "AnalisisOpinionesClientes.sln")))
                return carpeta.FullName;
        }
        return AppContext.BaseDirectory;
    }

    private sealed class SqlWorkerFactAttribute : FactAttribute
    {
        public SqlWorkerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlPruebas)) ||
                !File.Exists(RutaSocial) || !File.Exists(Path.Combine(Raiz, "CSV", "surveys_part1.csv")))
            {
                Skip = "Requiere ETL_SQL_PRUEBAS y los CSV originales de encuestas y comentarios sociales.";
            }
        }
    }
}
