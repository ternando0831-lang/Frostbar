using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class SpecGuardTests
{
    const string Targets = @"C:\dev\Frostbar\tools\Frostbar.TestTarget\bin\Debug\net10.0-windows10.0.26100.0";

    [Theory]
    [InlineData(Targets + @"\Frostbar.TestTarget.exe", true)]
    [InlineData(Targets + @"\Obsidian.exe", true)]                       // a copy under another name (menu-profiles)
    [InlineData(@"c:\DEV\frostbar\tools\frostbar.testtarget\bin\debug\net10.0-windows10.0.26100.0\Frostbar.TestTarget.exe", true)] // the case
    [InlineData(@"C:\Users\user\AppData\Local\AnthropicClaude\app-1.0\claude.exe", false)] // the user's app (2026-10-16)
    [InlineData(Targets + @"x\Frostbar.TestTarget.exe", false)]             // a folder whose name only begins the same
    [InlineData(Targets, false)]                                         // the folder itself is no program
    [InlineData(null, false)]                                            // a program not known
    [InlineData("", false)]
    public void Only_the_programs_in_the_test_targets_folder(string? exe, bool allowed) =>
        Assert.Equal(allowed, SpecGuard.Allows(SpecGuard.Folder(Targets)!, exe, isDesktop: false));

    [Fact]
    public void Never_the_desktop() =>
        Assert.False(SpecGuard.Allows(SpecGuard.Folder(Targets)!, Targets + @"\Frostbar.TestTarget.exe", isDesktop: true));

    [Theory]
    [InlineData(Targets + @"\")]
    [InlineData(Targets + @"\\")]
    [InlineData("  " + Targets + "  ")]
    [InlineData(@"C:/dev/Frostbar/tools/Frostbar.TestTarget/bin/Debug/net10.0-windows10.0.26100.0")] // as a bash script writes it
    public void The_folder_is_read_however_it_is_written(string raw) => Assert.Equal(Targets, SpecGuard.Folder(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_folder_no_guard(string? raw) => Assert.Null(SpecGuard.Folder(raw));
}
