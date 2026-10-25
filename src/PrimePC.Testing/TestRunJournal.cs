using System.Text.Json;

namespace PrimePC.Testing;

public sealed record UnfinishedTestRun(string TestName, DateTime StartedAt);

/// <summary>
/// Registra o início de testes intensivos num arquivo marcador. Se o computador travar, reiniciar ou o
/// programa for encerrado de forma inesperada durante o teste, o marcador permanece e é detectado na próxima
/// abertura — indício de instabilidade sob carga.
/// </summary>
public sealed class TestRunJournal
{
    private readonly string _path;

    public TestRunJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "teste-em-andamento.json");
    }

    public void Begin(string testName)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new UnfinishedTestRun(testName, DateTime.Now)));
        }
        catch
        {
            // Sem o marcador apenas perdemos a detecção de encerramento inesperado.
        }
    }

    public void End()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Ignorado.
        }
    }

    /// <summary>Retorna (e limpa) o registro de um teste que não terminou normalmente.</summary>
    public UnfinishedTestRun? TakeUnfinished()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var run = JsonSerializer.Deserialize<UnfinishedTestRun>(File.ReadAllText(_path));
            File.Delete(_path);
            return run;
        }
        catch
        {
            return null;
        }
    }
}
