using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ChannelSurf
{
    // A private VLC instance keeps the user's other VLC windows and settings separate.
    public sealed class VlcController : IDisposable
    {
        private readonly object stateLock = new object();
        private readonly SemaphoreSlim playGate = new SemaphoreSlim(1, 1);
        private readonly string password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        private readonly string sessionDirectory = Path.Combine(Path.GetTempPath(), "ChannelSurf-" + Guid.NewGuid().ToString("N"));
        private Process ownedProcess;
        private int port;
        private int generation;
        private int disposed;
        private bool ready;
        private string lastPlaylist;
        private readonly Stopwatch playbackClock = Stopwatch.StartNew();
        private readonly Func<long> clock;
        private Channel currentChannel;
        private int currentStreamIndex;
        private bool wantsPlayback, manuallyPaused, healthyPlaybackSeen, recoveryEnabled = true, recoveryInFlight;
        private int recoveryAttempts, recoveryEpoch, lastStatusGeneration = -1;
        private long healthySince = -1, nextRecoveryAt = -1;
        private string observedState = "stopped", playbackNotice = "";
        private static readonly int[] RecoveryDelays = new int[] { 4, 8, 16 };
        private readonly object diagnosticLock = new object();

        public string ExecutablePath { get; private set; }
        public string LastPlaybackNotice { get { lock (stateLock) { return playbackNotice; } } }
        public int CurrentStreamIndex { get { lock (stateLock) { return currentStreamIndex; } } }

        public bool IsRunning
        {
            get
            {
                lock (stateLock) { return ProcessIsRunning(ownedProcess); }
            }
        }

        public VlcController() : this(null) { }

        internal VlcController(Func<long> monotonicClock)
        {
            ExecutablePath = FindVlc();
            clock = monotonicClock ?? delegate { return playbackClock.ElapsedMilliseconds; };
        }

        public async Task PlayAsync(Channel channel, int streamIndex = 0)
        {
            ThrowIfDisposed();
            if (channel == null) throw new ArgumentNullException("channel");
            if (channel.Streams == null || streamIndex < 0 || streamIndex >= channel.Streams.Count)
                throw new ArgumentException("This channel has no usable stream.", "streamIndex");
            ChannelStream stream = channel.Streams[streamIndex];
            ValidateStreamUrl(stream == null ? null : stream.Url);
            int requestGeneration;
            lock (stateLock)
            {
                requestGeneration = Interlocked.Increment(ref generation);
                CancelPendingRecoveryLocked();
                currentChannel = channel;
                currentStreamIndex = streamIndex;
                wantsPlayback = true;
                manuallyPaused = healthyPlaybackSeen = false;
                recoveryAttempts = 0;
                healthySince = -1;
                observedState = "stopped";
                lastStatusGeneration = -1;
                playbackNotice = "";
            }
            LogPlayback("select", channel, streamIndex, "opening", 0);
            await playGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (requestGeneration != Volatile.Read(ref generation)) return;
                await EnsureStartedAsync().ConfigureAwait(false);
                if (requestGeneration != Volatile.Read(ref generation)) return;
                await TuneAsync(channel, stream, delegate { return requestGeneration == Volatile.Read(ref generation) && Volatile.Read(ref disposed) == 0; }).ConfigureAwait(false);
            }
            finally { playGate.Release(); }
        }

        private async Task<bool> TuneAsync(Channel channel, ChannelStream stream, Func<bool> stillCurrent)
        {
            Directory.CreateDirectory(sessionDirectory);
            string playlistPath = Path.Combine(sessionDirectory, Guid.NewGuid().ToString("N") + ".m3u8");
            bool submitted = false;
            try
            {
                File.WriteAllText(playlistPath, BuildPlaylist(channel, stream), new UTF8Encoding(false));
                await QueryAsync("command=pl_empty", 3000).ConfigureAwait(false);
                if (!stillCurrent()) return false;
                // A timeout does not prove VLC rejected the command. Keep any
                // submitted playlist available until a later tune or disposal.
                submitted = true;
                string previousPlaylist = lastPlaylist;
                lastPlaylist = playlistPath;
                await QueryAsync("command=in_play&input=" + Uri.EscapeDataString(new Uri(playlistPath).AbsoluteUri)
                    + "&name=" + Uri.EscapeDataString(CleanLine(channel.Name)), 5000).ConfigureAwait(false);
                if (!stillCurrent()) return false;
                DeleteFile(previousPlaylist);
                return true;
            }
            finally { if (!submitted) DeleteFile(playlistPath); }
        }

        public async Task CommandAsync(string command)
        {
            ThrowIfDisposed();
            if (command != "pl_pause" && command != "pl_stop" && command != "pl_play"
                && command != "pl_forcepause" && command != "pl_forceresume" && command != "fullscreen")
                throw new ArgumentException("Unsupported playback command.", "command");
            bool changesPlayback = command != "fullscreen";
            int commandGeneration = Volatile.Read(ref generation);
            if (changesPlayback)
            {
                lock (stateLock)
                {
                    commandGeneration = Interlocked.Increment(ref generation);
                    CancelPendingRecoveryLocked();
                    playbackNotice = "";
                    lastStatusGeneration = -1;
                    healthySince = -1;
                    if (command == "pl_stop") { wantsPlayback = false; healthyPlaybackSeen = false; manuallyPaused = false; }
                    else if (command == "pl_forcepause") manuallyPaused = true;
                    else if (command == "pl_pause") manuallyPaused = !(manuallyPaused || observedState == "paused");
                    else { manuallyPaused = false; wantsPlayback = currentChannel != null; }
                }
            }
            await playGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (changesPlayback && commandGeneration != Volatile.Read(ref generation)) return;
                if (!IsRunning) return;
                await QueryAsync("command=" + Uri.EscapeDataString(command), 3000).ConfigureAwait(false);
                LogPlayback("manual-" + command, currentChannel, CurrentStreamIndex, command == "pl_stop" ? "stopped" : "requested", recoveryAttempts);
            }
            finally { playGate.Release(); }
        }

        public async Task SetVolumeAsync(int percent)
        {
            ThrowIfDisposed();
            percent = Math.Max(0, Math.Min(100, percent));
            await playGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!IsRunning) return;
                int rawVolume = (int)Math.Round(percent * 256.0 / 100.0);
                await QueryAsync("command=volume&val=" + rawVolume.ToString(CultureInfo.InvariantCulture), 3000).ConfigureAwait(false);
            }
            finally { playGate.Release(); }
        }

        public async Task<string> GetStateAsync()
        {
            ThrowIfDisposed();
            int statusGeneration = Volatile.Read(ref generation);
            string state = IsRunning ? ReadState(await QueryAsync(null, 2500).ConfigureAwait(false)) : "stopped";
            lock (stateLock) { lastStatusGeneration = statusGeneration; }
            return state;
        }

        private static string ReadState(string xml)
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersInDocument = 1024 * 1024;
            using (StringReader text = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(text, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "state") continue;
                    string state = reader.ReadElementContentAsString();
                    return state == "playing" || state == "paused" ? state : "stopped";
                }
            }
            return "stopped";
        }

        public void CancelRecovery()
        {
            lock (stateLock)
            {
                recoveryEnabled = false;
                CancelPendingRecoveryLocked();
                playbackNotice = "";
            }
        }

        private void CancelPendingRecoveryLocked()
        {
            recoveryEpoch++;
            nextRecoveryAt = -1;
        }

        // Polling schedules deadlines without waiting through the backoff. The UI
        // can display the notice immediately and manual controls remain responsive.
        public async Task<bool> RecoverIfNeededAsync(string state, bool enabled = true)
        {
            ThrowIfDisposed();
            int retryGeneration, retryEpoch, attempt, streamIndex;
            Channel channel;
            lock (stateLock)
            {
                if (!enabled)
                {
                    recoveryEnabled = false;
                    CancelPendingRecoveryLocked();
                    playbackNotice = "";
                }
                else recoveryEnabled = true;
                // A status request begun before a manual action belongs to the
                // previous intent and must not schedule playback for the new one.
                if (lastStatusGeneration != generation) return false;
                long now = clock();
                string previousState = observedState;
                observedState = state;
                if (state == "playing")
                {
                    if (previousState == "paused") manuallyPaused = false;
                    if (wantsPlayback && !manuallyPaused)
                    {
                        healthyPlaybackSeen = true;
                        if (healthySince < 0) healthySince = now;
                        if (now - healthySince >= 60000) recoveryAttempts = 0;
                    }
                    CancelPendingRecoveryLocked();
                    playbackNotice = "";
                    return false;
                }
                healthySince = -1;
                if (state == "paused")
                {
                    manuallyPaused = true;
                    CancelPendingRecoveryLocked();
                    playbackNotice = "";
                    return false;
                }
                if (!enabled || !wantsPlayback || manuallyPaused || !healthyPlaybackSeen || currentChannel == null)
                    return false;
                if (recoveryInFlight) return false;
                if (recoveryAttempts >= RecoveryDelays.Length)
                {
                    playbackNotice = "Stream stopped. Automatic reconnect failed after 3 attempts. Try another source or channel.";
                    return false;
                }
                if (nextRecoveryAt < 0) nextRecoveryAt = now + RecoveryDelays[recoveryAttempts] * 1000L;
                if (now < nextRecoveryAt)
                {
                    long seconds = Math.Max(1, (nextRecoveryAt - now + 999) / 1000);
                    playbackNotice = "Stream interrupted. Reconnecting in " + seconds.ToString(CultureInfo.InvariantCulture) + " seconds…";
                    return false;
                }
                retryGeneration = generation;
                retryEpoch = recoveryEpoch;
                attempt = recoveryAttempts + 1;
                channel = currentChannel;
                streamIndex = currentStreamIndex;
                if (attempt == RecoveryDelays.Length && channel.Streams.Count > 1)
                    streamIndex = (streamIndex + 1) % channel.Streams.Count;
                recoveryInFlight = true;
            }

            bool dispatched = false;
            await playGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!RecoveryStillCurrent(retryGeneration, retryEpoch)) return false;
                // The source can recover on its own between polls. Confirm that
                // it is still stopped before clearing an otherwise healthy input.
                if (IsRunning)
                {
                    string actual = null;
                    try { actual = ReadState(await QueryAsync(null, 2500).ConfigureAwait(false)); }
                    catch (WebException) { }
                    catch (TimeoutException) { }
                    if (!RecoveryStillCurrent(retryGeneration, retryEpoch)) return false;
                    if (actual == "playing" || actual == "paused")
                    {
                        lock (stateLock)
                        {
                            observedState = actual;
                            manuallyPaused = actual == "paused";
                            healthySince = actual == "playing" ? clock() : -1;
                            CancelPendingRecoveryLocked();
                            playbackNotice = "";
                        }
                        return false;
                    }
                }
                lock (stateLock)
                {
                    if (!RecoveryStillCurrentLocked(retryGeneration, retryEpoch)) return false;
                    recoveryAttempts = attempt;
                    nextRecoveryAt = -1;
                    currentStreamIndex = streamIndex;
                    playbackNotice = "Reconnecting… (attempt " + attempt.ToString(CultureInfo.InvariantCulture) + " of 3)";
                    dispatched = true;
                }
                LogPlayback("reconnect", channel, streamIndex, "stopped", attempt);
                await EnsureStartedAsync().ConfigureAwait(false);
                if (!RecoveryStillCurrent(retryGeneration, retryEpoch)) return false;
                await TuneAsync(channel, channel.Streams[streamIndex], delegate { return RecoveryStillCurrent(retryGeneration, retryEpoch); }).ConfigureAwait(false);
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception)
            {
                if (RecoveryStillCurrent(retryGeneration, retryEpoch))
                {
                    lock (stateLock)
                    {
                        if (recoveryAttempts < RecoveryDelays.Length)
                        {
                            nextRecoveryAt = clock() + RecoveryDelays[recoveryAttempts] * 1000L;
                            playbackNotice = "Reconnect did not finish. Trying again in " + RecoveryDelays[recoveryAttempts].ToString(CultureInfo.InvariantCulture) + " seconds…";
                        }
                        else playbackNotice = "Stream stopped. Automatic reconnect failed after 3 attempts. Try another source or channel.";
                    }
                    LogPlayback("reconnect-failed", channel, streamIndex, "stopped", attempt);
                }
                return dispatched;
            }
            finally
            {
                lock (stateLock) { recoveryInFlight = false; }
                playGate.Release();
            }
        }

        private bool RecoveryStillCurrent(int retryGeneration, int retryEpoch)
        {
            lock (stateLock) { return RecoveryStillCurrentLocked(retryGeneration, retryEpoch); }
        }

        private bool RecoveryStillCurrentLocked(int retryGeneration, int retryEpoch)
        {
            return disposed == 0 && generation == retryGeneration && recoveryEpoch == retryEpoch
                && recoveryEnabled && wantsPlayback && !manuallyPaused && healthyPlaybackSeen;
        }

        private void LogPlayback(string action, Channel channel, int streamIndex, string state, int attempt)
        {
            try
            {
                string id = channel == null ? "none" : CleanLine(channel.Id);
                if (id.IndexOf("://", StringComparison.Ordinal) >= 0) id = "custom-channel";
                if (id.Length > 160) id = id.Substring(0, 160);
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelSurf");
                string path = Path.Combine(directory, "playback.log");
                lock (diagnosticLock)
                {
                    Directory.CreateDirectory(directory);
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, "");
                    File.AppendAllText(path, DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture) + "Z action=" + action
                        + " channel=" + id + " source=" + streamIndex.ToString(CultureInfo.InvariantCulture)
                        + " state=" + state + " retry=" + attempt.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void ShowWindow()
        {
            Process process;
            lock (stateLock) { process = ownedProcess; }
            if (!ProcessIsRunning(process)) return;
            try
            {
                process.Refresh();
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero) window = FindOwnedWindow(process.Id);
                if (window == IntPtr.Zero) return;
                NativeShowWindow(window, 9);
                SetForegroundWindow(window);
            }
            catch (InvalidOperationException) { }
        }

        private async Task EnsureStartedAsync()
        {
            ThrowIfDisposed();
            if (!IsRunning)
            {
                if (String.IsNullOrEmpty(ExecutablePath) || !File.Exists(ExecutablePath))
                    throw new FileNotFoundException("VLC was not found. Install VLC from videolan.org, then reopen Channel Surf.");
                TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = ExecutablePath;
                start.Arguments = "--ignore-config --no-one-instance --no-one-instance-when-started-from-file "
                    + "--intf=dummy --extraintf=http --http-host=127.0.0.1 --http-port=" + port.ToString(CultureInfo.InvariantCulture)
                    + " --http-password=" + password + " --no-interact --no-media-library --no-metadata-network-access --no-auto-preparse --network-caching=1500 --http-reconnect";
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WorkingDirectory = Path.GetDirectoryName(ExecutablePath);
                lock (stateLock)
                {
                    ThrowIfDisposed();
                    if (ownedProcess != null) ownedProcess.Dispose();
                    ownedProcess = Process.Start(start);
                    ready = false;
                }
            }
            if (ready) return;
            Stopwatch deadline = Stopwatch.StartNew();
            Exception lastError = null;
            while (deadline.ElapsedMilliseconds < 12000)
            {
                ThrowIfDisposed();
                if (!IsRunning) throw new InvalidOperationException("VLC closed before its channel controls were ready.", lastError);
                try
                {
                    await QueryAsync(null, 1200).ConfigureAwait(false);
                    ready = true;
                    return;
                }
                catch (WebException ex) { lastError = ex; }
                catch (TimeoutException ex) { lastError = ex; }
                await Task.Delay(140).ConfigureAwait(false);
            }
            throw new InvalidOperationException("VLC did not become ready. Close its Channel Surf window and try a channel again.", lastError);
        }

        private async Task<string> QueryAsync(string query, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:"
                + port.ToString(CultureInfo.InvariantCulture) + "/requests/status.xml" + (String.IsNullOrEmpty(query) ? "" : "?" + query));
            request.Proxy = null;
            request.KeepAlive = false;
            request.AllowAutoRedirect = false;
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + password));
            Task<WebResponse> responseTask = request.GetResponseAsync();
            Task winner = await Task.WhenAny(responseTask, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);
            if (winner != responseTask)
            {
                request.Abort();
                try { await responseTask.ConfigureAwait(false); } catch (WebException) { }
                throw new TimeoutException("VLC took too long to respond.");
            }
            using (WebResponse response = await responseTask.ConfigureAwait(false))
            using (Stream body = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(body, Encoding.UTF8))
                return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static string BuildPlaylist(Channel channel, ChannelStream stream)
        {
            StringBuilder text = new StringBuilder("#EXTM3U\n#EXTINF:-1,");
            text.Append(CleanLine(channel.Name));
            text.Append('\n');
            if (!String.IsNullOrWhiteSpace(stream.UserAgent))
                text.Append("#EXTVLCOPT:http-user-agent=").Append(CleanLine(stream.UserAgent)).Append('\n');
            if (!String.IsNullOrWhiteSpace(stream.Referrer))
                text.Append("#EXTVLCOPT:http-referrer=").Append(CleanLine(stream.Referrer)).Append('\n');
            text.Append(stream.Url.Trim()).Append('\n');
            return text.ToString();
        }

        private static void ValidateStreamUrl(string url)
        {
            Uri parsed;
            if (String.IsNullOrWhiteSpace(url) || url.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed))
                throw new ArgumentException("This stream has an invalid address.");
            string scheme = parsed.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "rtsp" && scheme != "rtsps"
                && scheme != "rtmp" && scheme != "rtmps" && scheme != "udp" && scheme != "rtp"
                && scheme != "mms" && scheme != "mmsh")
                throw new ArgumentException("This stream uses a protocol that Channel Surf does not support.");
        }

        private static string CleanLine(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            StringBuilder clean = new StringBuilder(text.Length);
            foreach (char c in text) clean.Append(Char.IsControl(c) ? ' ' : c);
            return clean.ToString().Trim();
        }

        private static string FindVlc()
        {
            string[] roots = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
            };
            foreach (string root in roots)
            {
                if (String.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, "VideoLAN", "VLC", "vlc.exe");
                if (File.Exists(path)) return path;
            }
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VLC", "vlc.exe");
            return File.Exists(portable) ? portable : "";
        }

        private static bool ProcessIsRunning(Process process)
        {
            if (process == null) return false;
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("VlcController");
        }

        private static void DeleteFile(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Interlocked.Increment(ref generation);
            Process process;
            lock (stateLock) { process = ownedProcess; ownedProcess = null; ready = false; }
            if (process == null)
            {
                CleanSessionDirectory();
                return;
            }
            // A foreground cleanup thread survives WPF's last window closing. Its
            // bounded retry also covers closing the app while VLC is still starting.
            Thread cleanup = new Thread(delegate()
            {
                try
                {
                    Stopwatch deadline = Stopwatch.StartNew();
                    while (ProcessIsRunning(process) && deadline.ElapsedMilliseconds < 2000)
                    {
                        RequestOwnedQuit();
                        if (!ProcessIsRunning(process)) break;
                        try
                        {
                            if (!process.CloseMainWindow())
                            {
                                IntPtr window = FindOwnedWindow(process.Id);
                                if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        catch (InvalidOperationException) { }
                        Thread.Sleep(175);
                    }
                }
                catch (System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); CleanSessionDirectory(); }
            });
            cleanup.Name = "Channel Surf VLC cleanup";
            cleanup.IsBackground = false;
            cleanup.Start();
        }

        private void CleanSessionDirectory()
        {
            DeleteFile(lastPlaylist);
            try
            {
                if (!Directory.Exists(sessionDirectory)) return;
                foreach (string file in Directory.GetFiles(sessionDirectory, "*.m3u8")) DeleteFile(file);
                Directory.Delete(sessionDirectory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void RequestOwnedQuit()
        {
            // VLC's native video window can close without exiting a dummy interface.
            // Send its documented vlc://quit input through this session's private API.
            // This bounded local write does not wait for a shutdown response.
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult connection = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    using (WaitHandle completed = connection.AsyncWaitHandle)
                    {
                        if (!completed.WaitOne(150)) return;
                        client.EndConnect(connection);
                    }
                    client.SendTimeout = 150;
                    byte[] request = Encoding.ASCII.GetBytes("GET /requests/status.xml?command=in_play&input=vlc%3A%2F%2Fquit HTTP/1.1\r\n"
                        + "Host: 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "\r\n"
                        + "Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + password))
                        + "\r\nConnection: close\r\n\r\n");
                    client.GetStream().Write(request, 0, request.Length);
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static IntPtr FindOwnedWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                uint candidateId;
                GetWindowThreadProcessId(window, out candidateId);
                if (candidateId != processId || !IsWindowVisible(window)) return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool NativeShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
