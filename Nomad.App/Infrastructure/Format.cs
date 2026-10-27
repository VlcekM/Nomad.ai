using System.Globalization;

namespace Nomad.App.Infrastructure;

public static class Format
{
    /// <summary>1:08, 12:03, 1:02:03.</summary>
    public static string Duration(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>950, 2.9k, 41k, 1.2M.</summary>
    public static string Tokens(long tokens)
    {
        if (tokens < 1000)
        {
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        if (tokens < 10_000)
        {
            return (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        if (tokens < 1_000_000)
        {
            return (tokens / 1000).ToString(CultureInfo.InvariantCulture) + "k";
        }

        return (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>"a command" / "2 commands"; <paramref name="article"/> is "a" or "an".</summary>
    public static string Count(int n, string singular, string plural, string article = "a") =>
        n == 1 ? $"{article} {singular}" : $"{n} {plural}";

    public static string Relative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var now = DateTime.Now;
        if (local.Date == now.Date)
        {
            return "today " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return "yesterday " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return local.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"just now", "5 minutes ago", "3 hours ago", then the date.</summary>
    public static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 2 => "a minute ago",
            < 60 => $"{(int)age.TotalMinutes} minutes ago",
            < 120 => "an hour ago",
            < 24 * 60 => $"{(int)age.TotalHours} hours ago",
            _ => Relative(utc),
        };
    }

    /// <summary>"just now", "5 minutes ago", "3 hours ago", "yesterday", "12 days ago" up to 30 days, then "12 Oct 2026".</summary>
    public static string Since(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalHours switch
        {
            < 24 => Ago(utc),
            < 48 => "yesterday",
            < 31 * 24 => $"{(int)age.TotalDays} days ago",
            _ => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>"now", "5m", "3h", "2d", then "12 Oct".</summary>
    public static string AgoShort(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes switch
        {
            < 1 => "now",
            < 60 => $"{(int)age.TotalMinutes}m",
            < 24 * 60 => $"{(int)age.TotalHours}h",
            < 14 * 24 * 60 => $"{(int)age.TotalDays}d",
            _ => utc.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>"Resets in 1 hr 25 min" within a day, else "Resets Fri 09:00" (local time).</summary>
    public static string ResetsAt(DateTimeOffset? at)
    {
        if (at is not { } time)
        {
            return "";
        }

        var left = time - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            return "Resets now";
        }

        if (left < TimeSpan.FromDays(1))
        {
            var minutes = (int)Math.Ceiling(left.TotalMinutes);
            return minutes >= 60 ? $"Resets in {minutes / 60} hr {minutes % 60} min" : $"Resets in {minutes} min";
        }

        return "Resets " + time.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"today 14:00", "tomorrow 09:30", else "Thu 14:00" (local time).</summary>
    public static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Now.Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today ? "today " + time
            : local.Date == today.AddDays(1) ? "tomorrow " + time
            : local.ToString("ddd ", CultureInfo.InvariantCulture) + time;
    }

    /// <summary>"40 min", "5 hr", "2 days".</summary>
    public static string Span(TimeSpan span) => span.TotalMinutes switch
    {
        < 60 => $"{Math.Max(1, (int)span.TotalMinutes)} min",
        < 48 * 60 => $"{(int)span.TotalHours} hr",
        _ => $"{(int)span.TotalDays} days",
    };

    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The last path segment of a folder ("R:\Mato\Nomad" → "Nomad").</summary>
    public static string FolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "No folder";
        }

        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
