using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>The "designations" message: each of the coalition's pilots' latest targeting pod
/// point (F-16C, read from the TGP page by the pilot's NEBO-Export.lua), with the HAD's HTS
/// error ellipse when an emitter was designated, and every emitter each pilot designated in the
/// last 10 minutes (the HAD picture as the pilot steps through it). Controllers choose to see them
/// (the TGP button).</summary>
public sealed record DesignationsMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("designations")] IReadOnlyList<PilotDesignation> Designations);

/// <summary>One pilot's designation. <c>Lat</c>/<c>Lon</c> as the pod page shows them: degrees
/// without a hemisphere (see <see cref="Designations.Resolve"/>). <c>Age</c>: seconds since the
/// server got it. <c>HadMajorM</c>/<c>HadMinorM</c>: the HTS ellipse's axes in metres.
/// <c>Emitter</c>: the pilot's number for a designated emitter (null: a plain pod point);
/// <c>Current</c>: what the pod points at now (null from a server before it kept emitters).</summary>
public sealed record PilotDesignation(
    [property: JsonPropertyName("player")] long Player,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("aircraft")] string Aircraft,
    [property: JsonPropertyName("age")] double Age,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon,
    [property: JsonPropertyName("alt_ft")] double? AltFt,
    [property: JsonPropertyName("track")] string? Track,
    [property: JsonPropertyName("range_nm")] double? RangeNm,
    [property: JsonPropertyName("had_major_m")] double? HadMajorM,
    [property: JsonPropertyName("had_minor_m")] double? HadMinorM,
    [property: JsonPropertyName("had_pgm")] int? HadPgm,
    [property: JsonPropertyName("emitter")] int? Emitter = null,
    [property: JsonPropertyName("current")] bool? Current = null);

/// <summary>A designation ready for the map: signed position, its age now, where the pilot was
/// (for the ellipse's orientation: the HTS is least sure along the line of sight).</summary>
public sealed record DesignationView(
    long Player, string Name, string Aircraft, double Lat, double Lon, double? AltFt, string Track, double AgeSeconds,
    double? MajorM, double? MinorM, int? Pgm, double? FromLat, double? FromLon, int? Emitter = null, bool Current = true)
{
    public bool Hts => MajorM is not null && MinorM is not null;

    /// <summary>"Kestrel · TGP POINT · 1:20" / "Kestrel · HTS 2 PGM2 3.5 NM · 0:40" (emitter 2) /
    /// "Hornet · FLIR SCENE · 0:05" (an F/A-18C's ATFLIR: its track already names the sensor).</summary>
    public string Label
    {
        get
        {
            var what = Hts ? $"HTS{(Emitter is { } n ? $" {n}" : "")}{(Pgm is { } p ? $" PGM{p}" : "")} {MajorM!.Value / Nav.MetersPerNm:0.0} NM"
                : Track.StartsWith("FLIR", StringComparison.Ordinal) ? Track : $"TGP {Track}".TrimEnd();
            var age = TimeSpan.FromSeconds(Math.Max(0, AgeSeconds));
            return $"{Name} · {what} · {(int)age.TotalMinutes}:{age.Seconds:00}";
        }
    }
}

public static class Designations
{
    /// <summary>Designations older than this are not shown (the server forgets them after 10 min).</summary>
    public const double MaxAgeSeconds = 600;

    /// <summary>The pod page shows degrees without N/S or E/W: the signs that put the point nearest
    /// the pilot's own aircraft (always within pod range); without it, north and east.</summary>
    public static (double Lat, double Lon) Resolve(double lat, double lon, double? fromLat, double? fromLon)
    {
        if (fromLat is not { } fl || fromLon is not { } fo) return (lat, lon);
        var best = (lat, lon);
        var bestM = double.MaxValue;
        foreach (var sLat in new[] { 1, -1 })
            foreach (var sLon in new[] { 1, -1 })
            {
                var m = Nav.BearingRange(fl, fo, sLat * lat, sLon * lon).Meters;
                if (m < bestM) (best, bestM) = ((sLat * lat, sLon * lon), m);
            }
        return best;
    }

    /// <summary>The designations to draw: aged by the time since the message came, too old ones
    /// and dismissed ones left out. <paramref name="pilotAt"/>: a pilot's own position by slot.</summary>
    public static IReadOnlyList<DesignationView> Views(IReadOnlyList<PilotDesignation> list, DateTime receivedUtc, DateTime nowUtc,
        Func<string, (double Lat, double Lon)?> pilotAt, Func<PilotDesignation, bool> dismissed)
    {
        var since = (nowUtc - receivedUtc).TotalSeconds;
        var views = new List<DesignationView>();
        foreach (var d in list)
        {
            var age = d.Age + Math.Max(0, since);
            if (age > MaxAgeSeconds || dismissed(d)) continue;
            var from = pilotAt(d.Slot);
            var (lat, lon) = Resolve(d.Lat, d.Lon, from?.Lat, from?.Lon);
            views.Add(new DesignationView(d.Player, d.Name, d.Aircraft, lat, lon, d.AltFt, d.Track ?? "", age,
                d.HadMajorM, d.HadMinorM, d.HadPgm, from?.Lat, from?.Lon, d.Emitter, d.Current ?? true));
        }
        return views;
    }
}
