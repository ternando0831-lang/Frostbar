namespace Frostbar.Core.Shell;

/// <summary>
/// The version people see. The SDK writes the informational version as the version, '+' and the commit it was built
/// from (40 hex digits); people see "1.1.0 (e3e275f)", the commit's short form as git shows it. Other build metadata
/// after the '+' is shown as it is, and a version without any as it is.
/// </summary>
public static class VersionText
{
    public static string Short(string? informational)
    {
        var s = informational?.Trim() ?? "";
        int plus = s.IndexOf('+');
        if (plus < 0) return s;
        string version = s[..plus], meta = s[(plus + 1)..];
        if (meta.Length == 0) return version;
        bool commit = meta.Length >= 7 && meta.All(Uri.IsHexDigit);
        return $"{version} ({(commit ? meta[..7].ToLowerInvariant() : meta)})";
    }
}
