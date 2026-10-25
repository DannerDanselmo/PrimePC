namespace PrimePC.Drivers;

/// <summary>Categoria do dispositivo, usada para agrupar os drivers em abas.</summary>
public enum DriverCategory
{
    Video,
    Audio,
    Network,
    Bluetooth,
    Storage,
    Usb,
    Input,
    System,
    Processor,
    Monitor,
    Printer,
    Software,
    Other,
}

public enum DriverStatus
{
    Ok,
    UpdateAvailable,
    Warning,
    Problem,
}

/// <summary>Driver instalado (ou dispositivo sem driver) informado pelo Windows.</summary>
public sealed record InstalledDriver(
    string DeviceName,
    string DeviceId,
    string DeviceClass,
    DriverCategory Category,
    string? Provider,
    string? Manufacturer,
    string? Version,
    DateTime? Date,
    bool? IsSigned,
    string? Signer,
    string? InfName,
    IReadOnlyList<string> HardwareIds,
    int ProblemCode)
{
    /// <summary>Componente de software (não é um hardware físico).</summary>
    public bool IsSoftwareDevice =>
        DeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
        || Category == DriverCategory.Software;

    /// <summary>Driver que já vem com o Windows (INF da própria pasta do sistema, não "oemNN.inf").</summary>
    public bool IsInbox => InfName is not null && !InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase);

    public bool HasDriver => Version is not null;

    /// <summary>Versão no formato que o fabricante divulga (ex.: NVIDIA 32.0.16.1742 → 617.42).</summary>
    public string? FriendlyVersion => DriverInventory.FriendlyVersion(Provider, Version);
}

/// <summary>Atualização de driver oferecida pelo Windows Update para este computador.</summary>
public sealed record DriverUpdate(
    string Title,
    string? DriverClass,
    string? HardwareId,
    string? Manufacturer,
    string? Model,
    string? Provider,
    DateTime? Date,
    long SizeBytes,
    string? SupportUrl,
    string? UpdateId = null,
    bool RequiresEula = false);

public sealed record DriverScan(DateTime CollectedAt, IReadOnlyList<InstalledDriver> Drivers, IReadOnlyList<string> Notes);

public sealed record DriverUpdateSearch(DateTime SearchedAt, bool Succeeded, IReadOnlyList<DriverUpdate> Updates, string Message);

/// <summary>Avaliação de um driver: estado, rótulo curto e explicação.</summary>
public sealed record DriverAssessment(DriverStatus Status, string Label, string Detail);

/// <summary>Etapa da instalação pelo Windows Update (para a barra de progresso).</summary>
public sealed record DriverInstallProgress(int Index, int Count, string Title, string Stage);

/// <summary>Resultado da instalação de uma atualização de driver.</summary>
public sealed record DriverInstallResult(string Title, bool Succeeded, bool RebootRequired, string Message);

/// <summary>Versão de driver publicada pela NVIDIA para uma placa (consulta oficial por identificador de dispositivo).</summary>
public sealed record NvidiaDriverRelease(
    string Version,
    string Name,
    DateTime? ReleaseDate,
    bool IsWhql,
    Uri DownloadUrl,
    string? DetailsUrl,
    long? SizeBytes = null)
{
    /// <summary>Nome do arquivo do instalador (ex.: 621.01-desktop-win10-win11-64bit-international-dch-whql.exe).</summary>
    public string FileName => Path.GetFileName(DownloadUrl.AbsolutePath);
}

/// <summary>Resultado da consulta à NVIDIA para uma placa de vídeo instalada.</summary>
public sealed record NvidiaCheck(bool Succeeded, NvidiaDriverRelease? Latest, string? InstalledVersion, bool IsNewer, string Message);

public sealed record DownloadProgress(long Received, long? Total, double BytesPerSecond)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

/// <summary>Resultado da verificação da assinatura digital (Authenticode) de um instalador baixado.</summary>
public sealed record SignatureCheck(bool IsTrusted, string? Organization, string Message);

/// <summary>Resultado da criação do ponto de restauração. HasRecent: já existe um ponto recente que permite voltar atrás.</summary>
public sealed record RestorePointResult(bool Created, bool HasRecent, string Message)
{
    public bool CanUndo => Created || HasRecent;
}
