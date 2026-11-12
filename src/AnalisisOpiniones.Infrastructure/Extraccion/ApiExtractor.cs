using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AnalisisOpiniones.Application.Extraccion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AnalisisOpiniones.Infrastructure.Extraccion;

internal sealed class ApiExtractor(
    IHttpClientFactory clientes,
    IConfigurationSection configuracion,
    ILogger<ApiExtractor> logger) : IExtractor
{
    internal const string NombreCliente = "ComentariosSociales";

    public string CodigoFuente => "REDES_SOCIALES";
    public string TipoFuente => "API";

    public async IAsyncEnumerable<RegistroExtraido> ExtraerAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(configuracion["BaseUrl"], UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme is not ("http" or "https") || baseUrl.UserInfo.Length != 0 || baseUrl.Fragment.Length != 0)
        {
            throw new InvalidDataException("Fuentes:ComentariosSociales:BaseUrl requiere una URL absoluta HTTP o HTTPS sin credenciales ni fragmento.");
        }

        var endpoint = ResolverUrl(baseUrl, baseUrl, configuracion["Endpoint"],
            "Fuentes:ComentariosSociales:Endpoint requiere una ruta o URL valida del mismo origen que BaseUrl.");
        var timeout = TimeSpan.FromSeconds(LeerEnteroPositivo("TimeoutSegundos", 30, (int)(uint.MaxValue / 1000)));
        var maxPaginas = LeerEnteroPositivo("MaxPaginas", 100);
        var tokenBearer = configuracion["TokenBearer"];
        var urlsVisitadas = new HashSet<Uri>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var cliente = clientes.CreateClient(NombreCliente);

        for (Uri? url = endpoint; url is not null;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!urlsVisitadas.Add(url))
                throw new InvalidDataException("REDES_SOCIALES devolvio un enlace de paginacion repetido.");
            if (urlsVisitadas.Count > maxPaginas)
                throw new InvalidDataException("REDES_SOCIALES excedio el limite Fuentes:ComentariosSociales:MaxPaginas.");

            var pagina = urlsVisitadas.Count;
            using var documento = await DescargarPaginaAsync(cliente, url, tokenBearer, timeout, pagina, cancellationToken);
            var raiz = documento.RootElement;
            var elementos = raiz.ValueKind switch
            {
                JsonValueKind.Array => raiz,
                JsonValueKind.Object when raiz.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array => items,
                _ => throw new InvalidDataException($"REDES_SOCIALES requiere un arreglo JSON o un arreglo 'items' en la pagina {pagina}.")
            };
            var duplicados = 0;
            foreach (var elemento in elementos.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elemento.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"REDES_SOCIALES devolvio un comentario que no es un objeto en la pagina {pagina}.");

                var id = LeerCampo(elemento, "IdComment");
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidDataException($"REDES_SOCIALES devolvio un comentario sin IdComment en la pagina {pagina}.");
                if (!ids.Add(id))
                {
                    duplicados++;
                    continue;
                }

                yield return new RegistroExtraido(
                    RegistroExternoId: id,
                    ClienteExternoId: LeerCampo(elemento, "IdCliente"),
                    ProductoExternoId: LeerCampo(elemento, "IdProducto"),
                    FechaOriginal: LeerCampo(elemento, "Fecha"),
                    ComentarioOriginal: LeerCampo(elemento, "Comentario"),
                    PuntajeOriginal: null,
                    ClasificacionOriginal: null,
                    CanalOriginal: LeerCampo(elemento, "Fuente"),
                    DatosOriginalesJson: elemento.GetRawText());
            }

            logger.LogDebug("REDES_SOCIALES proceso la pagina {Pagina} con {Registros} comentarios y {Duplicados} duplicados.",
                pagina, elementos.GetArrayLength(), duplicados);
            url = raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("next", out var siguiente) &&
                siguiente.ValueKind != JsonValueKind.Null
                ? ResolverUrl(baseUrl, url, siguiente.ValueKind == JsonValueKind.String ? siguiente.GetString() : null,
                    "REDES_SOCIALES devolvio un enlace 'next' invalido o de otro origen.")
                : null;
        }
    }

    private static async Task<JsonDocument> DescargarPaginaAsync(
        HttpClient cliente, Uri url, string? tokenBearer, TimeSpan timeout, int pagina, CancellationToken cancellationToken)
    {
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        plazo.CancelAfter(timeout);
        try
        {
            using var solicitud = new HttpRequestMessage(HttpMethod.Get, url);
            solicitud.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(tokenBearer))
            {
                if (!AuthenticationHeaderValue.TryParse($"Bearer {tokenBearer}", out var autorizacion))
                    throw new InvalidDataException("Fuentes:ComentariosSociales:TokenBearer no es valido.");
                solicitud.Headers.Authorization = autorizacion;
            }

            using var respuesta = await cliente.SendAsync(solicitud, HttpCompletionOption.ResponseHeadersRead, plazo.Token);
            if (!respuesta.IsSuccessStatusCode)
                throw new InvalidDataException($"REDES_SOCIALES devolvio HTTP {(int)respuesta.StatusCode} en la pagina {pagina}.");

            await using var contenido = await respuesta.Content.ReadAsStreamAsync(plazo.Token);
            return await JsonDocument.ParseAsync(contenido, cancellationToken: plazo.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && plazo.IsCancellationRequested)
        {
            throw new InvalidDataException($"REDES_SOCIALES excedio el tiempo de espera en la pagina {pagina}.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"REDES_SOCIALES devolvio JSON invalido en la pagina {pagina}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plazo.IsCancellationRequested)
                throw new InvalidDataException($"REDES_SOCIALES excedio el tiempo de espera en la pagina {pagina}.");

            throw new InvalidDataException($"No se pudo leer la respuesta HTTP de REDES_SOCIALES en la pagina {pagina}.");
        }
    }

    private static Uri ResolverUrl(Uri origen, Uri pagina, string? valor, string mensaje)
    {
        if (string.IsNullOrWhiteSpace(valor) || !Uri.TryCreate(pagina, valor, out var url) ||
            url.Scheme != origen.Scheme || url.IdnHost != origen.IdnHost || url.Port != origen.Port ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0)
        {
            throw new InvalidDataException(mensaje);
        }

        return url;
    }

    private int LeerEnteroPositivo(string clave, int predeterminado, int maximo = int.MaxValue)
    {
        var texto = configuracion[clave];
        if (texto is null)
            return predeterminado;
        if (int.TryParse(texto, out var valor) && valor > 0 && valor <= maximo)
            return valor;

        throw new InvalidDataException($"Fuentes:ComentariosSociales:{clave} requiere un entero entre 1 y {maximo}.");
    }

    private static string? LeerCampo(JsonElement comentario, string nombre)
    {
        if (!comentario.TryGetProperty(nombre, out var valor) &&
            !comentario.TryGetProperty(char.ToLowerInvariant(nombre[0]) + nombre[1..], out valor))
        {
            return null;
        }

        return valor.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => valor.GetString(),
            JsonValueKind.Number => valor.GetRawText(),
            _ => throw new InvalidDataException($"REDES_SOCIALES devolvio un valor no escalar en el campo {nombre}.")
        };
    }
}
