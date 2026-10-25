using System.Globalization;
using System.Management;

namespace PrimePC.Sensors.Inventory;

/// <summary>Consultas WMI com tempo limite e conversões tolerantes a valores ausentes.</summary>
internal static class Wmi
{
    public static List<Dictionary<string, object?>> Query(string query, string scope = @"root\cimv2", int timeoutSeconds = 15)
    {
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties)
                    row[p.Name] = p.Value;
                rows.Add(row);
            }
        }

        return rows;
    }

    public static string? Str(this Dictionary<string, object?> row, string key)
    {
        var s = row.GetValueOrDefault(key)?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static long? Long(this Dictionary<string, object?> row, string key)
    {
        var v = row.GetValueOrDefault(key);
        if (v is null)
            return null;
        try
        {
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    public static int? Int(this Dictionary<string, object?> row, string key)
    {
        var l = row.Long(key);
        return l is null || l > int.MaxValue || l < int.MinValue ? null : (int)l;
    }

    public static bool? Bool(this Dictionary<string, object?> row, string key) =>
        row.GetValueOrDefault(key) is bool b ? b : null;

    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        var s = row.Str(key);
        if (s is null)
            return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(s);
        }
        catch
        {
            return null;
        }
    }
}
