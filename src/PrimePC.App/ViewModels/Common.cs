using CommunityToolkit.Mvvm.ComponentModel;
using PrimePC.App.Services;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Base das telas (abas) do aplicativo.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(AppServices services)
    {
        Services = services;
    }

    protected AppServices Services { get; }

    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    /// <summary>Glifo da fonte Segoe Fluent Icons.</summary>
    public abstract string Glyph { get; }

    [ObservableProperty]
    private bool _isActive;

    public virtual void OnActivated()
    {
    }

    /// <summary>Chamado na thread da interface a cada ciclo de sensores (somente para a tela visível e o painel).</summary>
    public virtual void OnSnapshot(SensorSnapshot snapshot)
    {
    }

    protected TemperatureUnit TempUnit => Services.Settings.TemperatureUnit;

    protected string Format(SensorKind kind, float? value) => SensorUnits.Format(kind, value, TempUnit);
}

/// <summary>Valor exibido com rótulo, estado (cor) e dica explicando indisponibilidades.</summary>
public sealed partial class MetricItem : ObservableObject
{
    public MetricItem(string label)
    {
        _label = label;
    }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _value = "—";

    [ObservableProperty]
    private SensorState _state = SensorState.Unavailable;

    [ObservableProperty]
    private string? _tooltip;

    /// <summary>Atualiza a partir de um sensor; se o sensor não existir neste PC, explica isso.</summary>
    public void Set(SensorSnapshot snapshot, string? sensorId, TemperatureUnit unit, string? missingReason = null)
    {
        var reading = sensorId is null ? null : snapshot.Find(sensorId);
        if (reading is null)
        {
            Value = "Não disponível";
            State = SensorState.Unavailable;
            Tooltip = missingReason ?? "Nenhum sensor correspondente foi encontrado neste computador.";
            return;
        }

        Value = SensorUnits.Format(reading.Descriptor.Kind, reading.Value, unit);
        State = reading.State;
        Tooltip = reading.StateReason is null
            ? $"{reading.Descriptor.Name} · {reading.Descriptor.Source}"
            : $"{reading.StateReason}\n{reading.Descriptor.Name} · {reading.Descriptor.Source}";
    }

    public void SetText(string value, SensorState state = SensorState.Normal, string? tooltip = null)
    {
        Value = value;
        State = state;
        Tooltip = tooltip;
    }
}

/// <summary>Aviso exibido no topo da janela.</summary>
public sealed partial class BannerItem : ObservableObject
{
    public BannerItem(string severity, string title, string message)
    {
        Severity = severity;
        Title = title;
        Message = message;
    }

    /// <summary>"Info", "Warning" ou "Critical".</summary>
    public string Severity { get; }

    public string Title { get; }
    public string Message { get; }
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public bool HasAction => Action is not null;
    public string Key { get; init; } = "";
}
