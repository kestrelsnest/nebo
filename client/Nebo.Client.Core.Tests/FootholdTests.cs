using System.Text.Json;
using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class FootholdTests
{
    private const string Json = """
        {"type":"foothold","zones":[
          {"name":"Akrotiri","side":2,"x":-35699,"z":-268771,"r":2286,"active":true,"suspended":false,"airbase":"Akrotiri","facility":"airbase","logistic":true,"built":3,"max":4,"wpt":1,
           "tags":[{"side":2,"tag":"Bomb runway","label":"Bomb runway"},{"side":1,"tag":"SEAD","label":"SEAD"},{"side":2,"tag":"Recon","label":null}]},
          {"name":"Hatay","side":1,"x":10,"z":20,"r":1000,"active":true,"suspended":true,"airbase":null,"facility":null,"logistic":false,"built":0,"max":0,"wpt":null}],
         "links":[["Akrotiri","Hatay"]]}
        """;

    [Fact]
    public void Parses_server_message()
    {
        var state = JsonSerializer.Deserialize<FootholdState>(Json)!;
        Assert.Equal(2, state.Zones.Count);
        Assert.Equal(["Akrotiri", "Hatay"], state.Links[0]);
        var akrotiri = state.Find("Akrotiri")!;
        Assert.Equal("blue", akrotiri.Owner);
        Assert.Equal(0.75, akrotiri.Strength);
        Assert.Equal(1, akrotiri.Waypoint);
    }

    [Fact]
    public void Zone_without_slots_has_unknown_strength()
    {
        var hatay = JsonSerializer.Deserialize<FootholdState>(Json)!.Find("Hatay")!;
        Assert.Null(hatay.Strength);
        Assert.Equal("red", hatay.Owner);
        Assert.Null(hatay.Waypoint);
        Assert.Empty(hatay.TagsFor("blue"));
    }

    [Fact]
    public void Zone_tags_are_per_side()
    {
        var akrotiri = JsonSerializer.Deserialize<FootholdState>(Json)!.Find("Akrotiri")!;
        var blue = akrotiri.TagsFor("blue").ToList();
        Assert.Equal(["RWY", "RCN"], blue.Select(t => t.Badge));
        Assert.Equal(["Bomb runway!", "Recon!"], blue.Select(t => t.Text));
        Assert.Equal(["SEAD"], akrotiri.TagsFor("red").Select(t => t.Badge));
        Assert.Empty(akrotiri.TagsFor(null));
    }
}
