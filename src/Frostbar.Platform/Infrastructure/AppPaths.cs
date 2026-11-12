using System.Reflection;
using Frostbar.Core.Shell;

namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// Where things live. <c>--profile &lt;dir&gt;</c> moves settings/state/logs into a test folder (E2E runs);
/// the single-instance names stay the same, because two shells must never run in one session.
/// </summary>
public static class AppPaths
{
    /// <summary>Internal ID: AUMID, mutex, pipes, Run value name. Never change it (settings and registrations hang on it).</summary>
    public const string Id = "frostbar";

    public static string Roaming { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Frostbar");
    public static string Local { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frostbar");
    public static string? Profile { get; private set; }

    public static void Init(string? profile)
    {
        if (!string.IsNullOrEmpty(profile))
        {
            Profile = Path.GetFullPath(profile);
            Roaming = Path.Combine(Profile, "roaming");
            Local = Path.Combine(Profile, "local");
        }
        Directory.CreateDirectory(Roaming);
        Directory.CreateDirectory(Local);
    }

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string StateFile => Path.Combine(Local, "state.json");
    public static string Logs => Path.Combine(Local, "logs");
    public static string Cache => Path.Combine(Local, "cache");
    public static string ExePath => Environment.ProcessPath ?? "Frostbar.exe";

    /// <summary>The version people see: "1.1.0 (e3e275f)", with the commit it was built from (<see cref="VersionText"/>).</summary>
    public static string Version { get; } = VersionText.Short(typeof(AppPaths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The licenses of the third-party software beside the exe (General › About opens it).</summary>
    public static string Notices => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    /// <summary>Installed copies live in %LOCALAPPDATA%\Programs\Frostbar; anything else is a development build.</summary>
    public static bool IsInstalledBuild => ExePath.StartsWith(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Frostbar") + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    static int Session => System.Diagnostics.Process.GetCurrentProcess().SessionId;
    public static string MutexName => $@"Local\{Id}";
    public static string PipeName => $"{Id}-{Session}";
    public static string DiagPipeName => $"{Id}-diag-{Session}";
}
