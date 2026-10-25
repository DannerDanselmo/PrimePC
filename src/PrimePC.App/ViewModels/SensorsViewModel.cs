using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Controls;
using PrimePC.App.Services;
using PrimePC.Reports;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Linha da tabela de sensores (atualizada no lugar a cada leitura).</summary>
public sealed partial class SensorRow : ObservableObject
{
    public SensorRow(SensorDescriptor d, bool favorite, bool hidden)
    {
        Descriptor = d;
        Group = $"{SensorUnits.CategoryDisplayName(d.Category)} · {d.HardwareName}";
        KindText = SensorUnits.KindDisplayName(d.Kind);
        _isFavorite = favorite;
        _isHidden = hidden;
    }

    public SensorDescriptor Descriptor { get; }
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Group { get; }
    public string KindText { get; }
    public string Source => Descriptor.Source;

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _minText = "—";
    [ObservableProperty] private string _maxText = "—";
    [ObservableProperty] private string _averageText = "—";
    [ObservableProperty] private SensorState _state = SensorState.Unavailable;
    [ObservableProperty] private string _stateText = "Indisponível";
    [ObservableProperty] private string? _stateReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph))]
    private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenGlyph))]
    private bool _isHidden;

    public string FavoriteGlyph => IsFavorite ? "" : "";
    public string HiddenGlyph => IsHidden ? "" : "";

    public void Update(SensorReading r, TemperatureUnit unit)
    {
        var k = Descriptor.Kind;
        ValueText = SensorUnits.Format(k, r.Value, unit);
        MinText = r.Min is null ? "—" : SensorUnits.Format(k, r.Min, unit);
        MaxText = r.Max is null ? "—" : SensorUnits.Format(k, r.Max, unit);
        AverageText = r.Average is null ? "—" : SensorUnits.Format(k, r.Average, unit);
        State = r.State;
        StateText = SensorUnits.StateDisplayName(r.State);
        StateReason = r.StateReason ?? $"Origem: {Descriptor.Source}";
    }
}

/// <summary>Aba de sensores (CPU, GPU, RAM…): agrupa categorias de hardware e mostra contagem e pior estado.</summary>
public sealed partial class SensorTab : ObservableObject
{
    public SensorTab(string label, string glyph, params HardwareCategory[] categories)
    {
        Label = label;
        Glyph = glyph;
        Categories = categories;
    }

    public string Label { get; }
    public string Glyph { get; }

    /// <summary>Categorias incluídas; vazio = todas.</summary>
    public IReadOnlyList<HardwareCategory> Categories { get; }

    public bool IsAll => Categories.Count == 0;

    public bool Matches(HardwareCategory category) => IsAll || Categories.Contains(category);

