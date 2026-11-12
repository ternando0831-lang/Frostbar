using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class VersionTextTests
{
    [Theory]
    [InlineData("1.1.0+e3e275feca065deceb2fa51a5f3e61e6947671f4", "1.1.0 (e3e275f)")] // what the SDK writes: the commit
    [InlineData("1.1.0+E3E275FECA065DEC", "1.1.0 (e3e275f)")]
    [InlineData("1.1.0-beta.2+e3e275feca065dec", "1.1.0-beta.2 (e3e275f)")]
    [InlineData(" 1.1.0+e3e275feca065dec \n", "1.1.0 (e3e275f)")]
    [InlineData("1.1.0", "1.1.0")]                // built outside a git repository: no commit
    [InlineData("1.1.0+", "1.1.0")]
    [InlineData("1.1.0+local", "1.1.0 (local)")]  // other build metadata, as it is
    [InlineData("1.1.0+abc", "1.1.0 (abc)")]      // too short for a commit
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Short(string? informational, string want) => Assert.Equal(want, VersionText.Short(informational));
}
