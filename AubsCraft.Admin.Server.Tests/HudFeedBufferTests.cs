using AubsCraft.Admin.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>What SignalR delivers while the viewer is open becomes one batched feed for the worker's HUD.</summary>
public class HudFeedBufferTests
{
    private static ActivityEventDto Evt(ActivityEventType type, string? player, string details) =>
        new(DateTime.UtcNow, type, player, details);

    [Test]
    public void ChatLinesAreBatchedInOrder_AndTakeEmptiesTheBuffer()
    {
        var b = new HudFeedBuffer();
        b.AddChat("SpudArt", "hello");
        b.AddChat("Aubs", "hi there");
        b.AddChat("x", "   "); // blank: dropped
        var feed = b.Take();
        Assert.That(feed.ChatSenders, Is.EqualTo(new[] { "SpudArt", "Aubs" }));
        Assert.That(feed.ChatTexts, Is.EqualTo(new[] { "hello", "hi there" }));
        Assert.That(b.HasPending, Is.False);
        Assert.That(b.Take().IsEmpty, Is.True);
    }

    [Test]
    public void ActivityBecomesNotices_AndTheNoiseIsIgnored()
    {
        var b = new HudFeedBuffer();
        b.AddActivity(Evt(ActivityEventType.PlayerJoin, "SpudArt", "SpudArt joined the game"));
        b.AddActivity(Evt(ActivityEventType.PlayerLeave, "Aubs", "Aubs left the game"));
        b.AddActivity(Evt(ActivityEventType.Death, "SpudArt", "SpudArt fell from a high place"));
        b.AddActivity(Evt(ActivityEventType.Advancement, "Aubs", "Aubs has made the advancement [Stone Age]"));
        b.AddActivity(Evt(ActivityEventType.Chat, "Aubs", "already shown as chat"));
        b.AddActivity(Evt(ActivityEventType.WhitelistRejection, "ODEX71", "not whitelisted"));
        b.AddActivity(Evt(ActivityEventType.AdminAction, null, "backup started"));
        var feed = b.Take();
        Assert.That(feed.ToastTexts, Is.EqualTo(new[]
        {
            "SpudArt joined", "Aubs left", "SpudArt fell from a high place", "Aubs has made the advancement [Stone Age]",
        }));
        Assert.That(feed.ToastKinds, Is.EqualTo(new[]
        {
            HudToastKind.Success, HudToastKind.Info, HudToastKind.Danger, HudToastKind.Achievement,
        }));
    }

    [Test]
    public void ThePlayerListIsSentOnlyWhenItChanges_AndOrderDoesNotMatter()
    {
        var b = new HudFeedBuffer();
        b.SetPlayers(["Zed", "aubs"]);
        var first = b.Take();
        Assert.That(first.PlayersChanged, Is.True);
        Assert.That(first.Players, Is.EqualTo(new[] { "aubs", "Zed" }), "sorted, case-insensitively");

        b.SetPlayers(["aubs", "Zed"]);   // the same set, delivered again by the 3-second status push
        Assert.That(b.HasPending, Is.False);

        b.SetPlayers(["aubs"]);          // someone left
        var second = b.Take();
        Assert.That(second.PlayersChanged, Is.True);
        Assert.That(second.Players, Is.EqualTo(new[] { "aubs" }));

        b.SetPlayers([]);                // everyone left: an empty list is a change too
        Assert.That(b.Take(), Has.Property("PlayersChanged").True.And.Property("Players").Empty);
    }

    [Test]
    public void ResetMakesTheNextPlayerListNewAgain()
    {
        var b = new HudFeedBuffer();
        b.SetPlayers(["a"]);
        b.Take();
        b.Reset();
        b.SetPlayers(["a"]);
        Assert.That(b.Take().PlayersChanged, Is.True, "after a reset the HUD has no list, so the same names are news");
    }

    [Test]
    public void AFloodIsCutToTheNewest()
    {
        var b = new HudFeedBuffer();
        for (int i = 0; i < HudFeedBuffer.MaxPendingChat + 30; i++) b.AddChat("bot", $"line {i}");
        var feed = b.Take();
        Assert.That(feed.ChatTexts, Has.Length.EqualTo(HudFeedBuffer.MaxPendingChat));
        Assert.That(feed.ChatTexts[^1], Is.EqualTo($"line {HudFeedBuffer.MaxPendingChat + 29}"));
        Assert.That(feed.ChatTexts[0], Is.EqualTo("line 30"));
    }
}