    [ObservableProperty] private int _count;
    [ObservableProperty] private SensorState _worst = SensorState.Normal;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class SensorsViewModel : PageViewModel
{
    private const string All = "Todos";
    private readonly Dictionary<string, SensorRow> _rowsById = new(StringComparer.Ordinal);

    public SensorsViewModel(AppServices services) : base(services)
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Group)));
        View.Filter = Filter;
        Tabs = new ObservableCollection<SensorTab>
        {
            new("CPU", "", HardwareCategory.Cpu),
            new("GPU", "", HardwareCategory.Gpu),
            new("RAM", "", HardwareCategory.Memory),
            new("Placa-mãe", "", HardwareCategory.Motherboard, HardwareCategory.EmbeddedController, HardwareCategory.Cooler),
            new("Armazenamento", "", HardwareCategory.Storage),
            new("Rede", "", HardwareCategory.Network),
            new("Sistema", "", HardwareCategory.System),
            new("Energia e outros", "", HardwareCategory.Battery, HardwareCategory.PowerSupply, HardwareCategory.Other),
            new("Todos", ""),
        };
        _selectedTab = Tabs[0];
        KindOptions = new[] { All }.Concat(Enum.GetValues<SensorKind>().Select(SensorUnits.KindDisplayName)).Distinct().ToList();
        services.FavoritesChanged += (_, _) =>
        {
            foreach (var row in Rows)
            {
                row.IsFavorite = services.Settings.Favorites.Contains(row.Id);
                row.IsHidden = services.Settings.Hidden.Contains(row.Id);
            }
        };
    }

    public override string Title => "Sensores";
    public override string Subtitle => "Todos os sensores acessíveis neste computador, com valor, unidade, origem e estado";
    public override string Glyph => "";

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public ObservableCollection<SensorTab> Tabs { get; }
    public ICollectionView View { get; }
    public IReadOnlyList<string> KindOptions { get; }
    public IReadOnlyList<string> ComparisonOptions { get; } = new[] { "acima de", "abaixo de" };

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private SensorTab _selectedTab;
    [ObservableProperty] private string _selectedKind = All;
    [ObservableProperty] private bool _onlyFavorites;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _showUnavailable = true;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _unavailableCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SensorRow? _selectedRow;

    [ObservableProperty] private ChartSeries? _selectedSeries;
    [ObservableProperty] private string _alertThreshold = "";
    [ObservableProperty] private int _alertComparisonIndex;
    [ObservableProperty] private int _alertDurationSeconds = 5;

    public bool HasSelection => SelectedRow is not null;

    partial void OnSearchTextChanged(string value) => RefreshView();

    partial void OnSelectedTabChanged(SensorTab value)
    {
        // A lista pode ficar sem itens selecionáveis quando o usuário clica fora de um item; mantém a aba atual.
        if (value is null)
        {
            SelectedTab = Tabs[0];
            return;
        }

        SelectedRow = null;
        RefreshView();
    }
    partial void OnSelectedKindChanged(string value) => RefreshView();
    partial void OnOnlyFavoritesChanged(bool value) => RefreshView();
    partial void OnShowHiddenChanged(bool value) => RefreshView();
    partial void OnShowUnavailableChanged(bool value) => RefreshView();

    partial void OnSelectedRowChanged(SensorRow? value)
    {
        UpdateChart();
        if (value?.Descriptor is { } d && Services.Hub.Latest.Find(d.Id)?.Value is { } v)
        {
            float shown = d.Kind == SensorKind.Temperature && TempUnit == TemperatureUnit.Fahrenheit ? SensorUnits.CelsiusToFahrenheit(v) : v;
            AlertThreshold = shown.ToString("0.#", CultureInfo.CurrentCulture);
        }
    }

    public override void OnActivated()
    {
        SyncRows(Services.Hub.Latest);
        UpdateTabs();
    }

    public override void OnSnapshot(SensorSnapshot snapshot)
    {
        bool added = SyncRows(snapshot);
        var unit = TempUnit;
        foreach (var r in snapshot.Readings)
        {
            if (_rowsById.TryGetValue(r.Descriptor.Id, out var row))
                row.Update(r, unit);
        }

        UnavailableCount = snapshot.Readings.Count(r => r.State == SensorState.Unavailable);
        UpdateTabs();
        if (added || !ShowUnavailable)
            RefreshView();
        UpdateChart();
    }

    /// <summary>Atualiza contagem e pior estado (atenção/crítico) de cada aba; abas sem sensores ficam ocultas.</summary>
    private void UpdateTabs()
    {
        foreach (var tab in Tabs)
        {
            int count = 0;
            var worst = SensorState.Normal;
            foreach (var row in Rows)
            {
                if (row.IsHidden || !tab.Matches(row.Descriptor.Category))
                    continue;
                count++;
                if (row.State == SensorState.Critical)
                    worst = SensorState.Critical;
                else if (row.State == SensorState.Warning && worst != SensorState.Critical)
                    worst = SensorState.Warning;
            }

            tab.Count = count;
            tab.Worst = worst;
            tab.IsVisible = tab.IsAll || count > 0;
        }
    }

    private bool SyncRows(SensorSnapshot snapshot)
    {
        var fresh = snapshot.Readings.Where(r => !_rowsById.ContainsKey(r.Descriptor.Id)).ToList();
        if (fresh.Count == 0)
            return false;

        var favorites = Services.Settings.Favorites;
        var hidden = Services.Settings.Hidden;
        foreach (var r in fresh.OrderBy(r => r.Descriptor.Category).ThenBy(r => r.Descriptor.HardwareName)
                     .ThenBy(r => r.Descriptor.Kind).ThenBy(r => NaturalKey(r.Descriptor.Name)))
        {
            var row = new SensorRow(r.Descriptor, favorites.Contains(r.Descriptor.Id), hidden.Contains(r.Descriptor.Id));
            row.Update(r, TempUnit);
            _rowsById[row.Id] = row;
            Rows.Add(row);
        }

        return true;
    }

    private bool Filter(object o)
    {
        if (o is not SensorRow r)
            return false;
        if (r.IsHidden && !ShowHidden)
            return false;
        if (OnlyFavorites && !r.IsFavorite)
            return false;
        if (!ShowUnavailable && r.State == SensorState.Unavailable)
            return false;
        if (SelectedTab is { } tab && !tab.Matches(r.Descriptor.Category))
            return false;
        if (SelectedKind != All && r.KindText != SelectedKind)
            return false;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            return r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                   || r.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                   || r.KindText.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        }

        return true;
    }

    private void RefreshView()
    {
        View.Refresh();
        VisibleCount = View.Cast<object>().Count();
    }

    private void UpdateChart()
    {
        if (SelectedRow is not { } row)
        {
            SelectedSeries = null;
            return;
        }

        bool f = row.Descriptor.Kind == SensorKind.Temperature && TempUnit == TemperatureUnit.Fahrenheit;
        SelectedSeries = ChartSeries.From(Services.Hub.GetHistory(row.Id), f ? c => c * 9 / 5 + 32 : null);
    }

    [RelayCommand]
    private void ToggleFavorite(SensorRow row)
    {
        var list = Services.Settings.Favorites;
        if (list.Contains(row.Id))
            list.Remove(row.Id);
        else
            list.Add(row.Id);
        Services.NotifyFavoritesChanged();
        if (OnlyFavorites)
            RefreshView();
    }

    [RelayCommand]
    private void ToggleHidden(SensorRow row)
    {
        var list = Services.Settings.Hidden;
        if (list.Contains(row.Id))
            list.Remove(row.Id);
        else
            list.Add(row.Id);
        Services.NotifyFavoritesChanged();
        UpdateTabs();
        RefreshView();
    }

    [RelayCommand]
    private void ResetStatistics()
    {
        Services.Hub.ResetStatistics();
        Services.Hub.RequestUpdate();
    }

    [RelayCommand]
    private void ExportHistory()
    {
        var rows = View.Cast<SensorRow>().ToList();
        if (rows.Count == 0)
        {
            Shell.Info("Nenhum sensor visível para exportar. Ajuste os filtros.");
            return;
        }

        var path = Shell.SaveFile("Exportar histórico dos sensores", "Planilha CSV (*.csv)|*.csv",
            $"historico-sensores-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        if (path is null)
            return;
        try
        {
            var series = rows.Select(r => (r.Descriptor, Services.Hub.GetHistory(r.Id))).Where(x => x.Item2.Length > 0).ToList();
            CsvExporter.ExportHistory(series, path);
            Shell.Info($"Histórico de {series.Count} sensores exportado para:\n{path}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao exportar histórico", ex);
            Shell.Error($"Não foi possível exportar: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CreateAlert()
    {
        if (SelectedRow is not { } row)
            return;
        if (!float.TryParse(AlertThreshold, NumberStyles.Float, CultureInfo.CurrentCulture, out float threshold))
        {
            Shell.Error("Informe um valor numérico válido para o limite do alerta.");
            return;
        }

        var d = row.Descriptor;
        if (d.Kind == SensorKind.Temperature && TempUnit == TemperatureUnit.Fahrenheit)
            threshold = (threshold - 32) * 5 / 9;

        var rule = new AlertRule
        {
            SensorId = d.Id,
            SensorName = d.Name,
            HardwareName = d.HardwareName,
            Kind = d.Kind,
            Comparison = AlertComparisonIndex == 0 ? AlertComparison.Above : AlertComparison.Below,
            Threshold = threshold,
            MinDurationSeconds = Math.Clamp(AlertDurationSeconds, 0, 3600),
        };
        Services.Settings.AlertRules.Add(rule);
        Services.Alerts.SetRules(Services.Settings.AlertRules);
        Services.SaveSettings();
        Shell.Info($"Alerta criado: {rule.Describe()}.\nGerencie os alertas em Configurações.");
    }

    private static string NaturalKey(string name) => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(4, '0'));
}
