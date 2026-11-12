using System.Text.Json;
using Nebo.Client.Core;
using Xunit;

namespace Nebo.Client.Core.Tests;

public class ChatTests
{
    private static ChatLine L(long id, string kind, string text, string from = "Viper1") => new(id, 1_700_000_000, kind, "all", from, text);

    [Fact]
    public void Parses_the_server_message()
    {
        const string json = """{"type":"chat","coalition":"blue","lines":[{"id":177,"time":1700000000,"kind":"side","to":"blue","from":"Viper1","text":"GCI, picture?"}]}""";
        var m = JsonSerializer.Deserialize<ChatMessage>(json)!;
        Assert.Equal("blue", m.Coalition);
        var l = Assert.Single(m.Lines);
        Assert.Equal(("side", "blue", "Viper1", "GCI, picture?"), (l.Kind, l.To, l.From, l.Text));
        Assert.True(l.IsPlayerChat);
    }

    [Fact]
    public void Keeps_only_new_lines_and_marks_the_backlog()
    {
        var log = new ChatLog();
        var (first, history) = log.Apply(new ChatMessage("blue", [L(10, "chat", "a"), L(11, "join", "connected")]));
        Assert.True(history);
        Assert.Equal(2, first.Count);
        var (second, history2) = log.Apply(new ChatMessage("blue", [L(10, "chat", "a"), L(11, "join", "connected"), L(12, "mission", "New mission")]));
        Assert.False(history2);
        Assert.Equal(12, Assert.Single(second).Id);
        Assert.Equal(3, log.Lines.Count);
        log.Clear();
        Assert.True(log.Apply(new ChatMessage("blue", [L(13, "chat", "b")])).History);
    }

    [Theory]
    [InlineData("GCI, picture?", null, true)]
    [InlineData("overlord request vector", "Overlord", true)]
    [InlineData("anyone tank?", "Overlord", false)]
    [InlineData("atcmd", null, false)]
    [InlineData("Darkstar 1-1 say again", "Darkstar 1-1", true)]
    public void Spots_lines_for_controllers(string text, string? callsign, bool expected) =>
        Assert.Equal(expected, ChatLog.ForControllers(L(1, "side", text), callsign));

    [Fact]
    public void Mission_lines_are_not_for_the_tray() =>
        Assert.False(ChatLog.ForControllers(L(1, "mission", "GCI says hello"), null));

    [Fact]
    public void Area_entry_asks_once_per_entry()
    {
        var e = new AreaEntries();
        Assert.Null(e.Entered(1, null));   // outside every area
        Assert.Equal(7, e.Entered(1, 7));  // into controller 7's area
        Assert.Null(e.Entered(1, 7));      // still there: asked already
        Assert.Equal(8, e.Entered(1, 8));  // straight into 8's
        Assert.Null(e.Entered(1, null));   // out
        Assert.Equal(8, e.Entered(1, 8));  // back in: asked again
        e.Keep([]);
        Assert.Equal(8, e.Entered(1, 8));  // forgotten pilot starts fresh
    }
}
