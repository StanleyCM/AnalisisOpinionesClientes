using AnalisisOpiniones.Infrastructure.Dimensiones;
using Xunit;

namespace AnalisisOpiniones.Tests;

public sealed class DimensionesTests
{
    [Fact]
    public void Conserva_id_por_origen_y_deduplica_referencias_normalizadas()
    {
        var datos = new DatosDimensionales();
        datos.AgregarOpinion("RESENAS_WEB", " c001 ", " p001 ", "2024-12-31");
        datos.AgregarOpinion("RESENAS_WEB", "C001", "P001", "2024-12-31");
        datos.AgregarOpinion("REDES_SOCIALES", "C001", "P001", "2025-01-02");
        datos.AgregarOpinion("ENCUESTAS", "001", "1", "2025-01-01");

        Assert.Equal(new[]
        {
            ("ENCUESTAS", "001"), ("REDES_SOCIALES", "C001"), ("RESENAS_WEB", "C001")
        }, datos.Clientes.Select(c => (c.Origen, c.Id)).Order());
        Assert.Equal(new[]
        {
            ("ENCUESTAS", "1"), ("REDES_SOCIALES", "P001"), ("RESENAS_WEB", "P001")
        }, datos.Productos.Select(p => (p.Origen, p.Id)).Order());
        Assert.Equal(new DateOnly(2024, 12, 31), datos.FechaMinima);
        Assert.Equal(new DateOnly(2025, 1, 2), datos.FechaMaxima);
        Assert.All(datos.Clientes, cliente => Assert.Null(cliente.Nombre));
    }

    [Fact]
    public void Omite_referencias_invalidas_sin_perder_los_demas_datos()
    {
        var datos = new DatosDimensionales();
        datos.AgregarOpinion("ENCUESTAS", null, "001", "31/12/2024");
        datos.AgregarOpinion("ENCUESTAS", " ", new string('P', 101), " 2025-01-01 ");

        Assert.Empty(datos.Clientes);
        Assert.Equal("001", Assert.Single(datos.Productos).Id);
        Assert.Equal(new DateOnly(2025, 1, 1), datos.FechaMinima);
        Assert.Equal(datos.FechaMinima, datos.FechaMaxima);
        Assert.Equal(1, datos.FechasInvalidas);
        Assert.Equal(2, datos.ClientesSinId);
        Assert.Equal(1, datos.ProductosSinId);
    }
}
