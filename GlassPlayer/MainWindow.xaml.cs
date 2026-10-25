using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassPlayer;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly AppSettings cfg = AppSettings.Load();
    AudioEngine? engine;
    long fallbackMs;
    bool importOpen;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer volDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, IProvider> providers = new();
    IProvider? provider;
    Spotify? spotify;
    bool updating, playing, advanced, polling, settingsOpen;

    // Spotify remote state: non-null while a Spotify track is the one playing
    Spotify? activeSp;
    Track? nowPlaying;
    int rProgress, rDuration;
    DateTime rStamp, lastPoll, playStart;
    bool rPlaying;

    public MainWindow()
    {
        InitializeComponent();

        _ = Task.Run(Cache.Cleanup);
        Spotify.AutoLaunch = cfg.LaunchSpotify;
        foreach (IProvider p in new IProvider[] { new YouTubeMusic(), new SoundCloud(), new Spotify(), new LocalFiles() })
            providers[p.Name] = p;
        spotify = (Spotify)providers["Spotify"];
        spotify.Web = new WebPlayer(Web, spotify);
        Loaded += (_, _) => { if (spotify is { SignedIn: true, Web: not null }) _ = spotify.Web.StartAsync(); }; // warm up so the first song starts fast

        AutoplayToggle.IsChecked = cfg.Autoplay;
        LaunchToggle.IsChecked = cfg.LaunchSpotify;
        VolumeToggle.IsChecked = cfg.RememberVolume;
        GlowToggle.IsChecked = cfg.AmbientGlow;
        if (!cfg.AmbientGlow) Ambient.Opacity = 0;
        if (cfg.RememberVolume) Volume.Value = cfg.Volume;

        SoundCloudBtn.IsChecked = true; // works with no keys

        StartEngine(); // VLC loads in the background so the window opens instantly

        seekDebounce.Tick += async (_, _) => { seekDebounce.Stop(); await Safe(() => activeSp?.SeekAsync((int)(Seek.Value * 1000)) ?? Task.CompletedTask); };
        volDebounce.Tick += async (_, _) => { volDebounce.Stop(); await Safe(() => activeSp?.VolumeAsync((int)(Volume.Value * 100)) ?? Task.CompletedTask); };

        timer.Tick += async (_, _) =>
        {
            if (activeSp != null) { await RemoteTickAsync(activeSp); return; }
            if (engine == null) return;
            long len = engine.LengthMs > 0 ? engine.LengthMs : fallbackMs;
            if (len <= 0) return;
            long pos = Math.Max(0, engine.TimeMs);
            updating = true;
            Seek.Maximum = len / 1000.0;
            if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
            Cur.Text = Fmt(TimeSpan.FromMilliseconds(pos));
            Dur.Text = Fmt(TimeSpan.FromMilliseconds(len));
            updating = false;
        };
        timer.Start();

        PreviewKeyDown += (_, e) => { if (e.Key != Key.Escape) return; if (importOpen) ToggleImport(false); else if (settingsOpen) ToggleSettings(false); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        int dark = 1, round = 2, acrylic = 3;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);    // immersive dark mode
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // rounded window corners
        DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4); // acrylic backdrop (Windows 11 22H2+)
    }

    bool closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closing)
        {
            e.Cancel = true; // play the exit animation first, then close for real
            closing = true;
            Anim(Root, OpacityProperty, 0, 240);
            Anim(Ambient, OpacityProperty, 0, 240);
            Anim(Overlay, OpacityProperty, 0, 240);
            Anim(RootScale, ScaleTransform.ScaleXProperty, .95, 260);
            Anim(RootScale, ScaleTransform.ScaleYProperty, .95, 260);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        cfg.Volume = Volume.Value;
        cfg.Save();
        base.OnClosed(e);
        Environment.Exit(0); // make sure no background audio/web processes keep Glass alive
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Status.Text = ex.Message; }
    }

    void StartEngine()
    {
        var eng = new AudioEngine(Dispatcher, Volume.Value);
        eng.Ended += () =>
        {
            if (activeSp != null) return;
            if ((DateTime.UtcNow - playStart).TotalSeconds < 3) { Status.Text = "Couldn't play this track. Try another one."; SetPlaying(false); return; }
            if (cfg.Autoplay) Step(1); else SetPlaying(false);
        };
        eng.Failed += msg => { Status.Text = msg; SetPlaying(false); };
        engine = eng;
    }

    // ---- animation helpers ----
    static void Anim(IAnimatable target, DependencyProperty prop, double to, int ms = 350, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    static void Fade(UIElement el, double from, double to, int ms)
        => el.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    async Task AnimateResultsAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < Math.Min(List.Items.Count, 14); i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c) continue;
            var tt = new TranslateTransform(0, 18);
            c.RenderTransform = tt;
            var begin = TimeSpan.FromMilliseconds(i * 45);
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    // ---- smooth wheel scrolling for the song list ----
    ScrollViewer? listScroll;
    double scrollTarget;
    bool scrolling;

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }

    void List_PreviewMouseWheel(object s, MouseWheelEventArgs e)
    {
        listScroll ??= FindScroll(List);
        if (listScroll == null) return;
        e.Handled = true;
        if (!scrolling) scrollTarget = listScroll.VerticalOffset;
        scrollTarget = Math.Clamp(scrollTarget - e.Delta * 0.9, 0, listScroll.ScrollableHeight);
        if (!scrolling) { scrolling = true; CompositionTarget.Rendering += ScrollStep; }
    }

    void ScrollStep(object? s, EventArgs e)
    {
        var sv = listScroll!;
        double diff = scrollTarget - sv.VerticalOffset;
        if (Math.Abs(diff) < 0.5)
        {
            sv.ScrollToVerticalOffset(scrollTarget);
            CompositionTarget.Rendering -= ScrollStep;
            scrolling = false;
            return;
        }
        sv.ScrollToVerticalOffset(sv.VerticalOffset + diff * 0.18);
    }

    // ---- Spotify progress: poll the API about every 1.5s and interpolate in between ----
    async Task RemoteTickAsync(Spotify sp)
    {
        var now = DateTime.UtcNow;
        if (!polling && (now - lastPoll).TotalMilliseconds > 1500)
        {
            polling = true; lastPoll = now;
            try
            {
                if (await sp.StateAsync() is { } st && (st.Uri == nowPlaying?.OpenUri || (DateTime.UtcNow - playStart).TotalSeconds > 3))
                {
                    rPlaying = st.Playing; rProgress = st.Progress; rDuration = st.Duration; rStamp = DateTime.UtcNow;
                    SetPlaying(st.Playing);
                }
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            polling = false;
        }
        if (rDuration <= 0) return;
        int pos = rPlaying ? Math.Min(rProgress + (int)(DateTime.UtcNow - rStamp).TotalMilliseconds, rDuration) : rProgress;
        updating = true;
        Seek.Maximum = rDuration / 1000.0;
        if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
        Cur.Text = Fmt(TimeSpan.FromMilliseconds(pos));
        Dur.Text = Fmt(TimeSpan.FromMilliseconds(rDuration));
        updating = false;
        if (rPlaying && pos >= rDuration - 1500 && !advanced) { advanced = true; if (cfg.Autoplay) Step(1); }
    }

    // ---- search ----
    void Provider_Checked(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { Tag: string name } && providers.TryGetValue(name, out var p)) provider = p;
        if (SearchBox.Text.Length > 0) _ = SearchAsync();
    }

    void Search_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) _ = SearchAsync(); }

    async Task SearchAsync()
    {
        if (provider == null || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        Status.Text = provider is Spotify { SignedIn: false } ? "Approve Glass in your browser to connect Spotify…" : "Searching…";
        try
        {
            var results = await provider.SearchAsync(SearchBox.Text.Trim());
            List.ItemsSource = results;
            Status.Text = results.Count == 0 ? "No results. Try different words." : "";
            _ = AnimateResultsAsync();
        }
        catch (Exception ex) { Status.Text = "Search failed: " + ex.Message; }
    }

    void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is Track t) _ = PlayAsync(t);
    }

    // ---- playback ----
    async Task PlayAsync(Track t)
    {
        nowPlaying = t; advanced = false;
        fallbackMs = t.DurationMs;
        updating = true; Seek.Maximum = 1; Seek.Value = 0; Cur.Text = "0:00"; Dur.Text = t.DurationMs > 0 ? Fmt(TimeSpan.FromMilliseconds(t.DurationMs)) : "0:00"; updating = false;
        TitleText.Text = t.Title;
        ArtistText.Text = t.Artist;
        Fade(TitleText, 0, 1, 450); Fade(ArtistText, 0, 1, 600); Fade(ArtBox, .25, 1, 550);
        if (t.Art != null)
        {
            var img = new BitmapImage(new Uri(t.Art));
            ArtBrush.ImageSource = img;
            Glow.Source = img;
            Fade(Glow, 0, .6, 1000);
        }
        else { ArtBrush.ImageSource = null; Glow.Source = null; }
        try
        {
            Status.Text = "";
            if (t.Source == "Spotify" && providers["Spotify"] is Spotify sp)
            {
                engine?.Stop();
                Status.Text = "Starting Spotify…";
                var device = await sp.PlayAsync(t.OpenUri!);
                playStart = DateTime.UtcNow;
                activeSp = sp; rPlaying = true; rProgress = 0; rDuration = 0; rStamp = DateTime.UtcNow; lastPoll = DateTime.MinValue;
                SetPlaying(true);
                Status.Text = $"Playing on {device}";
                await Safe(() => sp.VolumeAsync((int)(Volume.Value * 100)));
            }
            else
            {
                if (activeSp != null) { var old = activeSp; activeSp = null; await Safe(old.PauseAsync); }
                Status.Text = "Loading…";
                var url = await providers[t.Source].ResolveAsync(t);
                if (url == null) { Status.Text = "This track can't be streamed."; return; }
                engine!.Open(url);
                playStart = DateTime.UtcNow;
                SetPlaying(true);
                Status.Text = "";
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; SetPlaying(false); }
    }

    void SetPlaying(bool on)
    {
        playing = on;
        PlayBtn.Content = on ? "\uE769" : "\uE768";
        double scale = on ? 1.0 : 0.88; // cover art swells while playing, settles when paused
        Anim(ArtScale, ScaleTransform.ScaleXProperty, scale, 500);
        Anim(ArtScale, ScaleTransform.ScaleYProperty, scale, 500);
    }

    async void PlayPause_Click(object s, RoutedEventArgs e)
    {
        if (List.SelectedItem == null) return;
        bool resume = !playing;
        SetPlaying(resume);
        if (activeSp != null)
        {
            rPlaying = resume; rStamp = DateTime.UtcNow; lastPoll = DateTime.UtcNow;
            var sp = activeSp;
            await Safe(() => resume ? sp.ResumeAsync() : sp.PauseAsync());
        }
        else engine?.SetPaused(!resume);
    }

    void Step(int d)
    {
        int n = List.Items.Count;
        if (n == 0) return;
        List.SelectedIndex = ((List.SelectedIndex + d) % n + n) % n;
    }
    void Next_Click(object s, RoutedEventArgs e) => Step(1);
    void Prev_Click(object s, RoutedEventArgs e) => Step(-1);

    void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating) return;
        if (activeSp != null)
        {
            rProgress = (int)(e.NewValue * 1000); rStamp = DateTime.UtcNow; lastPoll = DateTime.UtcNow;
            seekDebounce.Stop(); seekDebounce.Start();
        }
        else if (engine != null) engine.TimeMs = (long)(e.NewValue * 1000);
    }

    void Volume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (engine != null) engine.Volume = e.NewValue;
        if (activeSp != null) { volDebounce.Stop(); volDebounce.Start(); }
    }

    // ---- import ----
    void ShowTracks(List<Track> tracks, string message)
    {
        List.ItemsSource = tracks;
        Status.Text = message;
        listScroll?.ScrollToTop();
        _ = AnimateResultsAsync();
    }

    void Import_Click(object s, RoutedEventArgs e) => ToggleImport(true);
    void ImportDim_Click(object s, MouseButtonEventArgs e) => ToggleImport(false);
    void Import_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) ImportGo_Click(s, e); }

    void ToggleImport(bool open)
    {
        importOpen = open;
        if (open)
        {
            ImportOverlay.Visibility = Visibility.Visible;
            ImportStatus.Text = "";
            Anim(ImportCard, OpacityProperty, 1, 300);
            Anim(ImportScale, ScaleTransform.ScaleXProperty, 1, 380);
            Anim(ImportScale, ScaleTransform.ScaleYProperty, 1, 380);
            ImportBox.Focus();
        }
        else
        {
            Anim(ImportCard, OpacityProperty, 0, 200);
            Anim(ImportScale, ScaleTransform.ScaleXProperty, .94, 220);
            Anim(ImportScale, ScaleTransform.ScaleYProperty, .94, 220, () => { if (!importOpen) ImportOverlay.Visibility = Visibility.Collapsed; });
        }
    }

    void ImportFiles_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        var tracks = dlg.FileNames.Select(LocalFiles.ToTrack).ToList();
        ShowTracks(tracks, $"Imported {tracks.Count} file{(tracks.Count == 1 ? "" : "s")}");
        ToggleImport(false);
    }

    async void ImportGo_Click(object s, RoutedEventArgs e)
    {
        var link = ImportBox.Text.Trim();
        if (link.Length == 0) return;
        ImportGo.IsEnabled = false;
        ImportStatus.Text = "Importing…";
        try
        {
            List<Track> tracks;
            var sp = Regex.Match(link, "playlist[/:]([A-Za-z0-9]+)");
            if (link.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            {
                if (!sp.Success) throw new Exception("For Spotify, paste a playlist link.");
                if (!spotify!.Enabled) throw new Exception("Spotify needs a Client ID first (see Settings).");
                tracks = await spotify.PlaylistAsync(sp.Groups[1].Value);
            }
            else if (Regex.IsMatch(link, "youtube\\.com|youtu\\.be|soundcloud\\.com", RegexOptions.IgnoreCase))
                tracks = await Importer.FromLinkAsync(link);
            else
                throw new Exception("Paste a link from YouTube, YouTube Music, SoundCloud or Spotify.");

            if (tracks.Count == 0) ImportStatus.Text = "Nothing found at that link.";
            else
            {
                ShowTracks(tracks, $"Imported {tracks.Count} song{(tracks.Count == 1 ? "" : "s")}");
                ImportBox.Text = "";
                ToggleImport(false);
            }
        }
        catch (Exception ex) { ImportStatus.Text = ex.Message; }
        ImportGo.IsEnabled = true;
    }

    // ---- settings sheet ----
    void Settings_Click(object s, RoutedEventArgs e) => ToggleSettings(!settingsOpen);
    void CloseSettings_Click(object s, RoutedEventArgs e) => ToggleSettings(false);
    void Dim_Click(object s, MouseButtonEventArgs e) => ToggleSettings(false);

    async void ToggleSettings(bool open)
    {
        settingsOpen = open;
        if (open)
        {
            Overlay.Visibility = Visibility.Visible;
            Anim(Dim, OpacityProperty, 1, 300);
            Anim(Sheet, OpacityProperty, 1, 350);
            Anim(SheetScale, ScaleTransform.ScaleXProperty, 1, 450);
            Anim(SheetScale, ScaleTransform.ScaleYProperty, 1, 450);
            await RefreshAccountsAsync();
        }
        else
        {
            Anim(Dim, OpacityProperty, 0, 250);
            Anim(Sheet, OpacityProperty, 0, 250);
            Anim(SheetScale, ScaleTransform.ScaleXProperty, .94, 280);
            Anim(SheetScale, ScaleTransform.ScaleYProperty, .94, 280, () => { if (!settingsOpen) Overlay.Visibility = Visibility.Collapsed; });
        }
    }

    void SetAvatar(string? url, string initial)
    {
        AvatarInitial.Text = initial;
        if (url != null)
        {
            try { AvatarBrush.ImageSource = new BitmapImage(new Uri(url)); AvatarInitial.Visibility = Visibility.Collapsed; return; }
            catch { }
        }
        AvatarBrush.ImageSource = null;
        AvatarInitial.Visibility = Visibility.Visible;
    }

    async Task RefreshAccountsAsync()
    {
        SoundCloudDetail.Text = "Work out of the box. No sign-in or keys needed.";

        if (spotify == null || !spotify.Enabled)
        {
            SpotifyName.Text = "Spotify";
            SpotifyDetail.Text = "Add SPOTIFY_CLIENT_ID to config.json to turn it on.";
            SpotifyAction.Visibility = Visibility.Collapsed;
            SetAvatar(null, "S");
            return;
        }
        SpotifyAction.Visibility = Visibility.Visible;
        if (!spotify.SignedIn)
        {
            SpotifyName.Text = "Not connected";
            SpotifyDetail.Text = "Connect your Premium account to play full tracks.";
            SpotifyAction.Content = "Connect";
            SetAvatar(null, "S");
            return;
        }

        SpotifyAction.Content = "Sign out";
        SpotifyName.Text = "Loading…";
        SpotifyDetail.Text = "";
        try
        {
            var pr = await spotify.ProfileAsync();
            if (pr == null)
            {
                SpotifyName.Text = "Connected";
                SpotifyDetail.Text = "Couldn't load your profile. Sign out and connect again.";
                SetAvatar(null, "S");
                return;
            }
            SpotifyName.Text = pr.Name;
            string plan = pr.Plan switch
            {
                "premium" => "Spotify Premium",
                null => "Connected",
                _ => "Free plan. Playback needs Premium"
            };
            SpotifyDetail.Text = pr.Country != null ? $"{plan} ({pr.Country})" : plan;
            SetAvatar(pr.Image, pr.Name.Length > 0 ? pr.Name[..1].ToUpper() : "S");
        }
        catch (Exception ex) { SpotifyName.Text = "Connected"; SpotifyDetail.Text = ex.Message; }
    }

    async void SpotifyAction_Click(object s, RoutedEventArgs e)
    {
        if (spotify == null) return;
        if (spotify.SignedIn)
        {
            if (activeSp != null) { await Safe(spotify.PauseAsync); activeSp = null; SetPlaying(false); }
            spotify.SignOut();
            await RefreshAccountsAsync();
            return;
        }
        SpotifyAction.IsEnabled = false;
        SpotifyDetail.Text = "Approve Glass in your browser…";
        try { await spotify.ConnectAsync(); }
        catch (Exception ex) { SpotifyDetail.Text = ex.Message; SpotifyAction.IsEnabled = true; return; }
        SpotifyAction.IsEnabled = true;
        await RefreshAccountsAsync();
    }

    void Setting_Click(object s, RoutedEventArgs e)
    {
        if (s is not ToggleButton { Tag: string key } tb) return;
        bool on = tb.IsChecked == true;
        switch (key)
        {
            case "Autoplay": cfg.Autoplay = on; break;
            case "Launch": cfg.LaunchSpotify = on; Spotify.AutoLaunch = on; break;
            case "Volume": cfg.RememberVolume = on; break;
            case "Glow": cfg.AmbientGlow = on; Anim(Ambient, OpacityProperty, on ? 1 : 0, 600); break;
        }
        cfg.Save();
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
