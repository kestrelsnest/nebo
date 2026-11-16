using System.Globalization;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Track symbols, style B: identity shapes, speed vectors,
/// data blocks, threat rings, selection.</summary>
public sealed class SymbolsSk : MapLayer
{
    private static readonly float R = MapStyle.Number("symbols.halfSize");
    private static readonly float SymbolWidth = MapStyle.Number("symbols.width");
    private static readonly float ThinWidth = MapStyle.Number("symbols.thinWidth");
    private static readonly float LeaderWidth = MapStyle.Number("symbols.leaderWidth");
    private static readonly double VectorSeconds = MapStyle.Number("symbols.vectorSeconds");
    private static readonly double GroundLabelMaxUnitsPerDip = MapStyle.Number("symbols.groundLabelsMaxMetersPerDip");
    private static readonly Text.Face LabelFace = Text.FaceOf(MapStyle.Text("symbols.label.font"));
    private static readonly float LabelSize = MapStyle.Number("symbols.label.size");
    private static readonly float LineHeight = MapStyle.Number("symbols.label.lineHeight");
    private static readonly Ink.Pen SelectionPen = new(SkPalette.Selection, MapStyle.Number("symbols.selection.width"), MapStyle.Dash("symbols.selection.dash"));
    private static readonly float SelectionRadius = MapStyle.Number("symbols.selection.radius");
    private static readonly Ink.Pen RingPen = new(MapStyle.Color("threatRing.color"), MapStyle.Number("threatRing.width"), MapStyle.Dash("threatRing.dash"));
    private const double HitRadius = 12;

    public MapSettings Settings { get; set; } = new();
    public SKColor Halo { get; set; } = SkPalette.HaloDark;

    /// <summary>The tracks near the mouse (Settings → General, 10 NM by default): their labels (their
    /// flights') are drawn in full, whatever the label level, placed first and drawn on top.</summary>
    public IReadOnlySet<long> HoverIds { get; set; } = new HashSet<long>();
    public Func<MapView>? ViewProvider { get; set; }

    /// <summary>Ground units (vehicles, structures) drawn; ships always are.</summary>
    public bool ShowGroundUnits { get; set; } = true;
    /// <summary>Aircraft on the ground or a deck drawn.</summary>
    public bool ShowLanded { get; set; }

    /// <summary>Filter stages (opacity 0.3 / 0.6 / 1): ground units, landed aircraft, threat rings.</summary>
    public float GroundOpacity { get; set; } = 1;
    public float LandedOpacity { get; set; } = 1;
    public float RingsOpacity { get; set; } = 1;

    /// <summary>Which controllers' jets get their owner's coloured ring (the map's owner filter).</summary>
    public OwnerView Owners { get; set; } = OwnerView.All;

    /// <summary>This map follows AppState.PlayersOnly (GCI maps).</summary>
    public bool PlayersOnlyApplies { get; set; }

    private bool Shown(Track t)
    {
        if (PlayersOnlyApplies && AppState.PlayersOnly && !Symbology.IsPlayer(t.Unit)) return false;
        var kind = Symbology.KindOf(t.Unit);
        if (kind == SymbolKind.Ground) return ShowGroundUnits;
        return ShowLanded || !t.OnGround;
    }

    /// <summary>Every contact within the settings' hover distance of the pointer (LabelHoverNm,
    /// default 10 NM on the map; at least the click radius when zoomed far out).</summary>
    public HashSet<long> HoverTest(double px, double py)
    {
        var near = new HashSet<long>();
        if (ViewProvider?.Invoke() is not { IsValid: true } view) return near;
        var (plat, _) = view.ToLatLon(px, py);
        var radius = Math.Max(HitRadius, AppState.Settings.LabelHoverNm * Nav.MetersPerNm * Mercator.ScaleAt(plat) / view.UnitsPerDip);
        foreach (var t in AppState.Store.Tracks)
        {
            if (t.Unit.Lat is not { } lat || t.Unit.Lon is not { } lon || !Shown(t)) continue;
            var (x, y) = view.ToScreenLonLat(lon, lat);
            if ((x - px) * (x - px) + (y - py) * (y - py) <= radius * radius) near.Add(t.Unit.Id);
        }
        return near;
    }

