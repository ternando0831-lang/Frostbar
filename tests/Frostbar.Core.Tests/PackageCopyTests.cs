using System.Runtime.InteropServices;
using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

/// <summary>
/// The shell's data folders as another app's copy of AppData (2026-10-19: started from a Claude session, the installed
/// shell wrote to Packages\Claude_…\LocalCache and was closed by Claude's update), and the line it starts again with.
/// </summary>
public sealed class PackageCopyTests
{
    const string Local = @"C:\Users\user\AppData\Local\Frostbar";

    /// <summary>What a new file's final path read from a Claude session on 2026-10-19 (and from outside it).</summary>
    [Theory]
    [InlineData(Local, @"\\?\C:\Users\user\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\Frostbar", "Claude_pzs8sxrjxfjjc")]
    [InlineData(@"C:\Users\user\AppData\Roaming\Frostbar", @"\\?\C:\Users\user\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Frostbar", "Claude_pzs8sxrjxfjjc")]
    [InlineData(Local, @"\\?\C:\Users\user\AppData\Local\Frostbar", null)]
    public void A_new_file_in_a_packages_LocalCache_is_its_copy(string folder, string went, string? family) =>
        Assert.Equal(family, PackageCopy.Family(folder, went));

    [Theory]
    [InlineData(@"\\?\c:\users\user\appdata\local\frostbar\")]
    [InlineData(@"C:\Users\user\AppData\Local\Frostbar")]
    public void The_folder_asked_for_is_not_a_copy_whatever_its_spelling(string went) =>
        Assert.Null(PackageCopy.Family(Local, went));

    /// <summary>AppData moved to another drive through a junction: elsewhere, but nobody's copy.</summary>
    [Fact]
    public void Elsewhere_but_not_in_a_LocalCache_is_not_a_copy()
    {
        Assert.Null(PackageCopy.Family(Local, @"\\?\D:\AppData\Local\Frostbar"));
        Assert.Null(PackageCopy.Family(Local, @"\\?\UNC\server\share\AppData\Local\Frostbar"));
        Assert.Null(PackageCopy.Family(Local, @"\\?\C:\Users\user\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalState\Frostbar"));
    }

    [Fact]
    public void A_folder_that_is_itself_in_a_LocalCache_is_taken_as_asked()
    {
        const string inCache = @"C:\Users\user\AppData\Local\Packages\Some_abc\LocalCache\Local\Frostbar";
        Assert.Null(PackageCopy.Family(inCache, @"\\?\C:\Users\user\AppData\Local\Packages\Other_def\LocalCache\Local\Frostbar"));
    }

    [Fact]
    public void Network_paths_lose_their_prefix_too() =>
        Assert.Equal("Claude_x", PackageCopy.Family(@"\\server\share\Frostbar", @"\\?\UNC\server\share\Packages\Claude_x\LocalCache\Frostbar"));

    /// <summary>
    /// Only a start on the user's own data goes outside; the short commands wait for the start outside (install.ps1 and
    /// uninstall.ps1 wait for --quit and --restore-taskbar), --settings does not (with no shell running it starts one).
    /// Specs, profiles and the shell's own helpers stay.
    /// </summary>
    [Theory]
    [InlineData(new string[0], PackageCopyStep.StartOutside)]
    [InlineData(new[] { "--hide-taskbar" }, PackageCopyStep.StartOutside)]
    [InlineData(new[] { "--quit" }, PackageCopyStep.StartOutsideAndWait)]
    [InlineData(new[] { "--settings" }, PackageCopyStep.StartOutside)]
    [InlineData(new[] { "--restore-taskbar" }, PackageCopyStep.StartOutsideAndWait)]
    [InlineData(new[] { "--outside" }, PackageCopyStep.Warn)]
    [InlineData(new[] { "--outside", "--quit" }, PackageCopyStep.Warn)]
    [InlineData(new[] { "--diag" }, PackageCopyStep.Warn)]
    [InlineData(new[] { "--profile", @"C:\p", "--settings" }, PackageCopyStep.Warn)]
    [InlineData(new[] { "--watchdog", "1", "2" }, PackageCopyStep.None)]
    [InlineData(new[] { "--launch-watchdog", "1", "2" }, PackageCopyStep.None)]
    [InlineData(new[] { "--icons", @"C:\r.txt", "1" }, PackageCopyStep.None)]
    public void What_a_start_in_a_copy_does(string[] args, PackageCopyStep step) =>
        Assert.Equal(step, PackageCopy.Decide(StartupOptions.Parse(args), copied: true));

    [Fact]
    public void Nothing_without_a_copy()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["--quit"], ["--diag"], ["--outside"] })
            Assert.Equal(PackageCopyStep.None, PackageCopy.Decide(StartupOptions.Parse(args), copied: false));
    }

    /// <summary>The shell started again outside does not carry --outside into its own starts (the watchdog's, a new language's).</summary>
    [Fact]
    public void Outside_is_not_passed_on()
    {
        var o = StartupOptions.Parse(["--outside", "--hide-taskbar"]);
        Assert.True(o.Outside);
        Assert.DoesNotContain("--outside", o.RelaunchArgs);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string line, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    /// <summary>Splits as Windows does (the program's name first, then the arguments).</summary>
    static string[] Windows(string args)
    {
        IntPtr argv = CommandLineToArgvW("x.exe " + args, out int n);
        try { return Enumerable.Range(1, n - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    [Fact]
    public void Arguments_come_back_as_they_were()
    {
        string[] args = ["--profile", @"C:\Temp\macos e2e", "", "a\"b", @"C:\dir\", @"C:\dir with space\", "tab\there", @"\\server\share", @"a\\""b"];
        Assert.Equal(args, Windows(CommandLine.Join(args)));
    }

    [Theory]
    [InlineData("--quit", "--quit")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void Plain_arguments_stay_plain_and_the_rest_are_quoted(string arg, string quoted) =>
        Assert.Equal(quoted, CommandLine.Quote(arg));
}
