// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 on8st
using System.Text.Json.Nodes;

namespace Zeus.Plugin.Ubersdr.Domain;

/// <summary>
/// One public UberSDR receiver, as the directory describes it.
///
/// <para>Only the fields the monitor actually uses are lifted out; the directory
/// carries far more. Everything is optional on the wire, so nothing here throws
/// on a missing or oddly-typed field — a directory that grows a field must not
/// break a released plugin.</para>
/// </summary>
public sealed record UberSdrInstance(
    string Id,
    string Callsign,
    string Name,
    string Location,
    string Host,
    bool Tls,
    double DistanceKm,
    double BearingDegrees,
    int AvailableClients,
    int MaxClients,
    bool IsOnline,
    bool AntennaConnected)
{
    /// <summary>The base URL to open a socket against.</summary>
    public string BaseUrl => (Tls ? "https://" : "http://") + Host;

    public string WebSocketBase => (Tls ? "wss://" : "ws://") + Host;

    /// <summary>
    /// Whether this receiver can contribute a signal reading.
    ///
    /// <para>An instance with no antenna streams audio quite happily and reports
    /// <c>-Infinity</c> for power on every frame. Offering it as a monitor would
    /// show the operator a receiver that appears to hear nothing, which reads as
    /// "my signal is not getting there" — the most misleading answer available.
    /// It is excluded, and the panel says why.</para>
    /// </summary>
    public bool CanMeter => IsOnline && AntennaConnected;

    public bool HasCapacity => AvailableClients > 0;

    public static UberSdrInstance? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var host = Str(o, "host");
        if (string.IsNullOrWhiteSpace(host)) return null;

        return new UberSdrInstance(
            Id: Str(o, "id") ?? host,
            Callsign: Str(o, "callsign") ?? "",
            Name: Str(o, "name") ?? host,
            Location: Str(o, "location") ?? "",
            Host: host,
            Tls: Bool(o, "tls") ?? true,
            DistanceKm: Num(o, "distance") ?? double.NaN,
            BearingDegrees: Num(o, "bearing_degrees") ?? double.NaN,
            AvailableClients: (int)(Num(o, "available_clients") ?? 0),
            MaxClients: (int)(Num(o, "max_clients") ?? 0),
            IsOnline: Bool(o, "is_online") ?? false,
            AntennaConnected: Bool(o, "antenna_connected") ?? false);
    }

    private static string? Str(JsonObject o, string k) =>
        o[k] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>() : null;

    private static bool? Bool(JsonObject o, string k) => o[k]?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };

    // Numbers arrive as numbers here, but Wavelog taught this codebase that an
    // API's JSON types are not to be assumed — so a string that parses is
    // accepted too.
    private static double? Num(JsonObject o, string k)
    {
        var v = o[k];
        if (v is null) return null;
        try
        {
            return v.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => v.GetValue<double>(),
                System.Text.Json.JsonValueKind.String =>
                    double.TryParse(v.GetValue<string>(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