    /// <summary>Track under a screen point, if any.</summary>
    public long? HitTest(double px, double py, double radius = HitRadius)
    {
        if (ViewProvider?.Invoke() is not { IsValid: true } view) return null;
        long? best = null;
        var bestDist = radius;
        foreach (var t in AppState.Store.Tracks)
        {
            if (t.Unit.Lat is not { } lat || t.Unit.Lon is not { } lon || !Shown(t)) continue;
            var (x, y) = view.ToScreenLonLat(lon, lat);
            var d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (d < bestDist) (best, bestDist) = (t.Unit.Id, d);
        }
        return best;
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var store = AppState.Store;
        if (store.Coalition is not { } own) return;
        var now = DateTime.UtcNow;
        var ordered = store.Tracks
            .Where(t => t.Unit.Lat is not null && t.Unit.Lon is not null && Shown(t))
            .OrderBy(t => Symbology.KindOf(t.Unit) switch { SymbolKind.Ground => 0, SymbolKind.Ship => 1, _ => 2 })
            .ThenBy(t => t.Unit.Id == store.SelectedId)
            .ToList();
        if (Settings.ThreatRings) DrawThreatRings(canvas, view, ordered, own, now);

        var air = new List<(Track Track, SKPoint P, SKColor Ink, SKColor Halo)>();
        foreach (var t in ordered)
        {
            var u = t.Unit;
            var p = SkDraw.ScreenLonLat(view, u.Lon!.Value, u.Lat!.Value);
            if (p.X < -300 || p.Y < -300 || p.X > view.Width + 300 || p.Y > view.Height + 300) continue;
            var identity = Symbology.IdentityOf(u, own);
            var kind = Symbology.KindOf(u);
            var fresh = t.Freshness(now) * (kind == SymbolKind.Ground ? GroundOpacity : t.OnGround && Symbology.IsAir(kind) ? LandedOpacity : 1);
            // Package members in their package's colour, the tag ("P1") left of the symbol.
            var package = identity == Identity.Friendly ? AppState.Packages.PackageOf(u.Id) : null;
            var ink = Ink.WithOpacity(package is null ? SkPalette.For(identity) : PackageColors.Sk(package.Number), fresh);
            // A pilot a controller has: drawn in that controller's colour; while a handoff of him
            // waits for an answer it blinks with white.
            if (identity == Identity.Friendly && FlightDesk.OwnerColor(u.Id, Owners) is { } oc)
                ink = Ink.WithOpacity(FlightDesk.HandoffBlinkOff(u.Id) ? SKColors.White : new SKColor(oc.R, oc.G, oc.B), fresh);
            var halo = Ink.WithOpacity(Halo, fresh);
            var pen = Ink.Stroke(ink, SymbolWidth);
            switch (kind)
            {
                case SymbolKind.Aircraft:
                case SymbolKind.Helicopter:
                    DrawAir(canvas, view, u, identity, kind, p, ink, halo, pen);
                    if (Symbology.IsPlayer(u)) canvas.DrawCircle(p, 2.6f, Ink.Fill(ink));  // flown by a person
                    if (package is not null)
                        Text.Draw(canvas, package.Tag, p.X - R - 24, p.Y + 5, ink, halo, Text.Face.Bold, 13);
                    air.Add((t, p, ink, halo));
                    break;
                case SymbolKind.Ship:
                    Shape(canvas, identity, p, pen, null);
                    using (var hull = new SKPath())
                    {
                        hull.MoveTo(p.X - 12, p.Y + 9);
                        hull.QuadTo(p.X, p.Y + 20, p.X + 12, p.Y + 9);
                        canvas.DrawPath(hull, pen);
                    }
                    if (u.Emitter) EmitterArcs(canvas, p, ink);
                    CenteredBlock(canvas, u, Symbology.DataBlock(u, Settings.Labels), p.X, p.Y + 20, ink, halo);
                    break;
                default:
                    Shape(canvas, identity, p, pen, ink);
                    canvas.DrawLine(p.X - 10, p.Y + 11, p.X + 10, p.Y + 11, pen);
                    if (u.Emitter) EmitterArcs(canvas, p, ink);
                    if (view.UnitsPerDip <= GroundLabelMaxUnitsPerDip)
                        CenteredBlock(canvas, u, Symbology.DataBlock(u, Settings.Labels), p.X, p.Y + 14, ink, halo);
                    break;
            }
            if (u.Id == store.SelectedId) canvas.DrawCircle(p, SelectionRadius, SelectionPen.Paint());
            if (u.Player is not null && Srs.SrsListen.Instance.TalkingOn(u) is { } channel) DrawTalking(canvas, p, channel, halo);
        }
        DrawBlocks(canvas, view, air, own, store.SelectedId);
        DrawMissiles(canvas, view, store, own);
    }

