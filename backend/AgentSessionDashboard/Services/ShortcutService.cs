using System.Net;
using System.Text.RegularExpressions;
using AgentSessionDashboard.Data;
using AgentSessionDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentSessionDashboard.Services;

public class ShortcutService(SessionDbContext db, IHttpClientFactory httpClientFactory)
{
    public async Task<List<Shortcut>> GetAllAsync()
    {
        return await db.Shortcuts
            .OrderBy(s => s.Order)
            .ToListAsync();
    }

    public async Task<Shortcut> CreateAsync(string url, string? name)
    {
        var resolvedName = string.IsNullOrWhiteSpace(name)
            ? await ScrapeTitleAsync(url)
            : name;

        var maxOrder = await db.Shortcuts.AnyAsync()
            ? await db.Shortcuts.MaxAsync(s => s.Order)
            : -1;

        var shortcut = new Shortcut
        {
            Url = url,
            Name = resolvedName,
            Order = maxOrder + 1
        };

        db.Shortcuts.Add(shortcut);
        await db.SaveChangesAsync();
        return shortcut;
    }

    public async Task<Shortcut?> UpdateNameAsync(int id, string name)
    {
        var shortcut = await db.Shortcuts.FindAsync(id);
        if (shortcut == null) return null;

        shortcut.Name = name;
        await db.SaveChangesAsync();
        return shortcut;
    }

    public async Task ReorderAsync(List<int> orderedIds)
    {
        var shortcuts = await db.Shortcuts
            .Where(s => orderedIds.Contains(s.Id))
            .ToListAsync();

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var shortcut = shortcuts.FirstOrDefault(s => s.Id == orderedIds[i]);
            if (shortcut != null)
                shortcut.Order = i;
        }

        await db.SaveChangesAsync();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var shortcut = await db.Shortcuts.FindAsync(id);
        if (shortcut == null) return false;

        db.Shortcuts.Remove(shortcut);
        await db.SaveChangesAsync();
        return true;
    }

    private async Task<string> ScrapeTitleAsync(string url)
    {
        try
        {
            var client = httpClientFactory.CreateClient("scraper");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var html = await client.GetStringAsync(url, cts.Token);

            var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success)
            {
                var title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                if (!string.IsNullOrEmpty(title))
                    return title;
            }
        }
        catch
        {
            // Never throw from scrape — fall through to host fallback
        }

        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }
}
