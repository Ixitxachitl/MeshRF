// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Node list search + filtering, ported from MeshRF.App's filter bar and its
/// NodeFilter* settings. MeshRF.App debounces and filters in parallel because
/// it also drives a map; here the node count is small enough to filter
/// synchronously on change.
/// </summary>
public partial class RadioViewModel
{
    public IReadOnlyList<string> NodeHopsFilterOptions { get; } = ["Any", "Direct", "≤1 hop", "≤2 hops", "≤3 hops", "≤4 hops"];
    public IReadOnlyList<string> NodeKeyFilterOptions { get; } = ["Any", "Good key", "Mismatch", "No key"];
    public IReadOnlyList<string> NodeSignedFilterOptions { get; } = ["Show all", "Signed", "Unsigned"];
    public IReadOnlyList<string> NodeLocationFilterOptions { get; } = ["Any", "Has position", "No position"];
    public IReadOnlyList<string> NodeIgnoredFilterOptions { get; } = ["Show all", "Hide ignored", "Only ignored"];
    public IReadOnlyList<string> NodeMqttFilterOptions { get; } = ["Any", "Hide via MQTT", "Only via MQTT"];
    public IReadOnlyList<string> TelemetryHasFilterOptions { get; } = ["Any", "Has value", "No value"];

    /// <summary>The filtered view bound to the node grid. <see cref="Nodes"/>
    /// stays the unfiltered source of truth that the RX host updates.</summary>
    public ObservableCollection<NodeRecord> FilteredNodes { get; } = new();

    /// <summary>Wraps <see cref="FilteredNodes"/> for the DataGrid. Avalonia's
    /// DataGrid has no public programmatic-sort API; column-header sorting is
    /// expressed through this view's SortDescriptions, which is also how the
    /// saved sort is restored.</summary>
    public DataGridCollectionView NodesView { get; }

    [ObservableProperty] private string _nodeSearchText = string.Empty;
    [ObservableProperty] private string _nodeHopsFilter = "Any";
    [ObservableProperty] private string _nodeKeyFilter = "Any";
    [ObservableProperty] private string _nodeSignedFilter = "Show all";
    [ObservableProperty] private string _nodeLocationFilter = "Any";
    [ObservableProperty] private bool _nodeHideInvalidLocations;
    [ObservableProperty] private string _nodeIgnoredFilter = "Show all";
    [ObservableProperty] private string _nodeMqttFilter = "Any";
    [ObservableProperty] private string _nodeMaxAgeMinutesText = string.Empty;
    [ObservableProperty] private string _nodeDistanceKmText = string.Empty;
    [ObservableProperty] private string _nodeTemperatureFilter = "Any";
    [ObservableProperty] private string _nodeHumidityFilter = "Any";
    [ObservableProperty] private string _nodePressureFilter = "Any";
    [ObservableProperty] private string _nodeGasResistanceFilter = "Any";
    [ObservableProperty] private string _nodeIaqFilter = "Any";
    [ObservableProperty] private string _nodePm10StdFilter = "Any";
    [ObservableProperty] private string _nodePm25StdFilter = "Any";
    [ObservableProperty] private string _nodePm100StdFilter = "Any";
    [ObservableProperty] private string _nodePm10EnvFilter = "Any";
    [ObservableProperty] private string _nodePm25EnvFilter = "Any";
    [ObservableProperty] private string _nodePm100EnvFilter = "Any";
    [ObservableProperty] private string _nodeCh1VoltageFilter = "Any";
    [ObservableProperty] private string _nodeCh1CurrentFilter = "Any";
    [ObservableProperty] private string _nodeCh2VoltageFilter = "Any";
    [ObservableProperty] private string _nodeCh2CurrentFilter = "Any";
    [ObservableProperty] private string _nodeCh3VoltageFilter = "Any";
    [ObservableProperty] private string _nodeCh3CurrentFilter = "Any";

    /// <summary>Label for the node pane header: shows the filtered count when
    /// a filter is narrowing the list.</summary>
    public string NodesHeader => FilteredNodes.Count == Nodes.Count
        ? $"Nodes ({Nodes.Count})"
        : $"Nodes ({FilteredNodes.Count} of {Nodes.Count})";

    private void HookNodeFilter()
    {
        Nodes.CollectionChanged += (_, _) => ApplyNodeFilter();
        ApplyNodeFilter();
    }

