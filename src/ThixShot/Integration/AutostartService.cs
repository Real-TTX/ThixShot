using System.Xml.Linq;
using Microsoft.Win32;

namespace ThixShot.Integration;

public static class AutostartService
{
    private const string ValueName = "ThixShot";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AgentLabel = "de.thixapps.thixshot";
    private static string AgentFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", AgentLabel + ".plist");

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }

        return OperatingSystem.IsMacOS() && File.Exists(AgentFile);
    }

    public static void SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (enabled && (executable is null || !Path.GetFileNameWithoutExtension(executable).Equals(ValueName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Öffne die installierte ThixShot-App, um den Autostart einzurichten.");
        }

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{executable}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return;
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        if (!enabled)
        {
            File.Delete(AgentFile);
            return;
        }

        var plist = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", AgentLabel),
                    new XElement("key", "ProgramArguments"), new XElement("array",
                        new XElement("string", executable), new XElement("string", "--autostart")),
                    new XElement("key", "RunAtLoad"), new XElement("true"))));
        UserFiles.Write(AgentFile, plist.ToString());
    }
}
