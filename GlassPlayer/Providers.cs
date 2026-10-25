using System.Text.RegularExpressions;
using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlassPlayer;

public record Track(string Id, string Title, string Artist, string? Art, string? Preview, string Source, string? OpenUri, int DurationMs = 0)
{
    public string DurationText => DurationMs > 0 ? $"{DurationMs / 60000}:{DurationMs / 1000 % 60:00}" : "";
}

public interface IProvider
{
    string Name { get; }
    bool Enabled { get; }
    Task<List<Track>> SearchAsync(string query);
    /// <summary>Returns a URL the built-in player can stream, or null if the track must be opened elsewhere.</summary>
    Task<string?> ResolveAsync(Track t);
}

static class Config
{
    static readonly Dictionary<string, string> file = Load();
    static Dictionary<string, string> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.json"))) ?? new(); }
        catch { return new(); }
    }
    public static string? Get(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return file.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }
}

/// <summary>Downloaded songs are kept here briefly so replays are instant.</summary>
static class Cache
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer", "cache");

    public static string? Find(string key)
    {
        if (!Directory.Exists(Dir)) return null;
        return Directory.GetFiles(Dir, key + ".*").FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".tmp") && !f.EndsWith(".ytdl"));
    }

    public static string PathFor(string key, string ext)
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, key + "." + ext);
    }

    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir))
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays > 3) File.Delete(f);
        }
        catch { }
    }
}

/// <summary>yt-dlp helper: downloaded once into %LocalAppData%\GlassPlayer\tools and refreshed every few days.</summary>
static class Dlp
{
    static readonly HttpClient Http = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer", "tools");
    static readonly string Exe = Path.Combine(Dir, "yt-dlp.exe");
    static bool ready;

    public static async Task EnsureAsync()
    {
        if (ready) return;
        if (!File.Exists(Exe))
        {
            Directory.CreateDirectory(Dir);
            var bytes = await Http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe");
            await File.WriteAllBytesAsync(Exe, bytes);
        }
        else if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(Exe)).TotalDays > 5)
        {
            try { await RunAsync("-U"); } catch { }
            File.SetLastWriteTimeUtc(Exe, DateTime.UtcNow);
        }
        ready = true;
    }

    public static async Task<string> RunAsync(string args, bool allowPartial = false)
    {
        var psi = new ProcessStartInfo(Exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0 && !(allowPartial && output.Length > 0))
        {
            var line = ((await err).Split('\n').LastOrDefault(l => l.Contains("ERROR")) ?? "").Trim();
            if (line.Length > 160) line = line[..160];
            throw new Exception(line.Length > 0 ? line : "Couldn't load this track. Try another one.");
        }
        return output;
    }

    /// <summary>Downloads the audio of a page to the cache and returns the local file path.</summary>
    public static async Task<string> DownloadAsync(string url, string key)
    {
        var hit = Cache.Find(key);
        if (hit != null) return hit;
        await EnsureAsync();
        Directory.CreateDirectory(Cache.Dir);
        await RunAsync($"-f \"bestaudio[ext=m4a]/bestaudio/18/best\" --no-playlist --no-warnings -q --no-progress -o \"{Path.Combine(Cache.Dir, key)}.%(ext)s\" \"{url}\"");
        return Cache.Find(key) ?? throw new Exception("Couldn't download this track.");
    }
}

