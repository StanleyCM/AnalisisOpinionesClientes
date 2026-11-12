using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AnalisisOpiniones.Application.Extraccion;
using CsvHelper;
using CsvHelper.Configuration;

namespace AnalisisOpiniones.Infrastructure.Extraccion;

internal sealed class CsvExtractor(string rutaEncuestas) : IExtractor
{
    private const string Clasificacion = "Clasificaci\u00f3n";
    private const string Puntaje = "PuntajeSatisfacci\u00f3n";
    private static readonly string[] Columnas =
    [
        "IdOpinion", "IdCliente", "IdProducto", "Fecha", "Comentario",
        Clasificacion, Puntaje, "Fuente"
    ];

    public string CodigoFuente => "ENCUESTAS";
    public string TipoFuente => "CSV";

    public async IAsyncEnumerable<RegistroExtraido> ExtraerAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var archivo = new FileStream(rutaEncuestas, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var lector = new StreamReader(archivo, new UTF8Encoding(false, true));
        using var parser = new CsvParser(lector, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            ExceptionMessagesContainRawData = false
        });

        var encabezados = await LeerFilaAsync(parser, cancellationToken);
        if (encabezados is null || encabezados.Distinct(StringComparer.Ordinal).Count() != encabezados.Length ||
            Columnas.Except(encabezados, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException($"El CSV '{rutaEncuestas}' no tiene los encabezados de encuestas esperados.");
        }

        while (await LeerFilaAsync(parser, cancellationToken) is { } campos)
        {
            if (campos.Length != encabezados.Length)
            {
                throw new InvalidDataException(
                    $"Numero de campos incorrecto en la fila {parser.Row} del CSV '{rutaEncuestas}'.");
            }

            var datos = new Dictionary<string, string>(encabezados.Length, StringComparer.Ordinal);
            for (var i = 0; i < encabezados.Length; i++)
            {
                datos.Add(encabezados[i], campos[i]);
            }

            yield return new RegistroExtraido(
                RegistroExternoId: datos["IdOpinion"],
                ClienteExternoId: datos["IdCliente"],
                ProductoExternoId: datos["IdProducto"],
                FechaOriginal: datos["Fecha"],
                ComentarioOriginal: datos["Comentario"],
                PuntajeOriginal: datos[Puntaje],
                ClasificacionOriginal: datos[Clasificacion],
                CanalOriginal: datos["Fuente"],
                DatosOriginalesJson: JsonSerializer.Serialize(datos));
        }
    }

    private async Task<string[]?> LeerFilaAsync(CsvParser parser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var hayFila = await parser.ReadAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return hayFila ? parser.Record : null;
        }
        catch (Exception exception) when (exception is CsvHelperException or DecoderFallbackException)
        {
            throw new InvalidDataException($"CSV '{rutaEncuestas}' invalido cerca de la fila {parser.Row}.", exception);
        }
    }
}
