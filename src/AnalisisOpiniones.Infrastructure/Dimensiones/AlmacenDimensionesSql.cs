using System.Data;
using System.Text.Json;
using AnalisisOpiniones.Application.Dimensiones;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AnalisisOpiniones.Infrastructure.Dimensiones;

internal sealed class AlmacenDimensionesSql(
    string cadenaConexion, string carpetaCsv, ILogger<AlmacenDimensionesSql> logger) : IAlmacenDimensiones
{
    public async Task<IReadOnlyList<ResultadoDimension>> CargarAsync(CancellationToken cancellationToken)
    {
        var datos = new DatosDimensionales();
        await datos.LeerCatalogosAsync(carpetaCsv, cancellationToken);

        await using var conexion = new SqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);
        await using var transaccion = (SqlTransaction)await conexion.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        using var bloqueo = new SqlCommand("""
            DECLARE @resultado int;
            EXEC @resultado = sys.sp_getapplock @Resource = N'AnalisisOpinionesETL.CargaDimensiones',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
            SELECT @resultado;
            """, conexion, transaccion);
        if ((int)(await bloqueo.ExecuteScalarAsync(cancellationToken))! < 0)
            throw new InvalidOperationException("No se pudo obtener el bloqueo para la carga de dimensiones.");

        await LeerStagingAsync(conexion, transaccion, datos, cancellationToken);
        using var carga = new SqlCommand(CargaSql, conexion, transaccion);
        carga.Parameters.Add("@clientes", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(datos.Clientes);
        carga.Parameters.Add("@productos", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(datos.Productos);
        carga.Parameters.Add("@desde", SqlDbType.Date).Value = (object?)datos.FechaMinima ?? DBNull.Value;
        carga.Parameters.Add("@hasta", SqlDbType.Date).Value = (object?)datos.FechaMaxima ?? DBNull.Value;
        var resultados = new List<ResultadoDimension>(5);
        await using (var lector = await carga.ExecuteReaderAsync(cancellationToken))
        {
            while (await lector.ReadAsync(cancellationToken))
            {
                resultados.Add(new ResultadoDimension(lector.GetString(0), lector.GetInt32(1), lector.GetInt32(2),
                    lector.GetInt32(3)));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaccion.CommitAsync(CancellationToken.None);
        return resultados;
    }

    private async Task LeerStagingAsync(
        SqlConnection conexion, SqlTransaction transaccion, DatosDimensionales datos, CancellationToken cancellationToken)
    {
        using var comando = new SqlCommand("""
            SELECT r.EjecucionId, f.Codigo, r.ClienteExternoId, r.ProductoExternoId, r.FechaOriginal
            FROM staging.RegistroExtraido AS r
            JOIN staging.Ejecucion AS e ON e.EjecucionId = r.EjecucionId
            JOIN config.FuenteDatos AS f ON f.FuenteId = r.FuenteId
            WHERE e.Estado IN (N'Completada', N'Parcial') AND e.FechaFinUtc IS NOT NULL;
            """, conexion, transaccion);
        var conteos = new Dictionary<(long Ejecucion, string Fuente), int>();
        await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
        while (await lector.ReadAsync(cancellationToken))
        {
            var origen = lector.GetString(1);
            var grupo = (lector.GetInt64(0), origen);
            conteos[grupo] = conteos.GetValueOrDefault(grupo) + 1;
            datos.AgregarOpinion(origen, Texto(2), Texto(3), Texto(4));
        }

        foreach (var (grupo, cantidad) in conteos.OrderBy(x => x.Key))
        {
            logger.LogInformation("Staging: ejecucion {Ejecucion}, fuente {Fuente}, {Opiniones} opiniones leidas.",
                grupo.Ejecucion, grupo.Fuente, cantidad);
        }
        if (datos.FechasInvalidas + datos.ClientesSinId + datos.ProductosSinId > 0)
        {
            logger.LogWarning(
                "Referencias omitidas en staging: {Fechas} fechas invalidas o ausentes, {Clientes} IDs de cliente y {Productos} IDs de producto invalidos o ausentes.",
                datos.FechasInvalidas, datos.ClientesSinId, datos.ProductosSinId);
        }

        string? Texto(int ordinal) => lector.IsDBNull(ordinal) ? null : lector.GetString(ordinal);
    }

    private const string CargaSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        DECLARE @resultados table (Nombre nvarchar(30), Insertados int, Actualizados int, Existentes int);
        DECLARE @insertados int, @actualizados int;
        DECLARE @calendario table (Fecha date PRIMARY KEY, FechaKey int, Dia tinyint, Mes tinyint, Anio smallint, Trimestre tinyint);

        IF @desde IS NOT NULL
        BEGIN
            INSERT @calendario (Fecha, FechaKey, Dia, Mes, Anio, Trimestre)
            SELECT d.Fecha, YEAR(d.Fecha) * 10000 + MONTH(d.Fecha) * 100 + DAY(d.Fecha),
                   DAY(d.Fecha), MONTH(d.Fecha), YEAR(d.Fecha), DATEPART(QUARTER, d.Fecha)
            FROM GENERATE_SERIES(0, DATEDIFF(DAY, @desde, @hasta), 1) AS numeros
            CROSS APPLY (VALUES (DATEADD(DAY, numeros.value, @desde))) AS d(Fecha);
        END;

        UPDATE d SET Dia = c.Dia, Mes = c.Mes, Anio = c.Anio, Trimestre = c.Trimestre
        FROM dw.DimFecha AS d JOIN @calendario AS c ON c.Fecha = d.Fecha
        WHERE EXISTS (SELECT c.Dia, c.Mes, c.Anio, c.Trimestre EXCEPT SELECT d.Dia, d.Mes, d.Anio, d.Trimestre);
        SET @actualizados = @@ROWCOUNT;
        INSERT dw.DimFecha (FechaKey, Fecha, Dia, Mes, Anio, Trimestre)
        SELECT FechaKey, Fecha, Dia, Mes, Anio, Trimestre FROM @calendario AS c
        WHERE NOT EXISTS (SELECT 1 FROM dw.DimFecha AS d WHERE d.Fecha = c.Fecha);
        SET @insertados = @@ROWCOUNT;
        INSERT @resultados SELECT N'DimFecha', @insertados, @actualizados, COUNT(*) FROM dw.DimFecha;

        DECLARE @entidades table (
            Origen nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,
            Id nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
            Nombre nvarchar(200), Detalle nvarchar(200), PRIMARY KEY (Origen, Id)
        );
        INSERT @entidades SELECT Origen, Id, Nombre, Detalle FROM OPENJSON(@clientes)
        WITH (Origen nvarchar(50), Id nvarchar(100), Nombre nvarchar(200), Detalle nvarchar(200));

        UPDATE d SET Nombre = COALESCE(e.Nombre, d.Nombre), Email = COALESCE(e.Detalle, d.Email)
        FROM dw.DimCliente AS d JOIN @entidades AS e ON e.Origen = d.SistemaOrigen AND e.Id = d.ClienteIdOriginal
        WHERE EXISTS (
            SELECT COALESCE(e.Nombre, d.Nombre) COLLATE Latin1_General_100_BIN2,
                   COALESCE(e.Detalle, d.Email) COLLATE Latin1_General_100_BIN2
            EXCEPT SELECT d.Nombre COLLATE Latin1_General_100_BIN2, d.Email COLLATE Latin1_General_100_BIN2
        );
        SET @actualizados = @@ROWCOUNT;
        INSERT dw.DimCliente (SistemaOrigen, ClienteIdOriginal, Nombre, Email)
        SELECT Origen, Id, Nombre, Detalle FROM @entidades AS e
        WHERE NOT EXISTS (
            SELECT 1 FROM dw.DimCliente AS d WHERE d.SistemaOrigen = e.Origen AND d.ClienteIdOriginal = e.Id
        );
        SET @insertados = @@ROWCOUNT;
        INSERT @resultados SELECT N'DimCliente', @insertados, @actualizados, COUNT(*) FROM dw.DimCliente;

        DELETE FROM @entidades;
        INSERT @entidades SELECT Origen, Id, Nombre, Detalle FROM OPENJSON(@productos)
        WITH (Origen nvarchar(50), Id nvarchar(100), Nombre nvarchar(200), Detalle nvarchar(150));

        UPDATE d SET Nombre = COALESCE(e.Nombre, d.Nombre), Categoria = COALESCE(e.Detalle, d.Categoria)
        FROM dw.DimProducto AS d JOIN @entidades AS e ON e.Origen = d.SistemaOrigen AND e.Id = d.ProductoIdOriginal
        WHERE EXISTS (
            SELECT COALESCE(e.Nombre, d.Nombre) COLLATE Latin1_General_100_BIN2,
                   COALESCE(e.Detalle, d.Categoria) COLLATE Latin1_General_100_BIN2
            EXCEPT SELECT d.Nombre COLLATE Latin1_General_100_BIN2, d.Categoria COLLATE Latin1_General_100_BIN2
        );
        SET @actualizados = @@ROWCOUNT;
        INSERT dw.DimProducto (SistemaOrigen, ProductoIdOriginal, Nombre, Categoria)
        SELECT Origen, Id, Nombre, Detalle FROM @entidades AS e
        WHERE NOT EXISTS (
            SELECT 1 FROM dw.DimProducto AS d WHERE d.SistemaOrigen = e.Origen AND d.ProductoIdOriginal = e.Id
        );
        SET @insertados = @@ROWCOUNT;
        INSERT @resultados SELECT N'DimProducto', @insertados, @actualizados, COUNT(*) FROM dw.DimProducto;

        UPDATE d SET NombreFuente = f.Nombre, TipoFuente = f.Tipo
        FROM dw.DimFuente AS d JOIN config.FuenteDatos AS f ON f.Codigo = d.CodigoFuente
        WHERE EXISTS (
            SELECT f.Nombre COLLATE Latin1_General_100_BIN2, f.Tipo COLLATE Latin1_General_100_BIN2
            EXCEPT SELECT d.NombreFuente COLLATE Latin1_General_100_BIN2, d.TipoFuente COLLATE Latin1_General_100_BIN2
        );
        SET @actualizados = @@ROWCOUNT;
        INSERT dw.DimFuente (CodigoFuente, NombreFuente, TipoFuente)
        SELECT Codigo, Nombre, Tipo FROM config.FuenteDatos AS f
        WHERE NOT EXISTS (SELECT 1 FROM dw.DimFuente AS d WHERE d.CodigoFuente = f.Codigo);
        SET @insertados = @@ROWCOUNT;
        INSERT @resultados SELECT N'DimFuente', @insertados, @actualizados, COUNT(*) FROM dw.DimFuente;

        INSERT dw.DimClasificacion (Nombre)
        SELECT c.Nombre FROM (VALUES (N'Positiva'), (N'Negativa'), (N'Neutra')) AS c(Nombre)
        WHERE NOT EXISTS (SELECT 1 FROM dw.DimClasificacion AS d WHERE d.Nombre = c.Nombre);
        SET @insertados = @@ROWCOUNT;
        INSERT @resultados SELECT N'DimClasificacion', @insertados, 0, COUNT(*) FROM dw.DimClasificacion;

        SELECT Nombre, Insertados, Actualizados, Existentes FROM @resultados;
        """;
}