    /// <summary>Apply the persisted node-grid sort to <see cref="NodesView"/>.</summary>
    public void ApplyNodeSort(string? propertyPath, bool descending)
    {
        NodesView.SortDescriptions.Clear();
        if (string.IsNullOrWhiteSpace(propertyPath)) return;
        NodesView.SortDescriptions.Add(DataGridSortDescription.FromPath(
            propertyPath,
            descending ? ListSortDirection.Descending : ListSortDirection.Ascending,
            CultureInfo.CurrentCulture));
    }

    /// <summary>Current node-grid sort, for persisting on close.</summary>
    public (string Path, bool Descending) CurrentNodeSort
    {
        get
        {
            var first = NodesView.SortDescriptions.FirstOrDefault();
            return first is null
                ? (string.Empty, false)
                : (first.PropertyPath ?? string.Empty, first.Direction == ListSortDirection.Descending);
        }
    }

    partial void OnNodeSearchTextChanged(string value) => OnFilterChanged();
    partial void OnNodeHopsFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeKeyFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeSignedFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeLocationFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeHideInvalidLocationsChanged(bool value) => OnFilterChanged();
    partial void OnNodeIgnoredFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeMqttFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeMaxAgeMinutesTextChanged(string value) => OnFilterChanged();
    partial void OnNodeDistanceKmTextChanged(string value) => OnFilterChanged();
    partial void OnNodeTemperatureFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeHumidityFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePressureFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeGasResistanceFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeIaqFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePm10StdFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePm25StdFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePm100StdFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePm10EnvFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePm25EnvFilterChanged(string value) => OnFilterChanged();
    partial void OnNodePm100EnvFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeCh1VoltageFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeCh1CurrentFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeCh2VoltageFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeCh2CurrentFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeCh3VoltageFilterChanged(string value) => OnFilterChanged();
    partial void OnNodeCh3CurrentFilterChanged(string value) => OnFilterChanged();

    private void OnFilterChanged()
    {
        ApplyNodeFilter();
        SaveSettings();
    }

    [RelayCommand]
    private void ClearNodeFilters()
    {
        NodeSearchText = string.Empty;
        NodeHopsFilter = "Any";
        NodeKeyFilter = "Any";
        NodeSignedFilter = "Show all";
        NodeLocationFilter = "Any";
        NodeHideInvalidLocations = false;
        NodeIgnoredFilter = "Show all";
        NodeMqttFilter = "Any";
        NodeMaxAgeMinutesText = string.Empty;
        NodeDistanceKmText = string.Empty;
        NodeTemperatureFilter = "Any";
        NodeHumidityFilter = "Any";
        NodePressureFilter = "Any";
        NodeGasResistanceFilter = "Any";
        NodeIaqFilter = "Any";
        NodePm10StdFilter = "Any";
        NodePm25StdFilter = "Any";
        NodePm100StdFilter = "Any";
        NodePm10EnvFilter = "Any";
        NodePm25EnvFilter = "Any";
        NodePm100EnvFilter = "Any";
        NodeCh1VoltageFilter = "Any";
        NodeCh1CurrentFilter = "Any";
        NodeCh2VoltageFilter = "Any";
        NodeCh2CurrentFilter = "Any";
        NodeCh3VoltageFilter = "Any";
        NodeCh3CurrentFilter = "Any";
    }

    private void ApplyNodeFilter()
    {
        FilteredNodes.Clear();
        foreach (var n in Nodes)
            if (PassesFilter(n)) FilteredNodes.Add(n);
        OnPropertyChanged(nameof(NodesHeader));
    }

