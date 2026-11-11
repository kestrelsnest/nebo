using System.Globalization;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Colours of the shared style for identities and coalitions.</summary>
public static class SkPalette
{
    public static readonly SKColor Friendly = MapStyle.Color("identity.friendly");
    public static readonly SKColor Hostile = MapStyle.Color("identity.hostile");
    public static readonly SKColor Unknown = MapStyle.Color("identity.unknown");
    public static readonly SKColor Selection = MapStyle.Color("identity.selection");
    public static readonly SKColor HaloDark = MapStyle.Color("halo.dark");
    public static readonly SKColor HaloLight = MapStyle.Color("halo.light");

    public static SKColor For(Identity id) => id switch
    {
        Identity.Friendly => Friendly,
        Identity.Hostile => Hostile,
        _ => Unknown,
    };

    public static SKColor Coalition(int side) => side switch
    {
        1 => MapStyle.Color("coalition.red"),
        2 => MapStyle.Color("coalition.blue"),
        _ => MapStyle.Color("coalition.neutral"),
    };
}