    // ---- Aircraft data blocks: one per flight (Formations), placed clear of each other ----------

    /// <summary>A placed block as drawn last frame: what the panel hit-tests and drags.</summary>
    public readonly record struct PlacedBlock(long Key, Box Box, double AnchorX, double AnchorY, long Lead);

    private readonly Dictionary<long, (double Dx, double Dy)> _dragged = [];
    private readonly TagLayout.Memory _tagMemory = new();

    /// <summary>What labels and other boxes should not cover, as drawn this frame: symbols, pieces of
    /// the speed vectors and the data blocks (the BRAA boxes keep clear of them).</summary>
    public IReadOnlyList<Box> Obstacles { get; private set; } = [];
    private IReadOnlyList<PlacedBlock> _blocks = [];

    /// <summary>The block under a screen point (last frame's layout), if any.</summary>
    public PlacedBlock? HitBlock(double px, double py)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
            if (_blocks[i].Box.Contains(px, py)) return _blocks[i];
        return null;
    }

    /// <summary>A block dragged by the controller: its top-left corner relative to its anchor (the
    /// flight's lead symbol), kept while the flight (its key) lasts.</summary>
    public void DragBlock(long key, double dx, double dy) => _dragged[key] = (dx, dy);

    private static readonly SKColor ChipInk = MapStyle.Color("tags.color");
    private static readonly SKColor ChipText = MapStyle.Color("tags.text");

    /// <summary>The controller's tag: dark text on a filled chip, its top-left at (x, y).</summary>
    private static void DrawChip(SKCanvas canvas, string text, float x, float y)
    {
        var (w, h) = Text.Measure(text, Text.Face.Bold, LabelSize);
        canvas.DrawRoundRect(SKRect.Create(x, y, w + 8, h), 3, 3, Ink.Fill(ChipInk));
        Text.Draw(canvas, text, x + 4, y, ChipText, null, Text.Face.Bold, LabelSize);
    }

    /// <summary>Whether a label line is a player's name, drawn on a plate in the aircraft's colour.</summary>
    private static bool PlayerPlate(Flight f, LabelLevel level, int line)
    {
        if (level == LabelLevel.None) return false;
        // A player alone: the name is line 0 (Symbology.DataBlock); a named flight at full level:
        // one line per member, then the altitude.
        if (f.Members.Count == 1) return line == 0 && Symbology.IsPlayer(f.Lead);
        return level == LabelLevel.Full && Symbology.NameOf(f.Lead) is not null && line < f.Members.Count && Symbology.IsPlayer(f.Members[line]);
    }

    private static readonly SKColor PlateText = new(0x06, 0x10, 0x18);

    private const float PlatePad = 3;

    private float LineWidth(string line, bool plate) => Text.Measure(line, LabelFace, LabelSize).Width + (plate ? PlatePad : 0);

    /// <summary>A player's name: dark text on a plate in the aircraft's colour. The text sits exactly
    /// where a plain line would (same left edge, font and baseline); the plate is only its background.</summary>
    private void DrawPlate(SKCanvas canvas, string text, float x, float y, SKColor ink)
    {
        var (w, _) = Text.Measure(text, LabelFace, LabelSize);
        canvas.DrawRoundRect(SKRect.Create(x - PlatePad, y, w + 2 * PlatePad, LineHeight), 2, 2, Ink.Fill(ink));
        Text.Draw(canvas, text, x, y, Ink.WithOpacity(PlateText, ink.Alpha / 255f), null, LabelFace, LabelSize);
    }

    private void DrawBlocks(SKCanvas canvas, MapView view, List<(Track Track, SKPoint P, SKColor Ink, SKColor Halo)> air, string own, long? selected)
    {
        var at = air.ToDictionary(a => a.Track.Unit.Id);
        // A package member never shares a block with others (the block takes the lead's colour).
        var flights = Formations.Group(air.Select(a => a.Track.Unit).ToList(), own, u => AppState.Packages.PackageOf(u.Id)?.Number)
            // The hovered and the selected aircraft's blocks first (best spots), then friendly before contacts.
            .OrderByDescending(f => f.Members.Any(m => HoverIds.Contains(m.Id)))
            .ThenByDescending(f => f.Members.Any(m => m.Id == selected))
            .ThenBy(f => Symbology.IdentityOf(f.Lead, own))
            .ToList();
        // Drop drag offsets of flights that no longer exist (split up, or gone).
        foreach (var key in _dragged.Keys.Where(k => flights.All(f => f.Key != k)).ToList()) _dragged.Remove(key);
        bool Hovered(Flight f) => f.Members.Any(m => HoverIds.Contains(m.Id));
        var levels = flights.Select(f => Hovered(f) ? LabelLevel.Full : Settings.Labels).ToList();
        var lines = flights.Select((f, i) => Formations.Block(f, levels[i])).ToList();
        // Players stand out: their name line sits on a plate in the aircraft's colour.
        var styles = flights.Select((f, i) => lines[i].Select((_, k) => PlayerPlate(f, levels[i], k)).ToList()).ToList();
        // The controller's tags on the members ("LEAD · NO SRS"): a chip on top of the block.
        var chips = flights.Select(f => string.Join(" · ", f.Members.Select(m => AppState.Tags.Get(m)).OfType<string>())).ToList();
        var tags = new List<TagLayout.Tag>();
        for (int i = 0; i < flights.Count; i++)
        {
            var p = at[flights[i].Lead.Id].P;
            var w = lines[i].Count == 0 ? 0 : lines[i].Select((l, k) => LineWidth(l, styles[i][k])).Max() + 4;
            if (chips[i].Length > 0) w = Math.Max(w, Text.Measure(chips[i], Text.Face.Bold, LabelSize).Width + 8);
            var lead = flights[i].Lead;
            tags.Add(new TagLayout.Tag(flights[i].Key, p.X, p.Y, w, (lines[i].Count + (chips[i].Length > 0 ? 1 : 0)) * LineHeight + 2,
                _dragged.TryGetValue(flights[i].Key, out var d) ? d : null,
                lead.Spd is > 5 && lead.Hdg is { } hdg ? hdg * Math.PI / 180 : null));
        }
        // Obstacles: every symbol, and each speed vector as a chain of small boxes along it.
        var symbols = air.Select(a => new Box(a.P.X - R - 3, a.P.Y - R - 3, 2 * R + 6, 2 * R + 6)).ToList();
        foreach (var (track, p, _, _) in air)
        {
            if (track.Unit is not { Spd: > 5 and var spd, Hdg: { } hdg, Lat: { } lat }) continue;
            var len = spd * VectorSeconds * Mercator.ScaleAt(lat) / view.UnitsPerDip;
            var rad = hdg * Math.PI / 180;
            for (double s = R + 4; s <= len; s += 6)
                symbols.Add(new Box(p.X + Math.Sin(rad) * s - 3, p.Y - Math.Cos(rad) * s - 3, 6, 6));
        }
        var boxes = TagLayout.Place(tags, symbols, Settings.Tags, _tagMemory);
        _tagMemory.Keep(flights.Select(f => f.Key));
        var placed = new List<PlacedBlock>();
        // The hovered flight (index 0 when there is one) is drawn last, on top of the others.
        var order = Enumerable.Range(0, flights.Count).OrderBy(i => Hovered(flights[i]) ? 1 : 0);
        foreach (var i in order)
        {
            if (lines[i].Count == 0 && chips[i].Length == 0) continue;
            var (_, p, ink, halo) = at[flights[i].Lead.Id];
            var box = boxes[i];
            placed.Add(new PlacedBlock(flights[i].Key, box, p.X, p.Y, flights[i].Lead.Id));
            // Leader: from the symbol's edge to the block's nearest point.
            float nx = (float)Math.Clamp(p.X, box.X, box.Right), ny = (float)Math.Clamp(p.Y, box.Y, box.Bottom);
            var len = MathF.Sqrt((nx - p.X) * (nx - p.X) + (ny - p.Y) * (ny - p.Y));
            if (len > R + 2)
                canvas.DrawLine(p.X + (nx - p.X) / len * (R + 1), p.Y + (ny - p.Y) / len * (R + 1), nx, ny, Ink.Stroke(ink, LeaderWidth));
            // Whole pixels: a label moving by fractions of a pixel makes a plate's edges shimmer.
            var top = MathF.Round((float)box.Y) + 1;
            var left = MathF.Round((float)box.X) + 2;
            if (chips[i].Length > 0)
            {
                DrawChip(canvas, chips[i], left - 4, top);  // its text on the lines' left edge
                top += LineHeight;
            }
            for (int k = 0; k < lines[i].Count; k++)
            {
                var y = top + k * LineHeight;
                if (styles[i][k]) DrawPlate(canvas, lines[i][k], left, y, ink);
                else Text.Draw(canvas, lines[i][k], left, y, ink, halo, LabelFace, LabelSize);
            }
        }
        _blocks = placed;
        Obstacles = [.. symbols, .. placed.Select(b => b.Box)];
    }

    private static readonly SKColor TalkInk = MapStyle.Color("srs.talk");
    private static readonly float TalkWidth = MapStyle.Number("srs.ringWidth");

    /// <summary>A player talking on SRS: two rings pulsing out from the symbol and the frequency
    /// under it. The map is redrawn every 100 ms while anyone talks.</summary>
    private static void DrawTalking(SKCanvas canvas, SKPoint p, Core.SrsChannel channel, SKColor halo)
    {
        var phase = (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds % 0.8 / 0.8);
        foreach (var k in new[] { phase, (phase + 0.5f) % 1 })
            canvas.DrawCircle(p, R + 6 + k * 14, Ink.Stroke(Ink.WithOpacity(TalkInk, 1 - k), TalkWidth));
        Text.DrawCentered(canvas, $"{channel.Mhz:0.000}", p.X, p.Y - R - 30, TalkInk, halo, Text.Face.Bold, 11);
    }

    private static readonly float MissileSize = MapStyle.Number("missiles.size");
    private static readonly float TrailWidth = MapStyle.Number("missiles.trailWidth");
    private static readonly float TrailAlpha = MapStyle.Number("missiles.trailAlpha");
    private static readonly double MissileVectorSeconds = MapStyle.Number("missiles.vectorSeconds");
    private static readonly Text.Face MissileFace = Text.FaceOf(MapStyle.Text("missiles.label.font"));
    private static readonly float MissileLabelSize = MapStyle.Number("missiles.label.size");
    private static readonly Ink.Pen TargetPen = new(SKColors.White, MapStyle.Number("missiles.targetLine.width"), MapStyle.Dash("missiles.targetLine.dash"));

    /// <summary>Missiles: trail, an arrowhead along the flight path with a short vector, a line to
    /// the target when it is on the map, and the type (own) or "MSL" (incoming).</summary>
    private void DrawMissiles(SKCanvas canvas, MapView view, PictureStore store, string own)
    {
        foreach (var m in store.Missiles)
        {
            var w = m.Weapon;
            var ink = SkPalette.For(w.Coalition == own ? Identity.Friendly : Identity.Hostile);
            var p = SkDraw.ScreenLonLat(view, w.Lon, w.Lat);
            if (w.Target is { } tid && store.Find(tid)?.Unit is { Lat: { } tlat, Lon: { } tlon })
                canvas.DrawLine(p, SkDraw.ScreenLonLat(view, tlon, tlat), (TargetPen with { Color = Ink.WithOpacity(ink, 0.7) }).Paint());
            if (m.Trail.Count > 1)
            {
                using var trail = new SKPath();
                trail.MoveTo(SkDraw.ScreenLonLat(view, m.Trail[0].Lon, m.Trail[0].Lat));
                for (int i = 1; i < m.Trail.Count; i++) trail.LineTo(SkDraw.ScreenLonLat(view, m.Trail[i].Lon, m.Trail[i].Lat));
                canvas.DrawPath(trail, Ink.Stroke(Ink.WithOpacity(ink, TrailAlpha), TrailWidth));
            }
            if (p.X < -100 || p.Y < -100 || p.X > view.Width + 100 || p.Y > view.Height + 100) continue;
            var rad = w.Hdg * Math.PI / 180;
            float dx = (float)Math.Sin(rad), dy = (float)-Math.Cos(rad);
            var len = (float)(w.Spd * MissileVectorSeconds * Mercator.ScaleAt(w.Lat) / view.UnitsPerDip);
            canvas.DrawLine(p.X, p.Y, p.X + dx * len, p.Y + dy * len, Ink.Stroke(ink, ThinWidth));
            using (var head = new SKPath())
            {
                var s = MissileSize;
                head.AddPoly([new(p.X + dx * s * 1.4f, p.Y + dy * s * 1.4f),
                    new(p.X - dx * s + dy * s * 0.7f, p.Y - dy * s - dx * s * 0.7f),
                    new(p.X - dx * s - dy * s * 0.7f, p.Y - dy * s + dx * s * 0.7f)], close: true);
                canvas.DrawPath(head, Ink.Fill(ink));
                canvas.DrawPath(head, Ink.Stroke(Halo, 0.8f));
            }
            var label = w.Type is { Length: > 0 } t ? t : "MSL";
            Text.Draw(canvas, $"{label} {w.Alt * Unit.FeetPerMeter / 1000:0}k M{Symbology.MachOf(w.Spd, w.Alt):0.0}", p.X + 7, p.Y + 3, ink, Halo, MissileFace, MissileLabelSize);
        }
        DrawLaunches(canvas, view, store);
    }

    /// <summary>Launches at the side's players: a blinking ring on each target and a banner along
    /// the top of the map (who, how many, the jump key).</summary>
    private static void DrawLaunches(SKCanvas canvas, MapView view, PictureStore store)
    {
        if (store.Launches.Count == 0) return;
        var on = DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 1000 < 600;
        var ink = SkPalette.For(Identity.Hostile);
        foreach (var l in store.Launches)
            if (store.Find(l.Target)?.Unit is { Lat: { } lat, Lon: { } lon })
            {
                var p = SkDraw.ScreenLonLat(view, lon, lat);
                canvas.DrawCircle(p, R + 10, Ink.Stroke(Ink.WithOpacity(ink, on ? 1 : 0.35), on ? 3 : 2));
            }
        var newest = store.Launches[0];
        var more = store.Launches.Count > 1 ? $" (+{store.Launches.Count - 1})" : "";
        var text = $"MISSILE LAUNCH at {newest.Player}{(newest.Type is { } t ? " " + Symbology.ShortType(t) : "")}{more} · {Input.Bindings.Get(Input.Bindings.LaunchJump)} jumps";
        const float size = 15;
        var (w, h) = Text.Measure(text, Text.Face.Bold, size);
        var box = SKRect.Create((float)view.Width / 2 - w / 2 - 12, 8, w + 24, h + 10);
        canvas.DrawRect(box, Ink.Fill(on ? ink : Ink.WithOpacity(ink, 0.75)));
        Text.DrawCentered(canvas, text, box.MidX, box.Top + 5, SKColors.White, null, Text.Face.Bold, size);
    }

    private void DrawAir(SKCanvas canvas, MapView view, Unit u, Identity identity, SymbolKind kind, SKPoint p, SKColor ink, SKColor halo, SKPaint pen)
    {
        Shape(canvas, identity, p, pen, null);
        var top = identity == Identity.Hostile ? p.Y - R - 2 : p.Y - R;
        if (kind == SymbolKind.Helicopter)
        {
            canvas.DrawLine(p.X - 8, top - 5, p.X + 8, top - 5, pen);
            canvas.DrawLine(p.X, top - 5, p.X, top, pen);
        }
        if (u.Spd is > 5 && u.Hdg is { } hdg)
        {
            var len = u.Spd.Value * VectorSeconds * Mercator.ScaleAt(u.Lat!.Value) / view.UnitsPerDip;
            var rad = hdg * Math.PI / 180;
            canvas.DrawLine(p.X, p.Y, (float)(p.X + Math.Sin(rad) * len), (float)(p.Y - Math.Cos(rad) * len), pen);
        }
        // The data block comes later (DrawBlocks): flights share one, placed clear of the others.
    }

    private static void Shape(SKCanvas canvas, Identity identity, SKPoint p, SKPaint pen, SKColor? fill)
    {
        switch (identity)
        {
            case Identity.Friendly:
                if (fill is { } f1) canvas.DrawCircle(p, R, Ink.Fill(f1));
                canvas.DrawCircle(p, R, pen);
                break;
            case Identity.Hostile:
                var d = R + 2;
                using (var diamond = new SKPath())
                {
                    diamond.AddPoly([new(p.X, p.Y - d), new(p.X + d, p.Y), new(p.X, p.Y + d), new(p.X - d, p.Y)], close: true);
                    if (fill is { } f2) canvas.DrawPath(diamond, Ink.Fill(f2));
                    canvas.DrawPath(diamond, pen);
                }
                break;
            default:
                var rect = SKRect.Create(p.X - R, p.Y - R, 2 * R, 2 * R);
                if (fill is { } f3) canvas.DrawRect(rect, Ink.Fill(f3));
                canvas.DrawRect(rect, pen);
                break;
        }
    }

    private static void EmitterArcs(SKCanvas canvas, SKPoint p, SKColor ink)
    {
        var thin = Ink.Stroke(ink, ThinWidth);
        foreach (var r in new[] { 11f, 16f })
        {
            // Arc right of the symbol from (0.66r, −0.75r) to (0.66r, +0.75r) on a circle of radius r.
            var start = MathF.Atan2(-0.75f, 0.66f) * 180 / MathF.PI;
            using var arc = new SKPath();
            arc.AddArc(SKRect.Create(p.X + 0.66f * r - MathF.Sqrt(r * r - 0.5625f * r * r) - r, p.Y - r, 2 * r, 2 * r), start, -2 * start);
            canvas.DrawPath(arc, thin);
        }
    }

    private void DrawThreatRings(SKCanvas canvas, MapView view, List<Track> tracks, string own, DateTime now)
    {
        var area = SkClip.Area(view);
        var drawn = new HashSet<(int, int, double)>();
        foreach (var t in tracks)
        {
            var u = t.Unit;
            // From the DCS type, or from the controllers' identity of a ground unit ("6" → SA-6).
            if (Symbology.IdentityOf(u, own) != Identity.Hostile
                || (ThreatRanges.MetersFor(u.Type) ?? RwrLabels.ThreatFor(u.Assigned, u.Category)?.RangeM) is not { } range) continue;
            var p = SkDraw.ScreenLonLat(view, u.Lon!.Value, u.Lat!.Value);
            var r = (float)(range * Mercator.ScaleAt(u.Lat!.Value) / view.UnitsPerDip);
            if (!drawn.Add(((int)(p.X / 6), (int)(p.Y / 6), range))) continue;  // one ring per SAM site
            SkClip.Circle(canvas, null, RingPen with { Color = Ink.WithOpacity(RingPen.Color, t.Freshness(now) * RingsOpacity) }, p, r, area);
        }
    }

    private void CenteredBlock(SKCanvas canvas, Unit u, IReadOnlyList<string> lines, float x, float y, SKColor ink, SKColor halo)
    {
        if (AppState.Tags.Get(u) is { } tag)
        {
            var (w, _) = Text.Measure(tag, Text.Face.Bold, LabelSize);
            DrawChip(canvas, tag, x - w / 2 - 4, y);
            y += LineHeight;
        }
        foreach (var line in lines)
        {
            Text.DrawCentered(canvas, line, x, y, ink, halo, LabelFace, LabelSize);
            y += LineHeight;
        }
    }
}
