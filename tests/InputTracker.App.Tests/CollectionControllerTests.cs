namespace InputTracker.App.Tests;

public sealed class CollectionControllerTests
{
    [Fact]
    public void WritableDirectoryStartsHonestlyNotCollectingAndCanPause()
    {
        var controller = new CollectionController(new DirectoryProbeResult(true, @"C:\portable", null));

        Assert.Equal(CollectionMode.NotCollecting, controller.Current.Mode);
        Assert.True(controller.Current.CanPause);

        controller.Pause();
        Assert.Equal(CollectionMode.Paused, controller.Current.Mode);
        Assert.True(controller.Current.CanResume);

        controller.Resume();
        Assert.Equal(CollectionMode.NotCollecting, controller.Current.Mode);
    }

    [Fact]
    public void RejectedDirectoryCannotBeResumed()
    {
        var controller = new CollectionController(new DirectoryProbeResult(false, @"C:\locked", "拒绝访问"));

        controller.Resume();

        Assert.Equal(CollectionMode.RejectedProgramDirectory, controller.Current.Mode);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
        Assert.Contains("不会改用 AppData", controller.Current.Explanation);
    }
}
