using System.Text.Json;
using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class TargetsTests
{
    // As the server's targets.lua writes it.
    private const string Json = """
        {"type":"targets","coalition":"blue","version":"0.2","zones":[
          {"id":5,"name":"TGT zone 5","zone":"TGT zone 5","flights":"Aerial-2","assigned":true,"order":2,"reveal":"known","what":"all","need":5,"dead":3,"damaged":1,"done":false,"x":-150592,"z":443384,"r":120,"verts":null,
           "items":[{"kind":"unit","name":"[Aerial-2:2] Sochi Armor N-S-1","type":"T-72B","x":-150600,"z":443390,"state":"dead","t":754},
                    {"kind":"scenery","name":"71141310","type":"HOME9A","x":-150618,"z":443402,"state":"damaged","t":null}]},
          {"id":3,"name":"TGT zone 3","zone":"TGT zone 3","flights":"Aerial-2","assigned":true,"order":1,"reveal":"known","what":"units","need":4,"dead":4,"damaged":0,"done":true,"x":1,"z":2,"r":300,"verts":null,"items":[]},
          {"id":7,"name":"Depot","zone":"Depot","flights":"unassigned","assigned":false,"order":0,"reveal":"known","what":"units","need":2,"dead":0,"damaged":0,"done":false,"x":1,"z":2,"r":0,
           "verts":[[0,0],[0,100],[100,100],[100,0]],"items":[]}]}
        """;

    [Fact]
    public void Parses_server_message()
    {
        var t = JsonSerializer.Deserialize<TargetsMessage>(Json)!;
        Assert.Equal("blue", t.Coalition);
        Assert.Equal(3, t.Zones.Count);
        var z5 = t.Find(5)!;
        Assert.Equal("2 · TGT zone 5 · Aerial-2 · 3/5", z5.Label);
        Assert.Equal("Sochi Armor N-S-1", z5.Items[0].ShortName);
        Assert.True(z5.Items[0].Dead);
        Assert.Equal(754, z5.Items[0].Time);
        Assert.True(z5.Items[1].Damaged);
        Assert.Null(z5.Items[1].Time);
        Assert.Equal(4, t.Find(7)!.Verts!.Count);
    }

    [Fact]
    public void Groups_by_flight_in_task_order_unassigned_last()
    {
        var t = JsonSerializer.Deserialize<TargetsMessage>(Json)!;
        var groups = t.ByFlight().ToList();
        Assert.Equal(["Aerial-2", "unassigned"], groups.Select(g => g.Flight));
        Assert.Equal([3, 5], groups[0].Zones.Select(z => z.Id));
        Assert.Empty(t.Find(7)!.FlightList);
        Assert.Equal("Depot · unassigned · 0/2", t.Find(7)!.Label);
        Assert.Equal("1 · TGT zone 3 · Aerial-2 · 4/4 ✓", t.Find(3)!.Label);
    }

    [Fact]
    public void Empty_message_clears()
    {
        var t = JsonSerializer.Deserialize<TargetsMessage>("""{"type":"targets","coalition":"red","zones":[]}""")!;
        Assert.Empty(t.Zones);
    }
}
