using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AnalisisOpiniones.Application.Extraccion;
using AnalisisOpiniones.Infrastructure.Extraccion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace AnalisisOpiniones.Tests;

public sealed class ExtraccionTests
{
    private const string SqlPruebas = "ETL_SQL_PRUEBAS";
    private static readonly string RutaEncuestas = EncontrarEncuestas();
    private static readonly CancellationToken SinCancelacion = CancellationToken.None;

    [CsvLocalFact]
    public async Task Csv_conserva_los_campos_originales()
    {
        using var csv = new TextFieldParser(RutaEncuestas, Encoding.UTF8) { TrimWhiteSpace = false };
        csv.SetDelimiters(",");
        var encabezados = csv.ReadFields()!;
        var leidos = 0;
        await foreach (var registro in new CsvExtractor(RutaEncuestas).ExtraerAsync(SinCancelacion))
        {
            var campos = csv.ReadFields()!;
            using var json = JsonDocument.Parse(registro.DatosOriginalesJson);
            var mapeados = new[]
            {
                registro.RegistroExternoId, registro.ClienteExternoId, registro.ProductoExternoId,
                registro.FechaOriginal, registro.ComentarioOriginal, registro.ClasificacionOriginal,
                registro.PuntajeOriginal, registro.CanalOriginal
            };
            for (var i = 0; i < encabezados.Length; i++)
            {
                Assert.True(campos[i] == mapeados[i], $"Campo mapeado {i}, fila {leidos + 2}.");
                Assert.True(campos[i] == json.RootElement.GetProperty(encabezados[i]).GetString(),
                    $"Campo JSON {i}, fila {leidos + 2}.");
            }
            leidos++;
        }

        Assert.True(csv.EndOfData);
        Assert.True(leidos > 0);
    }

