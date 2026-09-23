namespace mTiles.Tests;

/// <summary>File operations for tests that write beside something else that reads the same file.</summary>
internal static class TestFiles
{
    /// <summary>
    /// Writes the file, retrying while another handle holds it — a watcher's reader that was already on
    /// its way when the test moved on. A sharing violation there is the harness's timing, not what the
    /// test asserts, so it is waited out for a moment rather than reported as a failure.
    /// </summary>
    public static void WriteWhenFree(string path, string text)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            try
            {
                File.WriteAllText(path, text);
                return;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }
}
