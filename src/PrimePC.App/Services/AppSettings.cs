using System.Text.Json;
using System.Text.Json.Serialization;
using PrimePC.Sensors;

namespace PrimePC.App.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>Preferências do usuário, salvas localmente em JSON.</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public int UpdateIntervalMs { get; set; } = 1000;
    public int HistoryMinutes { get; set; } = 10;
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    /// <summary>Sensores favoritos, na ordem exibida no painel inicial.</summary>
    public List<string> Favorites { get; set; } = new();

    public List<string> Hidden { get; set; } = new();
    public List<AlertRule> AlertRules { get; set; } = new();
    public bool AlertOnCriticalState { get; set; } = true;
    public bool AlertSound { get; set; } = true;
    public bool IncludeIdentifiersInReports { get; set; }
    public string? PreferredGpuId { get; set; }
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>Fechar a janela mantém o PrimePC rodando no ícone perto do relógio.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Iniciar com o Windows (tarefa agendada com privilégios de administrador, abrindo perto do relógio).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Registrar automaticamente as sessões de jogo (temperaturas e FPS de cada partida).</summary>
    public bool RecordGameSessions { get; set; } = true;

    public int BenchmarkSeconds { get; set; } = 60;
    public OverlayHotkey BenchmarkHotkey { get; set; } = OverlayHotkey.CtrlShiftF9;

    public PhoneDashboardSettings Phone { get; set; } = new();
}

/// <summary>Painel no celular (página servida na rede local, só leitura).</summary>
public sealed class PhoneDashboardSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8085;

    /// <summary>Código de acesso incluído no endereço; quem não tem o código não vê nada.</summary>
    public string AccessCode { get; set; } = "";
}

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public enum OverlaySize
{
    Small,
    Medium,
    Large,
}

public enum OverlayHotkey
{
    None,
    CtrlShiftF10,
    CtrlShiftF11,
    CtrlShiftO,
    AltShiftO,
    CtrlShiftF9,
    CtrlShiftF12,
    CtrlShiftB,
}

/// <summary>Opções da sobreposição exibida por cima dos jogos.</summary>
public sealed class OverlaySettings : OverlayLayout
{
    public bool Enabled { get; set; }
    public bool OnlyInGames { get; set; } = true;
    public OverlayHotkey Hotkey { get; set; } = OverlayHotkey.CtrlShiftF10;

    /// <summary>Esconde a sobreposição de capturas de tela, gravações e transmissões.</summary>
    public bool HideFromCapture { get; set; }

    public string PingReferenceHost { get; set; } = "1.1.1.1";

    /// <summary>Configuração própria de cada jogo (pelo nome do executável).</summary>
    public List<OverlayProfile> Profiles { get; set; } = new();
}

/// <summary>Perfil de um jogo: aparência própria e se a sobreposição liga sozinha quando ele abre.</summary>
public sealed class OverlayProfile : OverlayLayout
{
    public string Game { get; set; } = "";
    public bool AutoShow { get; set; } = true;
}

/// <summary>O que a sobreposição mostra e como (comum às opções gerais e aos perfis de jogo).</summary>
public class OverlayLayout
{
    public bool ShowFps { get; set; } = true;
    public bool ShowFrameTime { get; set; } = true;
    public bool ShowLow1 { get; set; } = true;
    public bool ShowGraph { get; set; }
    public bool ShowCpuLoad { get; set; } = true;
    public bool ShowCpuTemp { get; set; } = true;
    public bool ShowGpuLoad { get; set; } = true;
    public bool ShowGpuTemp { get; set; } = true;
    public bool ShowPing { get; set; } = true;
    public bool ShowCpuPower { get; set; }
    public bool ShowGpuClock { get; set; }
    public bool ShowGpuPower { get; set; }
    public bool ShowVram { get; set; }
    public bool ShowRam { get; set; }
    public bool ShowClock { get; set; }
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public OverlaySize Size { get; set; } = OverlaySize.Medium;

    /// <summary>Opacidade do fundo, de 0 (transparente) a 100.</summary>
    public int BackgroundOpacity { get; set; } = 60;

    public void CopyLayoutTo(OverlayLayout target)
    {
        target.ShowFps = ShowFps;
        target.ShowFrameTime = ShowFrameTime;
        target.ShowLow1 = ShowLow1;
        target.ShowGraph = ShowGraph;
        target.ShowCpuLoad = ShowCpuLoad;
        target.ShowCpuTemp = ShowCpuTemp;
        target.ShowGpuLoad = ShowGpuLoad;
        target.ShowGpuTemp = ShowGpuTemp;
        target.ShowPing = ShowPing;
        target.ShowCpuPower = ShowCpuPower;
        target.ShowGpuClock = ShowGpuClock;
        target.ShowGpuPower = ShowGpuPower;
        target.ShowVram = ShowVram;
        target.ShowRam = ShowRam;
        target.ShowClock = ShowClock;
        target.Corner = Corner;
        target.Size = Size;
        target.BackgroundOpacity = BackgroundOpacity;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Configurações inválidas; usando padrões.", ex);
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar configurações.", ex);
        }
    }
}