    private bool PassesFilter(NodeRecord n)
    {
        var search = NodeSearchText.Trim();
        if (search.Length > 0)
        {
            bool hit = n.LongName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || n.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || n.DisplayId.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }

        int maxHops = NodeHopsFilter switch
        {
            "Direct" => 0, "≤1 hop" => 1, "≤2 hops" => 2, "≤3 hops" => 3, "≤4 hops" => 4, _ => -1,
        };
        if (maxHops >= 0 && (n.HopsAway is null || n.HopsAway > maxHops)) return false;

        switch (NodeKeyFilter)
        {
            case "Good key" when !n.HasPublicKey || n.HasKeyMismatch: return false;
            case "Mismatch" when !n.HasKeyMismatch: return false;
            case "No key" when n.HasPublicKey: return false;
        }

        switch (NodeSignedFilter)
        {
            case "Signed" when !n.IsXeddsaVerified: return false;
            case "Unsigned" when n.IsXeddsaVerified: return false;
        }

        switch (NodeLocationFilter)
        {
            case "Has position" when !n.HasLocation: return false;
            case "No position" when n.HasLocation: return false;
        }

        if (NodeHideInvalidLocations && n.HasInvalidLocation) return false;

        switch (NodeIgnoredFilter)
        {
            case "Hide ignored" when n.Ignored: return false;
            case "Only ignored" when !n.Ignored: return false;
        }

        switch (NodeMqttFilter)
        {
            case "Hide via MQTT" when n.SeenViaMqtt: return false;
            case "Only via MQTT" when !n.SeenViaMqtt: return false;
        }

        if (int.TryParse(NodeMaxAgeMinutesText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var maxAge) && maxAge > 0)
        {
            if (n.LastHeardEpoch <= 0) return false;
            var ageMinutes = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - n.LastHeardEpoch) / 60.0;
            if (ageMinutes > maxAge) return false;
        }

        if (double.TryParse(NodeDistanceKmText, NumberStyles.Float, CultureInfo.CurrentCulture, out var maxDistKm) && maxDistKm > 0)
        {
            if (!TryGetHomeLocation(out var hlat, out var hlon)) return false;
            if (n.Latitude is not double lat || n.Longitude is not double lon) return false;
            if (HaversineKm(hlat, hlon, lat, lon) > maxDistKm) return false;
        }

