using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public class LoginShellPathTests
{
    [Fact]
    public void The_path_is_read_between_the_markers_whatever_the_rc_file_printed_around_it() =>
        Assert.Equal("/home/u/.nvm/versions/node/v22/bin:/usr/bin",
            LoginShellPath.Parse("Welcome!\n__MTILES_PATH__/home/u/.nvm/versions/node/v22/bin:/usr/bin__MTILES_PATH__"));

    [Fact]
    public void An_answer_without_both_markers_is_no_answer()
    {
        Assert.Null(LoginShellPath.Parse("bash: no job control"));
        Assert.Null(LoginShellPath.Parse("__MTILES_PATH__/usr/bin"));
    }

    [Fact]
    public void Nothing_is_found_on_no_path() => Assert.Null(LoginShellPath.Find("npm", null));
}
