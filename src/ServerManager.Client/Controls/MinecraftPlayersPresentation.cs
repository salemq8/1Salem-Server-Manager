using System.Globalization;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

public static class MinecraftPlayersPresentation
{
    public static string Text(string english, string arabic) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? arabic : english;

    public static string Count(int? online, int? maximum, bool stale) =>
        ServerPresentation.FormatPlayers(online, maximum) + (stale ? " · " + Text("Stale", "غير محدث") : string.Empty);

    public static IReadOnlyList<MinecraftPlayerProfile> Select(
        IEnumerable<MinecraftPlayerProfile> players, string? search, int filter, int sort)
    {
        var query = (search ?? string.Empty).Trim();
        var selected = players.Where(p => (query.Length == 0 ||
            p.Username?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            p.Uuid.ToString("D").Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Uuid.ToString("N").Contains(query, StringComparison.OrdinalIgnoreCase)) && (filter switch
            {
                1 => p.IsOnline == true, 2 => p.IsOnline == false,
                3 => p.IsOperator == true, 4 => p.IsWhitelisted == true,
                5 => p.IsBanned == true, _ => true
            }));
        return (sort switch
        {
            1 => selected.OrderBy(p => p.Username ?? p.Uuid.ToString(), StringComparer.OrdinalIgnoreCase),
            2 => selected.OrderByDescending(p => p.LastSeenAtUtc),
            3 => selected.OrderBy(p => p.FirstJoinedAtUtc is null).ThenBy(p => p.FirstJoinedAtUtc),
            _ => selected.OrderByDescending(p => p.IsOnline == true)
                .ThenBy(p => p.Username ?? p.Uuid.ToString(), StringComparer.OrdinalIgnoreCase)
        }).ToArray();
    }

    public static string Online(bool? value) => value switch
    {
        true => Text("Online", "متصل"), false => Text("Offline", "غير متصل"),
        _ => Text("Unknown", "غير معروف")
    };
    public static string Known(bool? value) => value switch
    {
        true => Text("Yes", "نعم"), false => Text("No", "لا"), _ => "—"
    };
}