        return HasValue(NodeTemperatureFilter, n.TemperatureC.HasValue)
            && HasValue(NodeHumidityFilter, n.RelativeHumidityPct.HasValue)
            && HasValue(NodePressureFilter, n.BarometricPressureHpa.HasValue)
            && HasValue(NodeGasResistanceFilter, n.GasResistanceMohm.HasValue)
            && HasValue(NodeIaqFilter, n.Iaq.HasValue)
            && HasValue(NodePm10StdFilter, n.Pm10Standard.HasValue)
            && HasValue(NodePm25StdFilter, n.Pm25Standard.HasValue)
            && HasValue(NodePm100StdFilter, n.Pm100Standard.HasValue)
            && HasValue(NodePm10EnvFilter, n.Pm10Environmental.HasValue)
            && HasValue(NodePm25EnvFilter, n.Pm25Environmental.HasValue)
            && HasValue(NodePm100EnvFilter, n.Pm100Environmental.HasValue)
            && HasValue(NodeCh1VoltageFilter, n.Ch1VoltageV.HasValue)
            && HasValue(NodeCh1CurrentFilter, n.Ch1CurrentMa.HasValue)
            && HasValue(NodeCh2VoltageFilter, n.Ch2VoltageV.HasValue)
            && HasValue(NodeCh2CurrentFilter, n.Ch2CurrentMa.HasValue)
            && HasValue(NodeCh3VoltageFilter, n.Ch3VoltageV.HasValue)
            && HasValue(NodeCh3CurrentFilter, n.Ch3CurrentMa.HasValue);
    }

    private static bool HasValue(string filter, bool present) => filter switch
    {
        "Has value" => present,
        "No value" => !present,
        _ => true,
    };

    private bool TryGetHomeLocation(out double lat, out double lon)
    {
        lat = lon = 0;
        return double.TryParse(HomeLatitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(HomeLongitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                   * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Load the persisted NodeFilter* values. Called from the
    /// constructor's snapshot phase, before <c>_settingsLoaded</c> is set.</summary>
    private void LoadNodeFilterSettings(AppSettings s)
    {
        NodeSearchText = s.NodeFilterSearch ?? string.Empty;
        if (NodeHopsFilterOptions.Contains(s.NodeFilterHops)) NodeHopsFilter = s.NodeFilterHops;
        if (NodeKeyFilterOptions.Contains(s.NodeFilterKey)) NodeKeyFilter = s.NodeFilterKey;
        if (NodeSignedFilterOptions.Contains(s.NodeFilterSigned)) NodeSignedFilter = s.NodeFilterSigned;
        if (NodeLocationFilterOptions.Contains(s.NodeFilterLocation)) NodeLocationFilter = s.NodeFilterLocation;
        NodeHideInvalidLocations = s.NodeFilterHideInvalidLocations;
        if (NodeIgnoredFilterOptions.Contains(s.NodeFilterIgnored)) NodeIgnoredFilter = s.NodeFilterIgnored;
        if (NodeMqttFilterOptions.Contains(s.NodeFilterMqtt)) NodeMqttFilter = s.NodeFilterMqtt;
        // Stored as raw text (same as MeshRF.App) so a partially-typed value round-trips.
        NodeMaxAgeMinutesText = s.NodeFilterMaxAgeMinutes ?? string.Empty;
        NodeDistanceKmText = s.NodeFilterDistanceKm ?? string.Empty;
        NodeTemperatureFilter = Valid(s.NodeFilterTemperature);
        NodeHumidityFilter = Valid(s.NodeFilterHumidity);
        NodePressureFilter = Valid(s.NodeFilterPressure);
        NodeGasResistanceFilter = Valid(s.NodeFilterGasResistance);
        NodeIaqFilter = Valid(s.NodeFilterIaq);
        NodePm10StdFilter = Valid(s.NodeFilterPm10Std);
        NodePm25StdFilter = Valid(s.NodeFilterPm25Std);
        NodePm100StdFilter = Valid(s.NodeFilterPm100Std);
        NodePm10EnvFilter = Valid(s.NodeFilterPm10Env);
        NodePm25EnvFilter = Valid(s.NodeFilterPm25Env);
        NodePm100EnvFilter = Valid(s.NodeFilterPm100Env);
        NodeCh1VoltageFilter = Valid(s.NodeFilterCh1Voltage);
        NodeCh1CurrentFilter = Valid(s.NodeFilterCh1Current);
        NodeCh2VoltageFilter = Valid(s.NodeFilterCh2Voltage);
        NodeCh2CurrentFilter = Valid(s.NodeFilterCh2Current);
        NodeCh3VoltageFilter = Valid(s.NodeFilterCh3Voltage);
        NodeCh3CurrentFilter = Valid(s.NodeFilterCh3Current);

        string Valid(string? v) => TelemetryHasFilterOptions.Contains(v) ? v! : "Any";
    }

    /// <summary>Write the NodeFilter* values back. Called from SaveSettings.</summary>
    private void StoreNodeFilterSettings(AppSettings s)
    {
        s.NodeFilterSearch = NodeSearchText;
        s.NodeFilterHops = NodeHopsFilter;
        s.NodeFilterKey = NodeKeyFilter;
        s.NodeFilterSigned = NodeSignedFilter;
        s.NodeFilterLocation = NodeLocationFilter;
        s.NodeFilterHideInvalidLocations = NodeHideInvalidLocations;
        s.NodeFilterIgnored = NodeIgnoredFilter;
        s.NodeFilterMqtt = NodeMqttFilter;
        s.NodeFilterMaxAgeMinutes = NodeMaxAgeMinutesText;
        s.NodeFilterDistanceKm = NodeDistanceKmText;
        s.NodeFilterTemperature = NodeTemperatureFilter;
        s.NodeFilterHumidity = NodeHumidityFilter;
        s.NodeFilterPressure = NodePressureFilter;
        s.NodeFilterGasResistance = NodeGasResistanceFilter;
        s.NodeFilterIaq = NodeIaqFilter;
        s.NodeFilterPm10Std = NodePm10StdFilter;
        s.NodeFilterPm25Std = NodePm25StdFilter;
        s.NodeFilterPm100Std = NodePm100StdFilter;
        s.NodeFilterPm10Env = NodePm10EnvFilter;
        s.NodeFilterPm25Env = NodePm25EnvFilter;
        s.NodeFilterPm100Env = NodePm100EnvFilter;
        s.NodeFilterCh1Voltage = NodeCh1VoltageFilter;
        s.NodeFilterCh1Current = NodeCh1CurrentFilter;
        s.NodeFilterCh2Voltage = NodeCh2VoltageFilter;
        s.NodeFilterCh2Current = NodeCh2CurrentFilter;
        s.NodeFilterCh3Voltage = NodeCh3VoltageFilter;
        s.NodeFilterCh3Current = NodeCh3CurrentFilter;
    }
}