/// <summary>SoundCloud without any API key: reads the public web client id, like the website itself does.</summary>
class SoundCloud : IProvider
{
    static readonly HttpClient Http = Make();
    static HttpClient Make()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return h;
    }

    string? clientId;
    public string Name => "SoundCloud";
    public bool Enabled => true;

    async Task<string> ClientIdAsync(bool refresh = false)
    {
        if (clientId != null && !refresh) return clientId;
        var html = await Http.GetStringAsync("https://soundcloud.com");
        var scripts = Regex.Matches(html, "<script[^>]+src=\"(https://[^\"]+\\.js)\"").Select(m => m.Groups[1].Value).Reverse().ToList();
        foreach (var url in scripts)
        {
            try
            {
                var js = await Http.GetStringAsync(url);
                var m = Regex.Match(js, "client_id\\s*[:=]\\s*\"([A-Za-z0-9]{32})\"");
                if (m.Success) return clientId = m.Groups[1].Value;
            }
            catch { }
        }
        throw new Exception("Couldn't reach SoundCloud.");
    }

    public async Task<List<Track>> SearchAsync(string q)
    {
        List<Track> list = new();
        try { list = await ApiSearchAsync(q); } catch { }
        if (list.Count == 0) list = await DlpSearchAsync(q); // fallback if the web API changes
        return list;
    }

    async Task<List<Track>> ApiSearchAsync(string q)
    {
        for (int attempt = 0; ; attempt++)
        {
            var cid = await ClientIdAsync(attempt > 0);
            var res = await Http.GetAsync($"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(q)}&client_id={cid}&limit=30");
            if (attempt == 0 && !res.IsSuccessStatusCode) continue;
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var list = new List<Track>();
            foreach (var t in doc.RootElement.GetProperty("collection").EnumerateArray())
            {
                try
                {
                    if (t.TryGetProperty("policy", out var pol) && pol.GetString() is "SNIP" or "BLOCK") continue; // previews / blocked
                    string? progressive = null;
                    if (t.TryGetProperty("media", out var media) && media.TryGetProperty("transcodings", out var tr))
                        foreach (var x in tr.EnumerateArray())
                            if (x.GetProperty("format").GetProperty("protocol").GetString() == "progressive") progressive = x.GetProperty("url").GetString();
                    string auth = t.TryGetProperty("track_authorization", out var ta) ? ta.GetString() ?? "" : "";
                    string? art = t.TryGetProperty("artwork_url", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    if (art == null && t.GetProperty("user").TryGetProperty("avatar_url", out var av) && av.ValueKind == JsonValueKind.String) art = av.GetString();
                    art = art?.Replace("-large", "-t500x500");
                    string? page = t.TryGetProperty("permalink_url", out var pu) ? pu.GetString() : null;
                    list.Add(new Track(t.GetProperty("id").GetRawText(), t.GetProperty("title").GetString() ?? "",
                        t.GetProperty("user").GetProperty("username").GetString() ?? "", art,
                        progressive != null ? progressive + "|" + auth : "", Name, page,
                        t.TryGetProperty("duration", out var du) && du.TryGetInt32(out var dms) ? dms : 0));
                }
                catch { }
            }
            return list;
        }
    }

    async Task<List<Track>> DlpSearchAsync(string q)
    {
        await Dlp.EnsureAsync();
        var output = await Dlp.RunAsync($"-j --no-warnings \"scsearch10:{q.Replace("\"", "")}\"");
        var list = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var e = d.RootElement;
                string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                list.Add(new Track(e.GetProperty("id").ToString(), S("title") ?? "", S("uploader") ?? "", S("thumbnail"), "", Name, S("webpage_url"),
                    e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? (int)(du.GetDouble() * 1000) : 0));
            }
            catch { }
        }
        return list;
    }

    // Returns a local file path (downloaded into the cache) so playback is rock solid.
    public async Task<string?> ResolveAsync(Track t)
    {
        var key = "sc" + t.Id;
        var hit = Cache.Find(key);
        if (hit != null) return hit;
        if (!string.IsNullOrEmpty(t.Preview) && t.Preview.Contains('|'))
        {
            try
            {
                var parts = t.Preview.Split('|');
                var cid = await ClientIdAsync();
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{parts[0]}?client_id={cid}&track_authorization={parts[1]}"));
                var media = doc.RootElement.GetProperty("url").GetString()!;
                var path = Cache.PathFor(key, "mp3");
                using (var s = await Http.GetStreamAsync(media))
                using (var f = File.Create(path + ".tmp"))
                    await s.CopyToAsync(f);
                File.Move(path + ".tmp", path, true);
                return path;
            }
            catch { }
        }
        if (t.OpenUri != null) return await Dlp.DownloadAsync(t.OpenUri, key);
        return null;
    }
}

/// <summary>YouTube Music without any API key: public web search, yt-dlp for the audio.</summary>
class YouTubeMusic : IProvider
{
    static readonly HttpClient Http = new();
    public string Name => "YouTube Music";
    public bool Enabled => true;

