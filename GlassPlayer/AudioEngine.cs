using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace GlassPlayer;

/// <summary>
/// Plays songs. Uses VLC when it is ready (plays any format) and falls back to Windows' built-in player
/// if VLC is still loading, fails to start, or doesn't produce sound. Problems are logged to %LocalAppData%\GlassPlayer\audio.log.
/// </summary>
class AudioEngine
{
    readonly Dispatcher ui;
    LibVLC? lib;
    LibVLCSharp.Shared.MediaPlayer? vlc;
    System.Windows.Media.MediaPlayer? wpf;
    bool vlcReady, usingVlc, paused;
    string? current;
    int openId;
    double volume;

    public event Action? Ended;
    public event Action<string>? Failed;

    public AudioEngine(Dispatcher ui, double volume)
    {
        this.ui = ui;
        this.volume = volume;
        Task.Run(() => // VLC can take a while to load the first time, so never block the window on it
        {
            try
            {
                Core.Initialize();
                var l = new LibVLC("--no-video", "--quiet");
                l.Log += (_, e) => { if (e.Level == LogLevel.Error) Log(e.Message); };
                var p = new LibVLCSharp.Shared.MediaPlayer(l);
                p.EndReached += (_, _) => ui.InvokeAsync(() => { if (usingVlc) Ended?.Invoke(); });
                p.EncounteredError += (_, _) => ui.InvokeAsync(() => { if (usingVlc) Fallback("VLC reported an error"); });
                ui.InvokeAsync(() =>
                {
                    lib = l; vlc = p;
                    p.Volume = (int)Math.Round(this.volume * 100);
                    vlcReady = true;
                });
            }
            catch (Exception ex) { Log("VLC failed to start: " + ex); }
        });
    }

    static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "audio.log"), $"{DateTime.Now:HH:mm:ss} {msg}\n");
        }
        catch { }
    }

    public void Open(string pathOrUrl)
    {
        current = pathOrUrl;
        paused = false;
        int id = ++openId;
        StopCurrent();
        if (vlcReady && vlc != null && lib != null)
        {
            usingVlc = true;
            var type = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? FromType.FromLocation : FromType.FromPath;
            using var media = new Media(lib, pathOrUrl, type);
            if (!vlc.Play(media)) { Fallback("VLC refused to play"); return; }
            var t = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (id == openId && usingVlc && !paused && vlc != null && !vlc.IsPlaying && vlc.Time <= 0) Fallback("VLC did not start playing");
            };
            t.Start();
        }
        else PlayWpf(pathOrUrl);
    }

    void Fallback(string why)
    {
        Log("Using Windows player instead: " + why);
        try { vlc?.Stop(); } catch { }
        if (current != null) PlayWpf(current);
    }

    void PlayWpf(string path)
    {
        usingVlc = false;
        if (wpf == null)
        {
            wpf = new System.Windows.Media.MediaPlayer();
            wpf.MediaEnded += (_, _) => { if (!usingVlc) Ended?.Invoke(); };
            wpf.MediaFailed += (_, e) => { Log("Windows player failed: " + e.ErrorException.Message); Failed?.Invoke("Couldn't play this track: " + e.ErrorException.Message); };
        }
        wpf.Volume = volume;
        wpf.Open(new Uri(path));
        wpf.Play();
    }

    void StopCurrent()
    {
        try { vlc?.Stop(); } catch { }
        wpf?.Stop();
    }

    public void SetPaused(bool p)
    {
        paused = p;
        if (usingVlc) vlc?.SetPause(p);
        else if (p) wpf?.Pause();
        else wpf?.Play();
    }

    public void Stop() { openId++; paused = false; StopCurrent(); }

    public long TimeMs
    {
        get => usingVlc ? (vlc?.Time ?? 0) : (long)(wpf?.Position.TotalMilliseconds ?? 0);
        set
        {
            if (usingVlc) { if (vlc != null) vlc.Time = value; }
            else if (wpf != null) wpf.Position = TimeSpan.FromMilliseconds(value);
        }
    }

    public long LengthMs => usingVlc
        ? (vlc?.Length ?? 0)
        : (wpf != null && wpf.NaturalDuration.HasTimeSpan ? (long)wpf.NaturalDuration.TimeSpan.TotalMilliseconds : 0);

    public double Volume
    {
        set
        {
            volume = value;
            if (vlc != null) vlc.Volume = (int)Math.Round(value * 100);
            if (wpf != null) wpf.Volume = value;
        }
    }
}
