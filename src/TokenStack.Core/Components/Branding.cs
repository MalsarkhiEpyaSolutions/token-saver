using System.Reflection;

namespace TokenStack.Core.Components;

/// <summary>Central product naming. The on-disk exe is token-saver.exe; token-stack.exe is
/// kept as a LEGACY marker so hooks/shortcuts from v1.0.x installs migrate on upgrade.</summary>
public static class Branding
{
    public const string ExeName = "token-saver.exe";
    public const string LegacyExeName = "token-stack.exe";

    /// <summary>This build's version, as publish.ps1 stamped it. Written into config.json on
    /// install so a later run can tell an up-to-date stack from one that needs upgrading.</summary>
    public static string Version =>
        typeof(Branding).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(Branding).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
