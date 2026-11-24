using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace AnalisisOpiniones.Infrastructure.Dimensiones;

internal sealed class DatosDimensionales
{
    internal const string OrigenCatalogo = "CATALOGO_CSV";
    private readonly Dictionary<(string Origen, string Id), EntidadDimensional> _clientes = new();
    private readonly Dictionary<(string Origen, string Id), EntidadDimensional> _productos = new();

    public IEnumerable<EntidadDimensional> Clientes => _clientes.Values;
    public IEnumerable<EntidadDimensional> Productos => _productos.Values;
    public DateOnly? FechaMinima { get; private set; }
    public DateOnly? FechaMaxima { get; private set; }
    public int FechasInvalidas { get; private set; }
    public int ClientesSinId { get; private set; }
    public int ProductosSinId { get; private set; }

    public async Task LeerCatalogosAsync(string carpeta, CancellationToken cancellationToken)
    {
        await LeerCatalogoAsync(Path.Combine(carpeta, "clients.csv"), "IdCliente", "Email", 200,
            _clientes, cancellationToken);
        await LeerCatalogoAsync(Path.Combine(carpeta, "products.csv"), "IdProducto", "Categor\u00eda", 150,
            _productos, cancellationToken);
    }

    public void AgregarOpinion(string origen, string? cliente, string? producto, string? fechaOriginal)
    {
        if (!AgregarReferencia(_clientes, origen, cliente))
            ClientesSinId++;
        if (!AgregarReferencia(_productos, origen, producto))
            ProductosSinId++;

        if (!DateOnly.TryParseExact(fechaOriginal?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fecha))
        {
            FechasInvalidas++;
            return;
        }

        if (FechaMinima is null || fecha < FechaMinima)
            FechaMinima = fecha;
        if (FechaMaxima is null || fecha > FechaMaxima)
            FechaMaxima = fecha;
    }

    private static bool AgregarReferencia(
        Dictionary<(string Origen, string Id), EntidadDimensional> entidades, string origen, string? idOriginal)
    {
        var id = NormalizarId(idOriginal);
        if (id is null)
            return false;

        entidades.TryAdd((origen, id), new EntidadDimensional(origen, id, null, null));
        return true;
    }

    private static async Task LeerCatalogoAsync(
        string ruta, string campoId, string campoDetalle, int longitudDetalle,
        Dictionary<(string Origen, string Id), EntidadDimensional> entidades, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var lector = new StreamReader(archivo, new UTF8Encoding(false, true));
        using var csv = new CsvReader(lector, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectColumnCountChanges = true,
            ExceptionMessagesContainRawData = false
        });

        try
        {
            if (!await csv.ReadAsync() || !csv.ReadHeader() || csv.HeaderRecord is not { } encabezados ||
                encabezados.Distinct(StringComparer.Ordinal).Count() != encabezados.Length ||
                new[] { campoId, "Nombre", campoDetalle }.Except(encabezados, StringComparer.Ordinal).Any())
            {
                throw new InvalidDataException($"Encabezados invalidos en '{ruta}'.");
            }

            while (await csv.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = NormalizarId(csv.GetField(campoId));
                var nombre = Recortar(csv.GetField("Nombre"));
                var detalle = Recortar(csv.GetField(campoDetalle));
                if (id is null || nombre?.Length > 200 || detalle?.Length > longitudDetalle)
                {
                    throw new InvalidDataException($"Identificador o longitud invalida en '{ruta}', fila {csv.Parser.Row}.");
                }

                var fila = new EntidadDimensional(OrigenCatalogo, id, nombre, detalle);
                if (!entidades.TryAdd((OrigenCatalogo, id), fila) && entidades[(OrigenCatalogo, id)] != fila)
                {
                    throw new InvalidDataException($"Identificador duplicado con atributos distintos en '{ruta}', fila {csv.Parser.Row}.");
                }
            }
        }
        catch (Exception exception) when (exception is CsvHelperException or DecoderFallbackException)
        {
            throw new InvalidDataException($"CSV invalido en '{ruta}', cerca de la fila {csv.Parser.Row}.");
        }
    }

    private static string? NormalizarId(string? valor)
    {
        var id = Recortar(valor)?.ToUpperInvariant();
        return id?.Length <= 100 ? id : null;
    }

    private static string? Recortar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}

internal sealed record EntidadDimensional(string Origen, string Id, string? Nombre, string? Detalle);