    public async Task<List<Track>> SearchAsync(string q)
    {
        var list = new List<Track>();
        try { list = await InnerTubeSearchAsync(q); } catch { }
        if (list.Count == 0) list = await DlpSearchAsync(q);
        return list;
    }

    static async Task<List<Track>> InnerTubeSearchAsync(string q)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = $"1.{DateTime.UtcNow.AddDays(-1):yyyyMMdd}.01.00", hl = "en", gl = "US" } },
            query = q,
            @params = "EgWKAQIIAWoMEA4QChADEAQQCRAF" // songs only
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search?prettyPrint=false")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0");
        req.Headers.Add("Origin", "https://music.youtube.com");
        req.Headers.Add("Referer", "https://music.youtube.com/");
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<Track>();
        Walk(doc.RootElement, list);
        return list;
    }

    static void Walk(JsonElement e, List<Track> list)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("musicResponsiveListItemRenderer", out var r))
            {
                var t = ParseItem(r);
                if (t != null) list.Add(t);
                return;
            }
            foreach (var p in e.EnumerateObject()) Walk(p.Value, list);
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) Walk(x, list);
    }

    static Track? ParseItem(JsonElement r)
    {
        try
        {
            string? id = r.TryGetProperty("playlistItemData", out var pd) ? pd.GetProperty("videoId").GetString() : null;
            if (id == null) return null;
            var cols = r.GetProperty("flexColumns");
            var title = cols[0].GetProperty("musicResponsiveListItemFlexColumnRenderer").GetProperty("text").GetProperty("runs")[0].GetProperty("text").GetString() ?? "";
            var texts = cols[1].GetProperty("musicResponsiveListItemFlexColumnRenderer").GetProperty("text").GetProperty("runs").EnumerateArray()
                .Select(x => x.GetProperty("text").GetString() ?? "").Where(s => s.Trim() != "•" && s != "Song" && s != "Video").ToList();
            string? art = null;
            if (r.TryGetProperty("thumbnail", out var th))
            {
                var arr = th.GetProperty("musicThumbnailRenderer").GetProperty("thumbnail").GetProperty("thumbnails");
                art = arr[arr.GetArrayLength() - 1].GetProperty("url").GetString();
                if (art != null) art = Regex.Replace(art, "=w\\d+-h\\d+", "=w512-h512");
            }
            static bool IsDur(string s) => Regex.IsMatch(s.Trim(), "^\\d+(:\\d{2}){1,2}$");
            var dur = texts.LastOrDefault(IsDur);
            int ms = dur == null ? 0 : dur.Trim().Split(':').Select(int.Parse).Aggregate(0, (acc, s) => acc * 60 + s) * 1000;
            var artist = texts.FirstOrDefault(s => !IsDur(s)) ?? "";
            return new Track(id, title, artist, art, null, "YouTube Music", null, ms);
        }
        catch { return null; }
    }

    static async Task<List<Track>> DlpSearchAsync(string q)
    {
        await Dlp.EnsureAsync();
        var output = await Dlp.RunAsync($"--flat-playlist -j --no-warnings \"ytsearch12:{q.Replace("\"", "")}\"");
        var list = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var e = d.RootElement;
                string? art = null;
                if (e.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Array && th.GetArrayLength() > 0)
                    art = th[th.GetArrayLength() - 1].GetProperty("url").GetString();
                string artist = (e.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String) ? c.GetString()!
                              : (e.TryGetProperty("uploader", out var u) && u.ValueKind == JsonValueKind.String) ? u.GetString()! : "";
                list.Add(new Track(e.GetProperty("id").GetString()!, e.GetProperty("title").GetString() ?? "", artist.Replace(" - Topic", ""), art, null, "YouTube Music", null,
                    e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? (int)(du.GetDouble() * 1000) : 0));
            }
            catch { }
        }
        return list;
    }

    // Downloads the audio to the cache and returns the local file path.
    public Task<string?> ResolveAsync(Track t)
        => Dlp.DownloadAsync($"https://music.youtube.com/watch?v={t.Id}", "yt" + t.Id).ContinueWith(x => (string?)x.Result);
}

