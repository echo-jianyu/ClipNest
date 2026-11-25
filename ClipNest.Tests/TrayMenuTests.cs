using ClipNest;

namespace ClipNest.Tests;

public sealed class TrayMenuTests
{
    [Fact]
    public void ForegroundChange_DoesNotCloseMenuImmediatelyAfterOpening()
    {
        var openedAt = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(App.ShouldCloseTrayMenuForForegroundChange(
            openedAt,
            openedAt.AddMilliseconds(120)));
    }

    [Fact]
    public void ForegroundChange_ClosesMenuAfterGracePeriod()
    {
        var openedAt = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(App.ShouldCloseTrayMenuForForegroundChange(
            openedAt,
            openedAt.AddMilliseconds(450)));
    }
}
