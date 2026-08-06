using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(mTiles.Tests.TestApp))]

// The headless Avalonia session is single-threaded and shared by the whole assembly; running these
// alongside anything else in parallel drops tests without reporting it as a failure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