    [Fact]
    public async Task Csv_inexistente_indica_la_ruta()
    {
        var ruta = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "surveys_part1.csv");
        var error = await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using var enumerador = new CsvExtractor(ruta).ExtraerAsync(SinCancelacion).GetAsyncEnumerator();
            await enumerador.MoveNextAsync();
        });
        Assert.Contains(ruta, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Csv_respeta_la_cancelacion()
    {
        var token = new CancellationToken(canceled: true);
        await using var enumerador = new CsvExtractor(RutaEncuestas).ExtraerAsync(token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerador.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task Sql_propaga_el_fallo_de_conexion()
    {
        const string cadena = "Server=tcp:127.0.0.1,1;Database=AnalisisOpinionesETL;Integrated Security=True;Connect Timeout=2;ConnectRetryCount=0";
        await using var enumerador = new DatabaseExtractor(cadena).ExtraerAsync(SinCancelacion).GetAsyncEnumerator();
        await Assert.ThrowsAsync<SqlException>(() => enumerador.MoveNextAsync().AsTask());
    }

    [SqlLocalFact]
    public async Task Staging_persiste_por_ejecucion_y_revierte_una_fuente_incompleta()
    {
        var conexionPruebas = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(SqlPruebas));
        Assert.Equal("AnalisisOpinionesETL", conexionPruebas.InitialCatalog, ignoreCase: true);
        var cadena = conexionPruebas.ConnectionString;
        var almacen = new AlmacenDePruebas(new AlmacenExtraccionSql(cadena));
        var csv = new CsvExtractor(RutaEncuestas);
        var sql = new DatabaseExtractor(cadena);
        var logger = NullLogger<EjecutarExtraccion>.Instance;

        await using var conexion = new SqlConnection(cadena);
        await conexion.OpenAsync();
        var cantidadCsv = 0;
        await foreach (var _ in csv.ExtraerAsync(SinCancelacion))
            cantidadCsv++;

        using var contar = new SqlCommand("SELECT COUNT(*) FROM origen.ResenaWeb;", conexion);
        var cantidadSql = (int)(await contar.ExecuteScalarAsync())!;
        Assert.True(cantidadCsv > 0);
        Assert.True(cantidadSql > 0);

        try
        {
            var ejecutar = new EjecutarExtraccion([csv, sql], almacen, logger);
            var primero = await ejecutar.EjecutarAsync(SinCancelacion);
            var segundo = await ejecutar.EjecutarAsync(SinCancelacion);
            Assert.NotEqual(primero.EjecucionId, segundo.EjecucionId);

            await ComprobarEjecucionAsync(conexion, primero, "Completada", cantidadCsv, cantidadSql, 0);
            await ComprobarEjecucionAsync(conexion, segundo, "Completada", cantidadCsv, cantidadSql, 0);

            var interrumpido = new EjecutarExtraccion([new CsvInterrumpido(csv), sql], almacen, logger);
            var parcial = await interrumpido.EjecutarAsync(SinCancelacion);
            await ComprobarEjecucionAsync(conexion, parcial, "Parcial", 0, cantidadSql, 1);
        }
        finally
        {
            foreach (var id in almacen.Ejecuciones)
            {
                using var limpiar = new SqlCommand("""
                    SET XACT_ABORT ON;
                    BEGIN TRANSACTION;
                    DELETE FROM staging.RegistroExtraido WHERE EjecucionId = @id;
                    DELETE FROM staging.Ejecucion WHERE EjecucionId = @id;
                    COMMIT TRANSACTION;
                    """, conexion);
                limpiar.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await limpiar.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task ComprobarEjecucionAsync(
        SqlConnection conexion, ResultadoExtraccion resultado, string estado, int csv, int sql, int errores)
    {
        var guardados = csv + sql;
        Assert.Equal(estado, resultado.Estado);
        Assert.Equal(guardados, resultado.Guardados);
        Assert.Equal(guardados + errores, resultado.Leidos);

        using var comando = new SqlCommand("""
            SELECT Estado, RegistrosLeidos, RegistrosGuardados, RegistrosConError, FechaFinUtc, MensajeError
            FROM staging.Ejecucion WHERE EjecucionId = @id;

            SELECT COUNT(CASE WHEN f.Codigo = N'ENCUESTAS' THEN 1 END),
                   COUNT(CASE WHEN f.Codigo = N'RESENAS_WEB' THEN 1 END)
            FROM staging.RegistroExtraido r JOIN config.FuenteDatos f ON r.FuenteId = f.FuenteId
            WHERE r.EjecucionId = @id AND ISJSON(r.DatosOriginalesJson) = 1;

            SELECT COUNT(*) FROM (
                SELECT ResenaId, ClienteIdOriginal, ProductoIdOriginal, CONVERT(nvarchar(10), Fecha, 23) AS Fecha,
                       Comentario, CONVERT(nvarchar(3), Rating) AS Rating
                FROM origen.ResenaWeb
                EXCEPT
                SELECT RegistroExternoId, ClienteExternoId, ProductoExternoId, FechaOriginal, ComentarioOriginal,
                       PuntajeOriginal
                FROM staging.RegistroExtraido r JOIN config.FuenteDatos f ON r.FuenteId = f.FuenteId
                WHERE r.EjecucionId = @id AND f.Codigo = N'RESENAS_WEB'
            ) AS diferencias;
            """, conexion);
        comando.Parameters.Add("@id", SqlDbType.BigInt).Value = resultado.EjecucionId;
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        Assert.Equal(estado, lector.GetString(0));
        Assert.Equal(guardados + errores, lector.GetInt32(1));
        Assert.Equal(guardados, lector.GetInt32(2));
        Assert.Equal(errores, lector.GetInt32(3));
        Assert.False(lector.IsDBNull(4));
        Assert.Equal(errores == 0, lector.IsDBNull(5));

        Assert.True(await lector.NextResultAsync());
        Assert.True(await lector.ReadAsync());
        Assert.Equal(csv, lector.GetInt32(0));
        Assert.Equal(sql, lector.GetInt32(1));

        Assert.True(await lector.NextResultAsync());
        Assert.True(await lector.ReadAsync());
        Assert.Equal(0, lector.GetInt32(0));
    }

    private static string EncontrarEncuestas()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
        {
            if (File.Exists(Path.Combine(carpeta.FullName, "AnalisisOpinionesClientes.sln")))
                return Path.Combine(carpeta.FullName, "CSV", "surveys_part1.csv");
        }

        return Path.Combine(AppContext.BaseDirectory, "CSV", "surveys_part1.csv");
    }

    private sealed class CsvLocalFactAttribute : FactAttribute
    {
        public CsvLocalFactAttribute()
        {
            if (!File.Exists(RutaEncuestas))
                Skip = "Requiere el CSV local de encuestas.";
        }
    }

    private sealed class SqlLocalFactAttribute : FactAttribute
    {
        public SqlLocalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlPruebas)) || !File.Exists(RutaEncuestas))
                Skip = "Requiere ETL_SQL_PRUEBAS y los datos originales en CSV y origen.ResenaWeb.";
        }
    }

    private sealed class CsvInterrumpido(IExtractor csv) : IExtractor
    {
        public string CodigoFuente => csv.CodigoFuente;
        public string TipoFuente => csv.TipoFuente;

        public async IAsyncEnumerable<RegistroExtraido> ExtraerAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var lector = csv.ExtraerAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            if (await lector.MoveNextAsync())
                yield return lector.Current;

            throw new InvalidDataException("Interrupcion controlada de la fuente de prueba.");
        }
    }

    private sealed class AlmacenDePruebas(IAlmacenExtraccion almacen) : IAlmacenExtraccion
    {
        public List<long> Ejecuciones { get; } = [];

        public async Task<long> IniciarAsync(CancellationToken cancellationToken)
        {
            var id = await almacen.IniciarAsync(cancellationToken);
            Ejecuciones.Add(id);
            return id;
        }

        public Task<int> GuardarFuenteAsync(long id, IExtractor extractor,
            IAsyncEnumerable<RegistroExtraido> registros, CancellationToken cancellationToken) =>
            almacen.GuardarFuenteAsync(id, extractor, registros, cancellationToken);

        public Task FinalizarAsync(long id, string estado, int leidos, string? detalle, CancellationToken cancellationToken) =>
            almacen.FinalizarAsync(id, estado, leidos, detalle, cancellationToken);
    }
}
