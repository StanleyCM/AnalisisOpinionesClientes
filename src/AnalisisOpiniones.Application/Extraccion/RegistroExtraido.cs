namespace AnalisisOpiniones.Application.Extraccion;

public sealed record RegistroExtraido(
    string? RegistroExternoId,
    string? ClienteExternoId,
    string? ProductoExternoId,
    string? FechaOriginal,
    string? ComentarioOriginal,
    string? PuntajeOriginal,
    string? ClasificacionOriginal,
    string? CanalOriginal,
    string DatosOriginalesJson);
