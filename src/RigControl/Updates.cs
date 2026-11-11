using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using RigControl.Core;

namespace RigControl;

/// <summary>A newer release on GitHub, and its installer when the release has one.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string? SetupUrl, string? SetupSha256);

/// <summary>
/// Asks GitHub for RigControl's latest release (once at start, then daily, when enabled in Settings). The request
/// carries nothing but a generic user agent. Updating downloads the release's setup, checks it against the SHA-256
/// GitHub publishes for it, and runs it; RigControl exits so setup can replace it.
/// </summary>
public static class Updates
{
    const string Api = "https://api.github.com/repos/KrapFey/rig-control/releases/latest";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static Updates()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RigControl-update-check");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version Current { get; } = typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>The newer release, or null when this is the latest. Throws when GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Api));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean() || root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        if (Parse(root.GetProperty("tag_name").GetString()) is not { } latest || latest <= Current) return null;
        string? setup = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() is not { } name || !name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
            setup = a.GetProperty("browser_download_url").GetString();
            sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } s && s.StartsWith("sha256:") ? s[7..] : null;
        }
        return new UpdateInfo(latest, root.GetProperty("html_url").GetString()!, setup, sha);
    }

    /// <summary>"v0.4.0" → 0.4.0.</summary>
    internal static Version? Parse(string? tag) =>
        Version.TryParse(tag?.TrimStart('v', 'V').Split('-', '+')[0], out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;

    /// <summary>Downloads the setup, verifies it, starts it, and exits RigControl. Returns an error message instead when it can't.</summary>
    public static async Task<string?> InstallAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        if (update.SetupUrl is null || update.SetupSha256 is null) return "This release has no verified installer; download it from the release page.";
        var file = Path.Combine(Path.GetTempPath(), $"rig-control-{update.Version}-setup.exe");
        using (var resp = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(file);
            var buf = new byte[1 << 16];
            for (int n; (n = await src.ReadAsync(buf)) > 0;)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        await using (var f = File.OpenRead(file))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!hash.Equals(update.SetupSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                return "The downloaded installer didn't match the checksum GitHub publishes for it, so it wasn't run.";
            }
        }
        AppConfig.Log?.Invoke($"update: running setup for {update.Version}");
        // Setup waits for RigControl to finish exiting (it hands fans back first), then installs and starts it again.
        Process.Start(new ProcessStartInfo(file, "/SILENT") { UseShellExecute = true });
        App.Instance!.Ui.TryEnqueue(App.Instance.Quit);
        return null;
    }
}
