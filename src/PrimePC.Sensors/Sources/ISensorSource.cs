namespace PrimePC.Sensors.Sources;

/// <summary>
/// Uma fonte de telemetria (biblioteca, API do Windows, driver...). A camada de abstração permite
/// combinar fontes diferentes e lidar com dados ausentes de forma uniforme.
/// </summary>
/// <remarks>Os métodos são chamados sempre a partir da mesma thread de coleta.</remarks>
public interface ISensorSource : IDisposable
{
    string Name { get; }

    /// <summary>Limitações conhecidas desta fonte no computador atual (permissões, drivers ausentes...).</summary>
    IReadOnlyList<string> Limitations { get; }

    /// <summary>Incrementado quando o conjunto de sensores muda (dispositivo conectado/removido).</summary>
    int Generation { get; }

    void Open();

    /// <summary>Atualiza as leituras do hardware.</summary>
    void Update();

    /// <summary>Retorna as leituras atuais. Valores nulos significam "sem leitura neste ciclo".</summary>
    IEnumerable<RawReading> Read();
}
