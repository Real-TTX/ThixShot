using MatDesktop;
using ThixShot.Integration;

namespace ThixShot.Studio;

internal static class StudioAppearance
{
    private static string SettingsPath => Path.Combine(UserFiles.DirectoryPath, "appearance");
    public static MatAppearance Current
    {
        get
        {
            try
            {
                return File.Exists(SettingsPath) && Enum.TryParse<MatAppearance>(File.ReadAllText(SettingsPath), out var appearance) &&
                    Enum.IsDefined(appearance) ? appearance : MatAppearance.System;
            }
            catch (IOException) { return MatAppearance.System; }
            catch (UnauthorizedAccessException) { return MatAppearance.System; }
        }
    }

    public static void Change(MatAppearance appearance)
    {
        if (!Enum.IsDefined(appearance)) throw new ArgumentOutOfRangeException(nameof(appearance));
        UserFiles.Write(SettingsPath, appearance.ToString());
        MatTheme.SetAppearance(appearance);
    }
}
