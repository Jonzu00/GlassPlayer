using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassPlayer;

record WebState(bool Paused, int Position, int Duration, string? Uri, DateTime At);

/// <summary>
/// Runs Spotify's Web Playback SDK inside a tiny hidden WebView2, so Glass becomes its own Spotify device ("Glass").
/// No Spotify desktop app is needed. Needs Spotify Premium.
/// </summary>
class WebPlayer
{
    readonly WebView2 web;
    readonly Spotify sp;
    readonly TaskCompletionSource<string> ready = new();
    bool started;
    WebState? last;
    string? deviceId;

    public WebPlayer(WebView2 web, Spotify sp) { this.web = web; this.sp = sp; }

    public async Task StartAsync()
    {
        if (started) return;
        started = true;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer");
            var site = Path.Combine(root, "site");
            Directory.CreateDirectory(site);
            File.WriteAllText(Path.Combine(site, "player.html"), Html);
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "webview"),
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("glass.local", site, CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.Navigate("https://glass.local/player.html");
        }
        catch (Exception ex) { ready.TrySetException(ex); }
    }

    public async Task<string?> WaitReadyAsync(TimeSpan timeout)
    {
        await StartAsync();
        var done = await Task.WhenAny(ready.Task, Task.Delay(timeout));
        if (done != ready.Task || ready.Task.IsFaulted) return null;
        return deviceId;
    }

    async void OnMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "need_token":
                    var token = await sp.GetTokenAsync();
                    web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { token }));
                    break;
                case "ready":
                    deviceId = m.GetProperty("device").GetString();
                    ready.TrySetResult(deviceId!);
                    break;
                case "error":
                    ready.TrySetException(new Exception(m.GetProperty("msg").GetString()));
                    break;
                case "state":
                    last = new WebState(m.GetProperty("paused").GetBoolean(), (int)m.GetProperty("position").GetDouble(),
                        (int)m.GetProperty("duration").GetDouble(), m.GetProperty("uri").GetString(), DateTime.UtcNow);
                    break;
            }
        }
        catch { }
    }

    public async Task CallAsync(string js)
    {
        if (web.CoreWebView2 != null) await web.CoreWebView2.ExecuteScriptAsync("player." + js);
    }

    public void ClearState() => last = null;

    public (bool Playing, int Progress, int Duration, string? Uri)? Current()
    {
        if (last is not { } s) return null;
        int pos = s.Paused ? s.Position : s.Position + (int)(DateTime.UtcNow - s.At).TotalMilliseconds;
        return (!s.Paused, Math.Min(pos, s.Duration), s.Duration, s.Uri);
    }

    const string Html = """
<!doctype html><html><body>
<script>
var player = null, tokenCb = null;
function post(o) { window.chrome.webview.postMessage(JSON.stringify(o)); }
window.chrome.webview.addEventListener('message', function (e) {
  if (e.data && e.data.token && tokenCb) { tokenCb(e.data.token); tokenCb = null; }
});
window.onSpotifyWebPlaybackSDKReady = function () {
  player = new Spotify.Player({
    name: 'Glass', volume: 0.8,
    getOAuthToken: function (cb) { tokenCb = cb; post({ type: 'need_token' }); }
  });
  player.addListener('ready', function (d) { post({ type: 'ready', device: d.device_id }); });
  ['initialization_error', 'authentication_error', 'account_error', 'playback_error'].forEach(function (n) {
    player.addListener(n, function (e) { post({ type: 'error', msg: n + ': ' + e.message }); });
  });
  player.addListener('player_state_changed', function (s) {
    if (!s) return;
    post({ type: 'state', paused: s.paused, position: s.position, duration: s.duration, uri: s.track_window.current_track.uri });
  });
  player.connect();
};
</script>
<script src="https://sdk.scdn.co/spotify-player.js"></script>
</body></html>
""";
}