/// <summary>
/// Spotify Premium: signs in once with PKCE (no secret needed), then controls the Spotify app on this PC through the Web API.
/// </summary>
class Spotify : IProvider
{
    static readonly HttpClient Http = new();
    const string Redirect = "http://127.0.0.1:5543/callback";
    const string Scopes = "streaming user-read-email user-read-private user-read-playback-state user-modify-playback-state";
    static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassPlayer", "spotify-v3.bin");

    readonly string? id = Config.Get("SPOTIFY_CLIENT_ID");
    string? access, refresh; DateTime expires;

    public string Name => "Spotify";
    public bool Enabled => id != null;
    public static bool AutoLaunch = true;
    public WebPlayer? Web;   // built-in "Glass" Spotify device
    bool UseWeb;
    public Task<string> GetTokenAsync() => TokenAsync();
    public bool SignedIn => refresh != null || File.Exists(Store);

    // ---- sign-in ----
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    string? LoadRefresh()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Store), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    async Task<string> TokenAsync()
    {
        if (access != null && DateTime.UtcNow < expires) return access;
        refresh ??= LoadRefresh();
        if (refresh != null)
        {
            try { await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = id! }); return access!; }
            catch { refresh = null; }
        }
        await LoginAsync();
        return access!;
    }

    async Task ExchangeAsync(Dictionary<string, string> form)
    {
        var res = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        if (!res.IsSuccessStatusCode) throw new Exception("Spotify sign-in failed.");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        access = r.GetProperty("access_token").GetString();
        expires = DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32() - 60);
        if (r.TryGetProperty("refresh_token", out var rt))
        {
            refresh = rt.GetString();
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            File.WriteAllBytes(Store, ProtectedData.Protect(Encoding.UTF8.GetBytes(refresh!), null, DataProtectionScope.CurrentUser));
        }
    }

    async Task LoginAsync()
    {
        var verifier = B64(RandomNumberGenerator.GetBytes(64));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:5543/");
        listener.Start();
        Process.Start(new ProcessStartInfo("https://accounts.spotify.com/authorize" +
            $"?client_id={id}&response_type=code&redirect_uri={Uri.EscapeDataString(Redirect)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}&code_challenge_method=S256&code_challenge={challenge}&state={state}") { UseShellExecute = true });
        var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3));
        var q = ctx.Request.QueryString;
        var page = Encoding.UTF8.GetBytes("<body style='font-family:Segoe UI;background:#111;color:#fff;text-align:center;padding-top:20vh'><h2>Spotify connected</h2><p>You can close this tab and return to Glass.</p>");
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(page);
        ctx.Response.Close();
        if (q["state"] != state || q["code"] == null) throw new Exception("Spotify sign-in was cancelled.");
        await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = Redirect, ["client_id"] = id!, ["code_verifier"] = verifier });
    }

    // ---- API ----
    async Task<HttpResponseMessage> SendAsync(HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, "https://api.spotify.com/v1" + url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        if (m != HttpMethod.Get)
            req.Content = body != null ? new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") : new StringContent("");
        return await Http.SendAsync(req);
    }

    public async Task<List<Track>> SearchAsync(string q)
    {
        if (!Enabled) throw new Exception("Spotify needs a free Client ID. Add SPOTIFY_CLIENT_ID to config.json (see Settings).");
        var res = await SendAsync(HttpMethod.Get, $"/search?type=track&limit=10&q={Uri.EscapeDataString(q)}");
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<Track>();
        foreach (var t in doc.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray())
            list.Add(ToTrack(t));
        return list;
    }

    static Track ToTrack(JsonElement t)
    {
        var images = t.GetProperty("album").GetProperty("images");
        return new Track(t.GetProperty("id").GetString()!, t.GetProperty("name").GetString() ?? "",
            t.GetProperty("artists")[0].GetProperty("name").GetString() ?? "",
            images.GetArrayLength() > 0 ? images[0].GetProperty("url").GetString() : null,
            null, "Spotify", t.GetProperty("uri").GetString(), t.TryGetProperty("duration_ms", out var dm) ? dm.GetInt32() : 0);
    }

    public async Task<List<Track>> PlaylistAsync(string id)
    {
        var list = new List<Track>();
        string? next = $"/playlists/{id}/tracks?limit=100";
        while (next != null && list.Count < 300)
        {
            var res = await SendAsync(HttpMethod.Get, next);
            if (!res.IsSuccessStatusCode) throw new Exception("Spotify wouldn't share that playlist with Glass. Playlists you own usually work.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                try { if (it.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object) list.Add(ToTrack(t)); }
                catch { }
            }
            next = doc.RootElement.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Replace("https://api.spotify.com/v1", "") : null;
        }
        return list;
    }

    public Task<string?> ResolveAsync(Track t) => Task.FromResult<string?>(null); // played remotely, see PlayAsync

    // ---- account ----
    public record Profile(string Name, string? Plan, string? Country, string? Image);

    public async Task ConnectAsync() => await TokenAsync();

    public void SignOut()
    {
        access = null; refresh = null;
        try { File.Delete(Store); } catch { }
    }

    public async Task<Profile?> ProfileAsync()
    {
        if (!SignedIn) return null;
        var res = await SendAsync(HttpMethod.Get, "/me");
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var e = doc.RootElement;
        string? Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? img = e.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array && im.GetArrayLength() > 0 ? im[0].GetProperty("url").GetString() : null;
        return new Profile(Str("display_name") ?? Str("id") ?? "Spotify user", Str("product"), Str("country"), img);
    }

    // ---- playback (Spotify Connect) ----
    async Task<(string Id, string Name)> PickDeviceAsync()
    {
        bool launched = false;
        for (int attempt = 0; attempt < 9; attempt++)
        {
            var res = await SendAsync(HttpMethod.Get, "/me/player/devices");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var devs = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();
            deviceSummary = devs.Count == 0 ? "none" : string.Join(", ", devs.Select(d => $"{d.GetProperty("name").GetString()} ({d.GetProperty("type").GetString()})"));
            bool IsPc(JsonElement d) => d.GetProperty("type").GetString() == "Computer" && !d.GetProperty("is_restricted").GetBoolean();
            bool Active(JsonElement d) => d.GetProperty("is_active").GetBoolean();

            var pick = devs.FirstOrDefault(d => IsPc(d) && Active(d));
            if (pick.ValueKind == JsonValueKind.Undefined) pick = devs.FirstOrDefault(IsPc);

            // No Spotify on this PC yet: launch it and wait up to ~6s for it to appear before falling back to other devices.
            if (pick.ValueKind == JsonValueKind.Undefined && AutoLaunch && attempt < 6)
            {
                if (!launched) { launched = true; try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); } catch { } }
                await Task.Delay(1000);
                continue;
            }
            if (pick.ValueKind == JsonValueKind.Undefined) pick = devs.FirstOrDefault(Active);
            if (pick.ValueKind == JsonValueKind.Undefined && devs.Count > 0) pick = devs[0];
            if (pick.ValueKind != JsonValueKind.Undefined)
                return (pick.GetProperty("id").GetString()!, pick.GetProperty("name").GetString() ?? "Spotify");
            if (!AutoLaunch) break;
            await Task.Delay(1000);
        }
        throw new Exception("No Spotify device found. Open the Spotify app on this PC, then try again.");
    }

    string? deviceId;
    string deviceSummary = "none";

    public async Task<string> PlayAsync(string uri)
    {
        // 1) Preferred: play inside Glass itself (Spotify Web Playback SDK). No Spotify app needed.
        if (Web != null)
        {
            var dev = await Web.WaitReadyAsync(TimeSpan.FromSeconds(15));
            if (dev != null)
            {
                Web.ClearState();
                for (int i = 0; i < 3; i++)
                {
                    var r = await SendAsync(HttpMethod.Put, $"/me/player/play?device_id={dev}", new { uris = new[] { uri } });
                    if (r.StatusCode == HttpStatusCode.Forbidden) throw new Exception("Spotify Premium is required for playback.");
                    if (r.IsSuccessStatusCode) { deviceId = dev; UseWeb = true; return "Glass"; }
                    if (r.StatusCode != HttpStatusCode.NotFound) break;
                    await Task.Delay(1000);
                }
            }
        }
        UseWeb = false;

        // 2) Fallback: remote-control the Spotify desktop app.
        string name = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (dev, n) = await PickDeviceAsync();
            deviceId = dev; name = n;
            // Wake the device and make it the active one before sending the track.
            await SendAsync(HttpMethod.Put, "/me/player", new { device_ids = new[] { dev }, play = false });
            await Task.Delay(attempt == 0 ? 600 : 1500);
            var res = await SendAsync(HttpMethod.Put, $"/me/player/play?device_id={dev}", new { uris = new[] { uri } });
            if (res.StatusCode == HttpStatusCode.Forbidden) throw new Exception("Spotify Premium is required for playback.");
            if (res.IsSuccessStatusCode)
            {
                await Task.Delay(1500);
                var st = await StateAsync();
                if (st != null && (st.Value.Playing || st.Value.Uri == uri)) return name;
            }
            else await Task.Delay(1000);
        }
        throw new Exception("Couldn't start playback. Check that your Spotify Premium is active, then try again.");
    }

    async Task CmdAsync(string url, bool quiet = false)
    {
        if (deviceId != null) url += (url.Contains('?') ? "&" : "?") + "device_id=" + deviceId;
        var res = await SendAsync(HttpMethod.Put, url);
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.Forbidden && !quiet)
            throw new Exception($"Spotify error ({(int)res.StatusCode}).");
    }
    public Task PauseAsync() => UseWeb ? Web!.CallAsync("pause()") : CmdAsync("/me/player/pause");
    public Task ResumeAsync() => UseWeb ? Web!.CallAsync("resume()") : CmdAsync("/me/player/play");
    public Task SeekAsync(int ms) => UseWeb ? Web!.CallAsync($"seek({ms})") : CmdAsync($"/me/player/seek?position_ms={ms}");
    public Task VolumeAsync(int pct) => UseWeb ? Web!.CallAsync("setVolume(" + (pct / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + ")") : CmdAsync($"/me/player/volume?volume_percent={pct}", quiet: true);

    public async Task<(bool Playing, int Progress, int Duration, string? Uri)?> StateAsync()
    {
        if (UseWeb) return Web?.Current();
        var res = await SendAsync(HttpMethod.Get, "/me/player");
        if (res.StatusCode != HttpStatusCode.OK) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var e = doc.RootElement;
        if (!e.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        return (e.GetProperty("is_playing").GetBoolean(), e.GetProperty("progress_ms").GetInt32(),
                item.GetProperty("duration_ms").GetInt32(), item.GetProperty("uri").GetString());
    }
}

/// <summary>Songs imported from files on this PC.</summary>
class LocalFiles : IProvider
{
    public string Name => "Local";
    public bool Enabled => true;
    public Task<List<Track>> SearchAsync(string q) => Task.FromResult(new List<Track>());
    public Task<string?> ResolveAsync(Track t) => Task.FromResult<string?>(t.OpenUri);

    public static Track ToTrack(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split(" - ", 2);
        return parts.Length == 2
            ? new Track(path, parts[1].Trim(), parts[0].Trim(), null, null, "Local", path)
            : new Track(path, name, "This PC", null, null, "Local", path);
    }
}

/// <summary>Imports a playlist or song from a YouTube, YouTube Music or SoundCloud link.</summary>
static class Importer
{
    public static async Task<List<Track>> FromLinkAsync(string url)
    {
        bool sc = url.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase);
        await Dlp.EnsureAsync();
        var output = await Dlp.RunAsync($"-j {(sc ? "" : "--flat-playlist ")}--no-warnings --playlist-end 100 \"{url.Replace("\"", "")}\"", allowPartial: true);
        var list = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var e = d.RootElement;
                string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string? art = S("thumbnail");
                if (art == null && e.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Array && th.GetArrayLength() > 0)
                    art = th[th.GetArrayLength() - 1].GetProperty("url").GetString();
                int ms = e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? (int)(du.GetDouble() * 1000) : 0;
                string artist = (S("uploader") ?? S("channel") ?? "").Replace(" - Topic", "");
                list.Add(new Track(e.GetProperty("id").ToString(), S("title") ?? "", artist, art, sc ? "" : null,
                    sc ? "SoundCloud" : "YouTube Music", sc ? S("webpage_url") : null, ms));
            }
            catch { }
        }
        return list;
    }
}
