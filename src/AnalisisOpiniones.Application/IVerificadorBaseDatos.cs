namespace AnalisisOpiniones.Application;

public interface IVerificadorBaseDatos
{
    Task VerificarConexionAsync(CancellationToken cancellationToken);
}
