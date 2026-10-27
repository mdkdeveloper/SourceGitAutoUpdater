namespace SourceGitAutoUpdater;

internal static class Texts
{
    internal static string Locale { get; set; } = "en_US";
    internal static bool Ukrainian => Locale == "uk_UA";
    internal static string T(string english, string ukrainian) => Ukrainian ? ukrainian : english;
    internal static string Update => T("Update", "Оновити");
    internal static string Launch => T("Launch SourceGit", "Запустити SourceGit");
    internal static string Error => T("Unable to update SourceGit", "Не вдалося оновити SourceGit");
}
