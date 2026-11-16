using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class ObjectTagsTests
{
    [Fact]
    public void A_players_tag_follows_the_player_and_is_kept_others_are_for_the_session()
    {
        var tags = new ObjectTags();
        var pilot = SymbologyTests.Make(id: 10, player: "HART");
        var ai = SymbologyTests.Make(id: 11);
        tags.Set(pilot, "  LEAD  ");
        tags.Set(ai, "TANKER");
        // The player respawned: a new unit, the same name.
        Assert.Equal("LEAD", tags.Get(pilot with { Id = 99 }));
        Assert.Equal("TANKER", tags.Get(ai));
        Assert.Equal(new Dictionary<string, string> { ["p:HART"] = "LEAD" }, tags.Persistent());

        var next = new ObjectTags();
        next.Load(tags.Persistent());
        Assert.Equal("LEAD", next.Get(pilot));
        Assert.Null(next.Get(ai));
    }

    [Fact]
    public void Tags_are_cut_to_ten_characters_and_cleared_by_empty_text()
    {
        var tags = new ObjectTags();
        var u = SymbologyTests.Make(id: 1, player: "Bullat95");
        var changes = 0;
        tags.Changed += () => changes++;
        tags.Set(u, "WINCHESTER BINGO");
        Assert.Equal("WINCHESTER", tags.Get(u));
        tags.Set(u, "WINCHESTER");  // unchanged: no event
        tags.Set(u, "   ");
        Assert.Null(tags.Get(u));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_contact_is_tagged_by_its_unit_even_with_a_name()
    {
        var contact = SymbologyTests.Make(id: 7, coalition: "red", detected: true, type: "MiG-29 Fulcrum") with { Player = "Ivan" };
        Assert.Equal("u:7", ObjectTags.KeyOf(contact));
    }
}
