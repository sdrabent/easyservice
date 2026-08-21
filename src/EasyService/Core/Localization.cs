using System.Globalization;
using Microsoft.Win32;

namespace EasyService.Core;

/// <summary>
/// Picks the interface language. English is the neutral language of the resources,
/// German ships as a translation.
///
/// Resolution order: the user's choice (HKCU), then a machine-wide default (HKLM),
/// then the language Windows is running in. The machine-wide value exists because the
/// monitoring commands run under the agent's account, not under the administrator who
/// configured them - an English Checkmk output on a German server is a legitimate wish.
/// </summary>
public static class Localization
{
    public sealed record Language(string Code, string DisplayName);

    /// <summary>
    /// Languages that ship with EasyService. The empty code means "follow Windows".
    /// Each entry names itself in its own language - that is what a speaker recognises
    /// in a menu, regardless of which language the rest of the window is currently in.
    /// </summary>
    public static readonly Language[] Supported =
    {
        new("", ""),
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("it", "Italiano"),
    };

    private const string MachineKey = @"SOFTWARE\EasyService";

    public static string MachineLanguage
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MachineKey, false);
                return key?.GetValue("Language") as string ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>The language actually in effect, as a two-letter code.</summary>
    public static string Effective => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>
    /// Must run before any string is read. Called from Program.Main for the GUI and the CLI,
    /// and from the service host before the supervisor starts writing its log.
    /// </summary>
    public static void Initialize(string? explicitCode = null)
    {
        var code = explicitCode;
        if (string.IsNullOrWhiteSpace(code)) code = UserChoice;
        if (string.IsNullOrWhiteSpace(code)) code = MachineLanguage;
        if (string.IsNullOrWhiteSpace(code)) return;   // follow Windows

        Apply(code);
    }

    public static void Apply(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Unknown code in the registry: stay with what Windows says.
        }
    }

    /// <summary>
    /// Per-user choice, stored next to the other GUI preferences. An empty string means
    /// "follow Windows"; the change takes effect the next time EasyService starts, which
    /// keeps us from having to rebuild every open window.
    /// </summary>
    public static string UserChoice
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\EasyService", false);
                return key?.GetValue("Language") as string ?? "";
            }
            catch
            {
                return "";
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\EasyService", true)!;
                key.SetValue("Language", value ?? "", RegistryValueKind.String);
            }
            catch
            {
                // A preference we could not store is not worth an error dialog.
            }
        }
    }
}
