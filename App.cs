using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ChannelSurf
{
    public static class Program
    {
        static Mutex instance;
        [STAThread]
        public static void Main()
        {
            bool first;
            instance = new Mutex(true, "Local\\ChannelSurf.NativeBrowser", out first);
            if (!first)
            {
                IntPtr window = FindWindow(null, "Channel Surf");
                if (window != IntPtr.Zero) { ShowWindow(window, 9); SetForegroundWindow(window); }
                return;
            }
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                var app = new Application();
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    Log(e.Exception);
                    MessageBox.Show("Channel Surf hit a problem: " + e.Exception.Message, "Channel Surf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                };
                var shell = new Shell();
                app.Run(shell.Window);
            }
            catch (Exception e) { Log(e); MessageBox.Show(e.ToString(), "Channel Surf could not start"); }
            finally { instance.ReleaseMutex(); instance.Dispose(); }
        }
        public static void Log(Exception e)
        {
            try { string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelSurf"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "errors.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine); } catch { }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    }

    public sealed class ChannelRow : INotifyPropertyChanged
    {
        public Channel Channel { get; private set; }
        public string Name { get { return Channel.Name; } }
        public string Initials { get { return MakeInitials(Name); } }
        public string Subtitle { get { return String.Join("  ·  ", new[] { Channel.CountryText, Channel.CategoryText, Channel.LanguageText }.Where(s => !String.IsNullOrWhiteSpace(s) && s != "Unknown").ToArray()); } }
        bool favorite;
        public bool Favorite { get { return favorite; } set { favorite = value; Changed("Star"); Changed("StarColor"); } }
        public string Star { get { return favorite ? "★" : "☆"; } }
        public Brush StarColor { get { return favorite ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(142, 163, 190)); } }
        public ChannelRow(Channel channel, bool saved) { Channel = channel; favorite = saved; }
        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string name) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name)); }
        public static string MakeInitials(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "TV";
            var words = name.Split(new[] { ' ', '-', '.', '+' }, StringSplitOptions.RemoveEmptyEntries);
            return (words.Length > 1 ? words[0].Substring(0, 1) + words[1].Substring(0, 1) : name.Substring(0, Math.Min(2, name.Length))).ToUpperInvariant();
        }
    }

    public sealed class Shell
    {
        public Window Window { get; private set; }
        readonly PlaylistService library = new PlaylistService();
        readonly UserStateStore stateStore = new UserStateStore();
        readonly VlcController vlc = new VlcController();
        UserState state;
        List<ChannelRow> rows = new List<ChannelRow>();
        List<ChannelRow> visible = new List<ChannelRow>();
        Channel current;
        string view = "all";
        bool populating, loading, polling, requested, closed, recoveryNoticeShown;
        int requestVersion;
        DateTime requestedAt;
        readonly DispatcherTimer filterTimer = new DispatcherTimer();
        readonly DispatcherTimer pollTimer = new DispatcherTimer();
        readonly DispatcherTimer volumeTimer = new DispatcherTimer();
        HwndSource source;
        IntPtr handle;
        T Get<T>(string name) where T : class { return Window.FindName(name) as T; }
        void Text(string name, string text) { Get<TextBlock>(name).Text = text; }
        public Shell()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            try { Window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChannelSurf.ico"))); } catch { }
            state = stateStore.Load();
            filterTimer.Interval = TimeSpan.FromMilliseconds(170);
            filterTimer.Tick += delegate { filterTimer.Stop(); ApplyFilters(); };
            Get<TextBox>("SearchBox").TextChanged += delegate { Get<TextBlock>("SearchHint").Visibility = Get<TextBox>("SearchBox").Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; filterTimer.Stop(); filterTimer.Start(); };
            foreach (string name in new[] { "CountryFilter", "LanguageFilter", "CategoryFilter" }) Get<ComboBox>(name).SelectionChanged += delegate { if (!populating) ApplyFilters(); };
            Get<CheckBox>("AdultCheck").Checked += delegate { ApplyFilters(); };
            Get<CheckBox>("AdultCheck").Unchecked += delegate { ApplyFilters(); };
            Get<Button>("AllButton").Click += delegate { SwitchView("all"); };
            Get<Button>("FavoritesButton").Click += delegate { SwitchView("favorites"); };
            Get<Button>("RecentButton").Click += delegate { SwitchView("recent"); };
            Get<Button>("RefreshButton").Click += async delegate { await LoadAsync(true); };
            Get<Button>("ResetButton").Click += delegate { ResetFilters(); };
            Get<Button>("PreviousButton").Click += async delegate { await SurfAsync(-1); };
            Get<Button>("NextButton").Click += async delegate { await SurfAsync(1); };
            Get<Button>("PauseButton").Click += async delegate { await ControlAsync("pl_pause"); };
            Get<Button>("StopButton").Click += async delegate { ++requestVersion; requested = false; await ControlAsync("pl_stop"); };
            Get<Button>("RetryButton").Click += async delegate { if (current != null) await PlayAsync(current, Math.Max(0, Get<ComboBox>("StreamSelect").SelectedIndex)); };
            Get<Button>("ShowVlcButton").Click += delegate { if (vlc.IsRunning) vlc.ShowWindow(); else Text("StatusText", "Click a channel to open VLC."); };
            Get<Button>("FullscreenButton").Click += async delegate { await ControlAsync("fullscreen"); vlc.ShowWindow(); };
            Get<CheckBox>("ReconnectCheck").Unchecked += delegate { vlc.CancelRecovery(); };
            volumeTimer.Interval = TimeSpan.FromMilliseconds(140);
            volumeTimer.Tick += async delegate { volumeTimer.Stop(); if (vlc.IsRunning) { try { await vlc.SetVolumeAsync((int)Get<Slider>("VolumeSlider").Value); } catch (Exception e) { Text("StatusText", e.Message); } } };
            Get<Slider>("VolumeSlider").ValueChanged += delegate { volumeTimer.Stop(); volumeTimer.Start(); };
            Get<ComboBox>("StreamSelect").SelectionChanged += async delegate { if (!populating && current != null && Get<ComboBox>("StreamSelect").SelectedIndex >= 0) await PlayAsync(current, Get<ComboBox>("StreamSelect").SelectedIndex); };
            var list = Get<ListBox>("ChannelList");
            list.AddHandler(Button.ClickEvent, new RoutedEventHandler(FavoriteClick));
            list.PreviewMouseLeftButtonUp += async delegate(object sender, MouseButtonEventArgs e)
            {
                var origin = e.OriginalSource as DependencyObject;
                if (Ancestor<ButtonBase>(origin) != null) return;
                var item = Ancestor<ListBoxItem>(origin);
                if (item != null && item.DataContext is ChannelRow) { list.SelectedItem = item.DataContext; await PlayAsync(((ChannelRow)item.DataContext).Channel, 0); }
            };
            list.PreviewKeyDown += async delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && list.SelectedItem is ChannelRow) { e.Handled = true; await PlayAsync(((ChannelRow)list.SelectedItem).Channel, 0); } };
            Window.PreviewKeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Get<TextBox>("SearchBox").Focus(); Get<TextBox>("SearchBox").SelectAll(); e.Handled = true; }
                else if (e.Key == Key.Escape && Get<TextBox>("SearchBox").IsKeyboardFocused) { Get<TextBox>("SearchBox").Clear(); e.Handled = true; }
                else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.Key == Key.Right || e.SystemKey == Key.Right)) { await SurfAsync(1); e.Handled = true; }
                else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.Key == Key.Left || e.SystemKey == Key.Left)) { await SurfAsync(-1); e.Handled = true; }
            };
            Window.SourceInitialized += delegate { RegisterKeys(); };
            Window.Loaded += async delegate { await LoadAsync(false); Get<TextBox>("SearchBox").Focus(); };
            Window.Closed += delegate { closed = true; filterTimer.Stop(); pollTimer.Stop(); volumeTimer.Stop(); if (source != null) source.RemoveHook(Hook); for (int i = 1; i <= 3; i++) UnregisterHotKey(handle, i); vlc.Dispose(); };
            pollTimer.Interval = TimeSpan.FromSeconds(2);
            pollTimer.Tick += async delegate { await PollAsync(); };
            pollTimer.Start();
            SwitchView("all");
            PlaybackButtons(false);
        }
        static T Ancestor<T>(DependencyObject item) where T : DependencyObject
        {
            while (item != null) { if (item is T) return (T)item; if (item is Visual || item is System.Windows.Media.Media3D.Visual3D) item = VisualTreeHelper.GetParent(item); else item = LogicalTreeHelper.GetParent(item); }
            return null;
        }
        async Task LoadAsync(bool refresh)
        {
            if (loading) return;
            loading = true; Get<Button>("RefreshButton").IsEnabled = false;
            Text("StatusText", refresh ? "Updating the channel list…" : "Loading your channel library…");
            try
            {
                var result = await library.LoadAsync(refresh);
                if (closed) return;
                var saved = new HashSet<string>(state.FavoriteIds ?? new List<string>());
                rows = result.Channels.Select(c => new ChannelRow(c, saved.Contains(c.Id))).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                populating = true;
                FillFilter("CountryFilter", "All countries", rows.SelectMany(r => r.Channel.Countries));
                FillFilter("LanguageFilter", "All languages", rows.SelectMany(r => r.Channel.Languages));
                FillFilter("CategoryFilter", "All categories", rows.SelectMany(r => r.Channel.Categories));
                populating = false;
                Text("CatalogLabel", rows.Count.ToString("N0") + " channels" + (result.FromCache ? " · cached" : " · up to date"));
                ApplyFilters();
                Text("StatusText", String.IsNullOrWhiteSpace(result.Warning) ? "Library ready. Favorites and recently played are saved on this PC." : result.Warning);
            }
            catch (Exception e) { Program.Log(e); Text("StatusText", "Couldn't load channels: " + e.Message); Text("CatalogLabel", "Library unavailable"); Text("EmptyTitle", "Couldn't load your library"); Text("EmptyText", "Check your connection, then click Refresh."); Get<Border>("EmptyPanel").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
            finally { populating = false; loading = false; Get<Button>("RefreshButton").IsEnabled = true; }
        }
        void FillFilter(string control, string all, IEnumerable<string> values)
        {
            var combo = Get<ComboBox>(control); string previous = combo.SelectedItem as string;
            var items = new[] { all }.Concat(values.Where(v => !String.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)).ToList();
            combo.ItemsSource = items; combo.SelectedItem = items.Contains(previous) ? previous : all;
        }
        void SwitchView(string next)
        {
            view = next;
            foreach (var pair in new[] { new[] { "AllButton", "all" }, new[] { "FavoritesButton", "favorites" }, new[] { "RecentButton", "recent" } }) Get<Button>(pair[0]).Background = pair[1] == view ? new SolidColorBrush(Color.FromRgb(36, 63, 66)) : Brushes.Transparent;
            Text("ViewTitle", view == "all" ? "All channels" : view == "favorites" ? "Favorites" : "Recently played");
            ApplyFilters();
        }
        void ResetFilters()
        {
            populating = true;
            Get<TextBox>("SearchBox").Clear();
            foreach (string name in new[] { "CountryFilter", "LanguageFilter", "CategoryFilter" }) Get<ComboBox>(name).SelectedIndex = 0;
            populating = false; ApplyFilters();
        }
        void ApplyFilters()
        {
            if (populating) return;
            string country = FilterValue("CountryFilter"), language = FilterValue("LanguageFilter"), category = FilterValue("CategoryFilter");
            var terms = Get<TextBox>("SearchBox").Text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var recent = state.RecentIds ?? new List<string>();
            IEnumerable<ChannelRow> query = rows.Where(r => (Get<CheckBox>("AdultCheck").IsChecked == true || !r.Channel.IsNsfw)
                && (view != "favorites" || r.Favorite) && (view != "recent" || recent.Contains(r.Channel.Id))
                && (country == null || r.Channel.Countries.Contains(country)) && (language == null || r.Channel.Languages.Contains(language)) && (category == null || r.Channel.Categories.Contains(category))
                && terms.All(term => (r.Channel.SearchText ?? r.Name).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
            if (view == "recent") query = query.OrderBy(r => recent.IndexOf(r.Channel.Id));
            visible = query.ToList();
            Get<ListBox>("ChannelList").ItemsSource = visible;
            if (current != null) Get<ListBox>("ChannelList").SelectedItem = visible.FirstOrDefault(r => r.Channel.Id == current.Id);
            Text("MatchLabel", visible.Count.ToString("N0") + (visible.Count == 1 ? " channel" : " channels") + " · search, filter, or start watching");
            Text("EmptyTitle", view == "favorites" && !rows.Any(r => r.Favorite) ? "Your favorites go here" : view == "recent" && recent.Count == 0 ? "Start watching" : "No channels found");
            Text("EmptyText", view == "favorites" && !rows.Any(r => r.Favorite) ? "Click the star beside a channel to save it." : view == "recent" && recent.Count == 0 ? "Channels you play will appear here." : "Try a different search or reset your filters.");
            Get<Border>("EmptyPanel").Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("PreviousButton").IsEnabled = Get<Button>("NextButton").IsEnabled = visible.Count > 0;
        }
        string FilterValue(string name) { var c = Get<ComboBox>(name); return c.SelectedIndex > 0 ? c.SelectedItem as string : null; }
        void FavoriteClick(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button ?? Ancestor<Button>(e.OriginalSource as DependencyObject);
            var row = button == null ? null : button.Tag as ChannelRow;
            if (row == null) return;
            e.Handled = true; row.Favorite = !row.Favorite;
            var favoriteIds = new HashSet<string>(state.FavoriteIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (row.Favorite) favoriteIds.Add(row.Channel.Id); else favoriteIds.Remove(row.Channel.Id);
            state.FavoriteIds = favoriteIds.ToList(); SaveState();
            if (view == "favorites") ApplyFilters();
            Text("StatusText", row.Favorite ? row.Name + " added to favorites." : row.Name + " removed from favorites.");
        }
        void SaveState() { try { stateStore.Save(state); } catch (Exception e) { Program.Log(e); Text("StatusText", "Couldn't save your library preferences: " + e.Message); } }
        async Task SurfAsync(int direction)
        {
            if (visible.Count == 0) { Text("StatusText", "No channels in these results. Reset your filters to keep surfing."); return; }
            int index = current == null ? -1 : visible.FindIndex(r => r.Channel.Id == current.Id);
            int next = index < 0 ? (direction > 0 ? 0 : visible.Count - 1) : (index + direction + visible.Count) % visible.Count;
            await PlayAsync(visible[next].Channel, 0);
        }
        async Task PlayAsync(Channel channel, int streamIndex)
        {
            if (channel.Streams == null || channel.Streams.Count == 0) return;
            int version = ++requestVersion;
            current = channel; requested = true; requestedAt = DateTime.UtcNow;
            Text("NowTitle", channel.Name); Text("NowInitials", ChannelRow.MakeInitials(channel.Name));
            Text("NowSubtitle", String.Join(" · ", new[] { channel.CountryText, channel.CategoryText }.Where(s => !String.IsNullOrWhiteSpace(s)).ToArray()));
            populating = true; Get<ComboBox>("StreamSelect").ItemsSource = channel.Streams; Get<ComboBox>("StreamSelect").SelectedIndex = Math.Min(streamIndex, channel.Streams.Count - 1); Get<ComboBox>("StreamSelect").IsEnabled = channel.Streams.Count > 1; populating = false;
            Text("PlaybackStatus", "Opening in VLC…"); Text("StatusText", "Tuning to " + channel.Name + "…"); PlaybackButtons(true);
            var row = visible.FirstOrDefault(r => r.Channel.Id == channel.Id);
            if (row != null) { Get<ListBox>("ChannelList").SelectedItem = row; Get<ListBox>("ChannelList").ScrollIntoView(row); }
            try
            {
                await vlc.PlayAsync(channel, streamIndex);
                if (closed || version != requestVersion) return;
                await vlc.SetVolumeAsync((int)Get<Slider>("VolumeSlider").Value);
                if (closed || version != requestVersion) return;
                if (state.RecentIds == null) state.RecentIds = new List<string>();
                state.RecentIds.Remove(channel.Id); state.RecentIds.Insert(0, channel.Id); state.RecentIds = state.RecentIds.Take(100).ToList(); SaveState();
                Text("StatusText", "Tuned to " + channel.Name + ". Ctrl+Alt+PageUp / PageDown flips through your current results.");
            }
            catch (Exception e) { if (version != requestVersion || closed) return; Program.Log(e); requested = false; Text("PlaybackStatus", "Couldn't start VLC."); Text("StatusText", e.Message); }
        }
        void PlaybackButtons(bool enabled) { foreach (string n in new[] { "PauseButton", "StopButton", "RetryButton", "ShowVlcButton", "FullscreenButton" }) Get<Button>(n).IsEnabled = enabled; }
        async Task ControlAsync(string command)
        {
            try { await vlc.CommandAsync(command); await PollAsync(); } catch (Exception e) { Text("StatusText", e.Message); }
        }
        async Task PollAsync()
        {
            if (polling || current == null || closed) return;
            polling = true;
            int version = requestVersion;
            try
            {
                string playing = await vlc.GetStateAsync();
                if (closed || version != requestVersion) return;
                bool recovered = await vlc.RecoverIfNeededAsync(playing, requested && Get<CheckBox>("ReconnectCheck").IsChecked == true);
                if (closed || version != requestVersion) return;
                if (recovered)
                {
                    populating = true;
                    Get<ComboBox>("StreamSelect").SelectedIndex = vlc.CurrentStreamIndex;
                    populating = false;
                }
                string notice = vlc.LastPlaybackNotice;
                Text("PlaybackStatus", !String.IsNullOrEmpty(notice) ? notice : playing == "playing" ? "● Playing in VLC" : playing == "paused" ? "Paused" : requested && (DateTime.UtcNow - requestedAt).TotalSeconds < 18 ? "Connecting to the stream…" : requested ? "Stream unavailable. Try another source or channel." : "Stopped");
                if (!String.IsNullOrEmpty(notice)) { Text("StatusText", current.Name + ": " + notice); recoveryNoticeShown = true; }
                else if (recoveryNoticeShown) { Text("StatusText", playing == "playing" ? "Playing " + current.Name + "." : playing == "paused" ? "Playback paused." : "Playback stopped."); recoveryNoticeShown = false; }
                Get<Button>("PauseButton").Content = playing == "paused" ? "Resume" : "Pause";
            }
            catch { if ((DateTime.UtcNow - requestedAt).TotalSeconds > 18) Text("PlaybackStatus", "VLC isn't responding. Click Retry to reconnect."); }
            finally { polling = false; }
        }
        void RegisterKeys()
        {
            handle = new WindowInteropHelper(Window).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
            bool previous = RegisterHotKey(handle, 1, 0x4003, 0x21), next = RegisterHotKey(handle, 2, 0x4003, 0x22);
            RegisterHotKey(handle, 3, 0x4003, 0x20);
            if (!previous || !next) Text("HotkeyHint", "Shortcut in use. Use Alt+← / → here, or the buttons.");
        }
        IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312) { handled = true; int id = wParam.ToInt32(); Window.Dispatcher.BeginInvoke(new Action(async delegate { if (id == 1) await SurfAsync(-1); else if (id == 2) await SurfAsync(1); else if (id == 3) await ControlAsync("pl_pause"); })); }
            return IntPtr.Zero;
        }
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
