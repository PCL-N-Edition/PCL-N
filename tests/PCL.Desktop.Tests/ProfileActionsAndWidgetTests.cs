using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Foundation;
using PCL.UI.Next;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void DeleteActionsPersistAndRejectStaleRows()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Service.AddProfile(new LaunchProfile { Username = "Second" });
        fixture.Service.AddProfile(new LaunchProfile { Username = "Third" });
        AccountClick(fixture, "AccountSwitch");
        XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
        XsrUiSceneNode delete = FindByKey(fixture.Shell, scene, "ProfileDelete:1");
        AssertEqual("删除档案 Second", delete.Label);
        XsrUiPoint point = new(delete.Rect.X + 14, delete.Rect.Y + 14);
        AssertTrue(fixture.Shell.Renderer.PointerPressed(point));
        AssertTrue(fixture.Shell.Renderer.PointerReleased(point));
        AssertEqual(2, fixture.Service.GetViews().Count);
        AssertEqual("Player", fixture.Service.GetViews()[fixture.Service.SelectedIndex].Username);
        AssertTrue(new LaunchProfileFilePort(Path.Combine(fixture.TemporaryDirectory, "profiles.json")).Load()
            .Profiles.Select(item => item.Username).SequenceEqual(["Player", "Third"]));
        long before = fixture.Store.ReadCollection<LaunchProfileView>(fixture.Store.Resolve(AccountService.ProfilesKey)).Revision;
        fixture.Service.AddProfile(new LaunchProfile { Username = "Newer" });
        AssertTrue(fixture.Foundation.Commands.TryResolve(FoundationRouteIds.AccountRemoveProfile, out var route));
        var result = fixture.Foundation.Commands.Dispatch(route, new AccountRemoveProfileCommand(0, before)).Completion.GetAwaiter().GetResult();
        AssertFalse(result.IsSuccess);
        AssertEqual(3, fixture.Service.GetViews().Count);
        for (int remaining = 2; remaining >= 0; remaining--)
        {
            AccountClick(fixture, "ProfileDelete:0");
            AssertEqual(remaining, fixture.Service.GetViews().Count);
        }
        scene = fixture.Shell.Render(AccountTestSize);
        AssertTrue(HasKey(fixture.Shell, scene, "AccountHint"));
        AssertTrue(FindByKey(fixture.Shell, scene, "AccountAdd").IsClickable);
    }

    private static void TriviaTimerPublishesOnlyStateAndStops()
    {
        using WidgetClock clock = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), timeProvider: clock);
        _ = fixture.Shell.Render(AccountTestSize);
        string first = ReadCell(fixture.Store, LaunchPageState.WidgetHintKey);
        XsrUiEntityId[] dirty = [.. fixture.Shell.Tree.DirtyEntities()];
        clock.Advance(TimeSpan.FromMilliseconds(2999));
        AssertEqual(first, ReadCell(fixture.Store, LaunchPageState.WidgetHintKey));
        Task.Run(() => clock.Advance(TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
        string second = ReadCell(fixture.Store, LaunchPageState.WidgetHintKey);
        AssertTrue(first != second);
        AssertTrue(dirty.SequenceEqual(fixture.Shell.Tree.DirtyEntities()));
        AssertTrue(fixture.Shell.StateBridge!.PendingCount > 0);
        clock.Advance(TimeSpan.FromSeconds(3));
        string third = ReadCell(fixture.Store, LaunchPageState.WidgetHintKey);
        AssertTrue(second != third);
        fixture.Controller.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        AssertEqual(third, ReadCell(fixture.Store, LaunchPageState.WidgetHintKey));
    }

    private sealed class WidgetClock : TimeProvider, IDisposable
    {
        private WidgetTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new(callback, state, dueTime, period);
        public void Advance(TimeSpan elapsed) => _timer!.Advance(elapsed);
        public void Dispose() => _timer?.Dispose();
        private sealed class WidgetTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = due;
            private TimeSpan _period = period;
            private bool _disposed;
            public void Advance(TimeSpan elapsed)
            {
                _remaining -= elapsed;
                while (!_disposed && _remaining <= TimeSpan.Zero) { _remaining += _period; callback(state); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) { _remaining = dueTime; _period = newPeriod; return !_disposed; }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
