using System.Xml.Linq;
using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public class LoginItemTests
{
    const string Exe = @"C:\Users\a\AppData\Local\Programs\Frostbar\Frostbar.exe";
    static readonly string Value = LoginItem.Command(Exe);
    static readonly byte[] TurnedOff = [3, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01];

    [Fact]
    public void OnWhenTheRunValueIsThereAndTaskManagerLeftItOn()
    {
        Assert.True(LoginItem.IsOn(Value, null));
        Assert.True(LoginItem.IsOn(Value, [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(LoginItem.IsOn(Value, [6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void OffWithoutTheValueOrWhenTaskManagerTurnedItOff()
    {
        Assert.False(LoginItem.IsOn(null, null));
        Assert.False(LoginItem.IsOn(" ", null));
        Assert.False(LoginItem.IsOn(Value, TurnedOff));
        Assert.False(LoginItem.IsOn(Value, [7, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01]));
    }

    [Fact]
    public void TheValueMustStartThisVeryProgram()
    {
        Assert.True(LoginItem.PointsAt(Value, @"c:\users\a\appdata\local\programs\frostbar\frostbar.exe"));
        Assert.True(LoginItem.PointsAt(Exe, Exe)); // the task's command, without quotes
        Assert.False(LoginItem.PointsAt(LoginItem.Command(@"C:\old\Frostbar.exe"), Exe));
        Assert.False(LoginItem.PointsAt(null, @"C:\x\Frostbar.exe"));
    }

    [Fact]
    public void TheTaskForThisProgramDecidesWhetherItIsOn()
    {
        Assert.True(LoginItem.IsOn(new LoginTask(Exe, true), null, null, Exe));
        Assert.False(LoginItem.IsOn(new LoginTask(Exe, false), null, null, Exe));
        // The task is the newer truth: a Run value left beside a disabled task does not turn it on.
        Assert.False(LoginItem.IsOn(new LoginTask(Exe, false), Value, null, Exe));
        // Another copy's task counts as off for this one; a Run value not moved yet still counts.
        Assert.False(LoginItem.IsOn(new LoginTask(@"C:\old\Frostbar.exe", true), null, null, Exe));
        Assert.True(LoginItem.IsOn(new LoginTask(@"C:\old\Frostbar.exe", true), Value, null, Exe));
        Assert.True(LoginItem.IsOn(null, Value, null, Exe));
        Assert.False(LoginItem.IsOn(null, Value, TurnedOff, Exe));
        Assert.False(LoginItem.IsOn(null, null, null, Exe));
    }

    [Fact]
    public void ARunValueThatIsOnMovesToTheTask()
    {
        Assert.Equal(LoginItemMove.ToTask, LoginItem.Next(null, Value, null, Exe));
        Assert.Equal(LoginItemMove.ToTask, LoginItem.Next(new LoginTask(@"C:\old\Frostbar.exe", true), Value, null, Exe));
    }

    [Fact]
    public void ARunValueBesideTheTaskIsDropped()
    {
        // Both would start Frostbar at sign-in, and the second start opens the settings.
        Assert.Equal(LoginItemMove.DropRun, LoginItem.Next(new LoginTask(Exe, true), Value, null, Exe));
        Assert.Equal(LoginItemMove.DropRun, LoginItem.Next(new LoginTask(Exe, false), Value, TurnedOff, Exe));
    }

    [Fact]
    public void ARunValueTurnedOffOrForAnotherCopyIsLeft()
    {
        Assert.Equal(LoginItemMove.None, LoginItem.Next(null, Value, TurnedOff, Exe));
        Assert.Equal(LoginItemMove.None, LoginItem.Next(null, LoginItem.Command(@"C:\old\Frostbar.exe"), null, Exe));
        Assert.Equal(LoginItemMove.None, LoginItem.Next(new LoginTask(Exe, true), null, null, Exe));
        Assert.Equal(LoginItemMove.None, LoginItem.Next(null, null, null, Exe));
    }

    [Fact]
    public void TheTaskStartsThisProgramAtThisUsersSignInWithoutTheDefaultsThatWouldStopIt()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var xml = XElement.Parse(LoginItem.TaskXml(@"C:\Users\a b\AppData\Local\Programs\Frostbar\Frostbar.exe", sid, "Opens Frostbar & more"));
        XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Of(params string[] path)
        {
            XElement e = xml;
            foreach (var p in path) e = e.Element(t + p) ?? throw new Xunit.Sdk.XunitException("no " + p);
            return e.Value;
        }

        Assert.Equal(sid, Of("Triggers", "LogonTrigger", "UserId"));
        Assert.Equal("true", Of("Triggers", "LogonTrigger", "Enabled"));
        Assert.Single(xml.Element(t + "Triggers")!.Elements());
        Assert.Equal(sid, Of("Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Of("Principals", "Principal", "LogonType"));
        Assert.Equal("LeastPrivilege", Of("Principals", "Principal", "RunLevel"));
        Assert.Equal("false", Of("Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Of("Settings", "StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Of("Settings", "ExecutionTimeLimit"));
        Assert.Equal("4", Of("Settings", "Priority"));
        Assert.Equal("IgnoreNew", Of("Settings", "MultipleInstancesPolicy"));
        Assert.Equal("true", Of("Settings", "Enabled"));
        Assert.Equal(@"C:\Users\a b\AppData\Local\Programs\Frostbar\Frostbar.exe", Of("Actions", "Exec", "Command"));
        Assert.Equal(@"C:\Users\a b\AppData\Local\Programs\Frostbar", Of("Actions", "Exec", "WorkingDirectory"));
        Assert.Equal("Opens Frostbar & more", Of("RegistrationInfo", "Description"));
    }
}
