using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ChannelSurf
{
    public sealed class PlaylistService
    {
        public const string PlaylistUrl = "https://iptv-org.github.io/iptv/index.m3u";
        private const string ApiRoot = "https://iptv-org.github.io/api/";
        private static readonly SemaphoreSlim LoadGate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Client = CreateClient();
        private static readonly Regex Attributes = new Regex(@"([\w-]+)\s*=\s*(?:""((?:\\.|[^""\\])*)""|'([^']*)'|([^\s,]+))", RegexOptions.Compiled);
        private static readonly HashSet<string> Protocols = new HashSet<string>(new[] { "http", "https", "rtsp", "rtmp", "udp", "rtp" }, StringComparer.OrdinalIgnoreCase);
        public string DataDirectory { get; private set; }

        public PlaylistService(string directory = null)
        {
            DataDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelSurf");
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8);
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 64L * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelSurf/1.0");
            return client;
        }

        public Task<CatalogResult> LoadAsync(bool refresh = false)
        {
            // A fully cached load can complete every await synchronously. Keep file
            // reads, playlist parsing, and metadata deserialization off the UI thread.
            return Task.Run(() => LoadCoreAsync(refresh));
        }

        private async Task<CatalogResult> LoadCoreAsync(bool refresh)
        {
            await LoadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    // Metadata is optional. All downloads share a deadline so five failed
                    // metadata endpoints cannot turn into five consecutive timeouts.
                    var playlistTask = GetDocumentAsync("playlist.m3u", PlaylistUrl, TimeSpan.FromHours(6), refresh, deadline.Token);
                    var channelTask = GetDocumentAsync("channels.json", ApiRoot + "channels.json", TimeSpan.FromDays(1), refresh, deadline.Token);
                    var feedTask = GetDocumentAsync("feeds.json", ApiRoot + "feeds.json", TimeSpan.FromDays(1), refresh, deadline.Token);
                    var countryTask = GetDocumentAsync("countries.json", ApiRoot + "countries.json", TimeSpan.FromDays(7), refresh, deadline.Token);
                    var languageTask = GetDocumentAsync("languages.json", ApiRoot + "languages.json", TimeSpan.FromDays(7), refresh, deadline.Token);
                    var categoryTask = GetDocumentAsync("categories.json", ApiRoot + "categories.json", TimeSpan.FromDays(7), refresh, deadline.Token);
                    await Task.WhenAll(playlistTask, channelTask, feedTask, countryTask, languageTask, categoryTask).ConfigureAwait(false);
                    var playlist = playlistTask.Result;
                    if (String.IsNullOrWhiteSpace(playlist.Text))
                        throw new InvalidOperationException("The channel list could not be downloaded and no saved copy is available. Check your connection, then refresh.");
                    var channels = ParsePlaylist(playlist.Text);
                    if (channels.Count == 0)
                        throw new InvalidOperationException("The downloaded playlist did not contain any supported channels. Please refresh to try again.");
                    var warnings = new List<string>();
                    if (!String.IsNullOrWhiteSpace(playlist.Error))
                        warnings.Add("Using the saved channel list because the online list could not be refreshed.");
                    bool enriched = ApplyMetadata(channels, channelTask.Result.Text, feedTask.Result.Text, countryTask.Result.Text, languageTask.Result.Text, categoryTask.Result.Text);
                    if (!enriched) warnings.Add("Some country or language information is unavailable. Search and playback still work.");
                    return new CatalogResult
                    {
                        Channels = channels.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                        FromCache = playlist.FromCache,
                        Warning = String.Join(" ", warnings.ToArray()),
                        UpdatedAtUtc = playlist.UpdatedAtUtc
                    };
                }
            }
            finally { LoadGate.Release(); }
        }

        private async Task<DocumentResult> GetDocumentAsync(string fileName, string url, TimeSpan freshness, bool refresh, CancellationToken token)
        {
            string path = Path.Combine(DataDirectory, fileName);
            string cached = null;
            DateTime saved = DateTime.MinValue;
            try
            {
                if (File.Exists(path))
                {
                    cached = File.ReadAllText(path, Encoding.UTF8);
                    saved = File.GetLastWriteTimeUtc(path);
                    if (!refresh && saved > DateTime.UtcNow.Subtract(freshness) && IsValidDocument(fileName, cached))
                        return new DocumentResult { Text = cached, FromCache = true, UpdatedAtUtc = saved };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                using (var response = await Client.GetAsync(url, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    string downloaded = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!IsValidDocument(fileName, downloaded)) throw new InvalidDataException("Invalid " + fileName + " response.");
                    try { AtomicFiles.WriteText(path, downloaded); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    return new DocumentResult { Text = downloaded, UpdatedAtUtc = DateTime.UtcNow };
                }
            }
            catch (Exception ex)
            {
                if (!(ex is HttpRequestException) && !(ex is OperationCanceledException) && !(ex is IOException) && !(ex is WebException)) throw;
                return new DocumentResult { Text = IsValidDocument(fileName, cached) ? cached : null, FromCache = true, UpdatedAtUtc = saved, Error = ex.Message };
            }
        }

        private static bool IsValidDocument(string fileName, string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return false;
            string start = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (fileName.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                return start.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) && start.IndexOf("#EXTINF:", StringComparison.OrdinalIgnoreCase) >= 0;
            return start.StartsWith("[", StringComparison.Ordinal) && start.TrimEnd().EndsWith("]", StringComparison.Ordinal);
        }

        public static List<Channel> ParsePlaylist(string text)
        {
            var result = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrEmpty(text)) return new List<Channel>();
            Dictionary<string, string> attrs = null;
            string name = null, userAgent = null, referrer = null;
            using (var reader = new StringReader(text))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Trim().TrimStart('\uFEFF');
                    if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    {
                        attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        int comma = FindNameSeparator(line);
                        string info = comma < 0 ? line : line.Substring(0, comma);
                        name = comma < 0 ? "" : line.Substring(comma + 1).Trim();
                        foreach (Match match in Attributes.Matches(info))
                            attrs[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value.Replace("\\\"", "\"") : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                        userAgent = Get(attrs, "http-user-agent");
                        referrer = Get(attrs, "http-referrer");
                        if (String.IsNullOrWhiteSpace(referrer)) referrer = Get(attrs, "http-referer");
                        continue;
                    }
                    if (attrs == null || line.Length == 0) continue;
                    if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                    {
                        string option = line.Substring(11);
                        int equals = option.IndexOf('=');
                        if (equals >= 0)
                        {
                            string key = option.Substring(0, equals).Trim();
                            string value = SanitizeHeader(option.Substring(equals + 1));
                            if (key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase)) userAgent = value;
                            if (key.Equals("http-referrer", StringComparison.OrdinalIgnoreCase) || key.Equals("http-referer", StringComparison.OrdinalIgnoreCase)) referrer = value;
                        }
                        continue;
                    }
                    if (line[0] == '#') continue;
                    string url = line;
                    int pipe = url.IndexOf('|');
                    if (pipe >= 0)
                    {
                        foreach (string pair in url.Substring(pipe + 1).Split('&'))
                        {
                            int equals = pair.IndexOf('=');
                            if (equals < 0) continue;
                            string key = pair.Substring(0, equals);
                            string value;
                            try { value = SanitizeHeader(Uri.UnescapeDataString(pair.Substring(equals + 1).Replace("+", " "))); }
                            catch (UriFormatException) { continue; }
                            if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) userAgent = value;
                            if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key.Equals("Referrer", StringComparison.OrdinalIgnoreCase)) referrer = value;
                        }
                        url = url.Substring(0, pipe).Trim();
                    }
                    Uri uri;
                    if (Uri.TryCreate(url, UriKind.Absolute, out uri) && Protocols.Contains(uri.Scheme) && !ContainsControl(url))
                    {
                        string sourceId = Get(attrs, "tvg-id");
                        string id = BaseId(sourceId);
                        string displayName = !String.IsNullOrWhiteSpace(name) ? name : Get(attrs, "tvg-name");
                        if (String.IsNullOrWhiteSpace(displayName)) displayName = uri.Host;
                        if (String.IsNullOrWhiteSpace(id)) id = StableNameId(displayName);
                        Channel channel;
                        if (!result.TryGetValue(id, out channel))
                        {
                            channel = new Channel { Id = id, Name = displayName, LogoUrl = SafeLogo(Get(attrs, "tvg-logo")), Group = Get(attrs, "group-title") };
                            result.Add(id, channel);
                        }
                        foreach (string group in Get(attrs, "group-title").Split(';'))
                        {
                            string category = group.Trim();
                            AddUnique(channel.Categories, category);
                            if (category.Equals("XXX", StringComparison.OrdinalIgnoreCase) || category.Equals("Adult", StringComparison.OrdinalIgnoreCase)) channel.IsNsfw = true;
                        }
                        foreach (string language in Get(attrs, "tvg-language").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)) AddUnique(channel.Languages, language.Trim());
                        foreach (string country in Get(attrs, "tvg-country").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)) AddUnique(channel.Countries, country.Trim());
                        userAgent = SanitizeHeader(userAgent);
                        referrer = SanitizeHeader(referrer);
                        if (!channel.Streams.Any(s => s.Url == url && s.UserAgent == userAgent && s.Referrer == referrer))
                            channel.Streams.Add(new ChannelStream { Name = displayName, SourceId = sourceId, Url = url, UserAgent = userAgent, Referrer = referrer });
                    }
                    // Directives belong to exactly one stream, including invalid URLs.
                    attrs = null; name = null; userAgent = null; referrer = null;
                }
            }
            foreach (Channel channel in result.Values) channel.UpdateSearchText();
            return result.Values.ToList();
        }

        private static int FindNameSeparator(string line)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0' && c == '\\' && i + 1 < line.Length && line[i + 1] == quote) { i++; continue; }
                if (quote == '\0' && (c == '"' || c == '\'')) { quote = c; continue; }
                if (c == quote) { quote = '\0'; continue; }
                if (quote == '\0' && c == ',') return i;
            }
            return -1;
        }

        internal static string BaseId(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return "";
            int at = id.IndexOf('@');
            return (at < 0 ? id : id.Substring(0, at)).Trim();
        }

        private static string StableNameId(string name)
        {
            string normalized = Regex.Replace(name.ToLowerInvariant(), @"\s*\((?:\d+[pi]|geo-blocked|not 24/7)\)\s*", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            using (var sha = SHA256.Create())
                return "name:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
        }

        private static string Get(Dictionary<string, string> attrs, string key) { string value; return attrs.TryGetValue(key, out value) ? value : ""; }
        private static string SanitizeHeader(string value) { return value == null ? "" : value.Replace("\r", "").Replace("\n", "").Trim(); }
        private static bool ContainsControl(string value) { return value.Any(c => Char.IsControl(c)); }
        private static string SafeLogo(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? value : "";
        }
        private static void AddUnique(List<string> values, string value)
        {
            if (!String.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
        }

        private static bool ApplyMetadata(List<Channel> channels, string channelJson, string feedJson, string countryJson, string languageJson, string categoryJson)
        {
            List<ApiChannel> apiChannels = ReadJson<List<ApiChannel>>(channelJson) ?? new List<ApiChannel>();
            List<ApiFeed> apiFeeds = ReadJson<List<ApiFeed>>(feedJson) ?? new List<ApiFeed>();
            var countries = (ReadJson<List<ApiCountry>>(countryJson) ?? new List<ApiCountry>()).Where(c => c.Code != null).GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var languages = (ReadJson<List<ApiLanguage>>(languageJson) ?? new List<ApiLanguage>()).Where(c => c.Code != null).GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var categories = (ReadJson<List<ApiCategory>>(categoryJson) ?? new List<ApiCategory>()).Where(c => c.Id != null).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var channelMap = apiChannels.Where(c => !String.IsNullOrEmpty(c.Id)).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var feedMap = apiFeeds.Where(f => !String.IsNullOrEmpty(f.Channel)).GroupBy(f => f.Channel, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (Channel channel in channels)
            {
                ApiChannel metadata;
                if (channelMap.TryGetValue(channel.Id, out metadata))
                {
                    if (!String.IsNullOrWhiteSpace(metadata.Name)) channel.Name = metadata.Name;
                    channel.IsNsfw = channel.IsNsfw || metadata.IsNsfw;
                    string countryName;
                    if (!String.IsNullOrWhiteSpace(metadata.Country)) AddUnique(channel.Countries, countries.TryGetValue(metadata.Country, out countryName) ? countryName : metadata.Country);
                    if (metadata.Languages != null)
                        foreach (string code in metadata.Languages) { string label; AddUnique(channel.Languages, languages.TryGetValue(code, out label) ? label : code); }
                    if (metadata.Categories != null)
                        foreach (string code in metadata.Categories)
                        {
                            string label;
                            AddUnique(channel.Categories, categories.TryGetValue(code, out label) ? label : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(code));
                            if (code.Equals("xxx", StringComparison.OrdinalIgnoreCase)) channel.IsNsfw = true;
                        }
                }
                List<ApiFeed> feeds;
                if (feedMap.TryGetValue(channel.Id, out feeds))
                {
                    var sourceIds = new HashSet<string>(channel.Streams.Select(s => s.SourceId ?? ""), StringComparer.OrdinalIgnoreCase);
                    List<ApiFeed> matching = feeds.Where(f => sourceIds.Contains(f.Channel + "@" + f.Id)).ToList();
                    if (matching.Count == 0) matching = feeds.Where(f => f.IsMain).ToList();
                    foreach (ApiFeed feed in matching)
                        foreach (string code in feed.Languages ?? new List<string>())
                        {
                            string label;
                            AddUnique(channel.Languages, languages.TryGetValue(code, out label) ? label : code);
                        }
                }
                channel.Countries.Sort(StringComparer.CurrentCultureIgnoreCase);
                channel.Languages.Sort(StringComparer.CurrentCultureIgnoreCase);
                channel.Categories.Sort(StringComparer.CurrentCultureIgnoreCase);
                channel.UpdateSearchText();
            }
            return apiChannels.Count > 0 && apiFeeds.Count > 0 && countries.Count > 0 && languages.Count > 0;
        }

        internal static T ReadJson<T>(string json) where T : class
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var input = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = Int32.MaxValue }).ReadObject(input);
            }
            catch (SerializationException) { return null; }
            catch (InvalidDataContractException) { return null; }
        }

        private sealed class DocumentResult { public string Text; public bool FromCache; public string Error; public DateTime UpdatedAtUtc; }
        [DataContract] private sealed class ApiChannel
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "country")] public string Country { get; set; }
            [DataMember(Name = "languages")] public List<string> Languages { get; set; }
            [DataMember(Name = "categories")] public List<string> Categories { get; set; }
            [DataMember(Name = "is_nsfw")] public bool IsNsfw { get; set; }
        }
        [DataContract] private sealed class ApiFeed
        {
            [DataMember(Name = "channel")] public string Channel { get; set; }
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "is_main")] public bool IsMain { get; set; }
            [DataMember(Name = "languages")] public List<string> Languages { get; set; }
        }
        [DataContract] private sealed class ApiCountry { [DataMember(Name = "code")] public string Code { get; set; } [DataMember(Name = "name")] public string Name { get; set; } }
        [DataContract] private sealed class ApiLanguage { [DataMember(Name = "code")] public string Code { get; set; } [DataMember(Name = "name")] public string Name { get; set; } }
        [DataContract] private sealed class ApiCategory { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "name")] public string Name { get; set; } }
    }

    public sealed class UserStateStore
    {
        private static readonly object StateGate = new object();
        public string DataDirectory { get; private set; }
        public UserStateStore(string directory = null)
        {
            DataDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelSurf");
        }
        public UserState Load()
        {
            lock (StateGate)
            {
                try
                {
                    string path = Path.Combine(DataDirectory, "state.json");
                    var state = File.Exists(path) ? PlaylistService.ReadJson<UserState>(File.ReadAllText(path, Encoding.UTF8)) : null;
                    if (state == null) return new UserState();
                    state.FavoriteIds = NormalizeIds(state.FavoriteIds, Int32.MaxValue);
                    state.RecentIds = NormalizeIds(state.RecentIds, 100);
                    return state;
                }
                catch (IOException) { return new UserState(); }
                catch (UnauthorizedAccessException) { return new UserState(); }
            }
        }
        public void Save(UserState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            lock (StateGate)
            {
                var snapshot = new UserState
                {
                    FavoriteIds = NormalizeIds(state.FavoriteIds, Int32.MaxValue),
                    RecentIds = NormalizeIds(state.RecentIds, 100),
                    LastCountry = state.LastCountry, LastLanguage = state.LastLanguage, LastCategory = state.LastCategory,
                    ShowAdultChannels = state.ShowAdultChannels
                };
                using (var output = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(UserState)).WriteObject(output, snapshot);
                    AtomicFiles.WriteText(Path.Combine(DataDirectory, "state.json"), Encoding.UTF8.GetString(output.ToArray()));
                }
            }
        }
        private static List<string> NormalizeIds(IEnumerable<string> ids, int limit)
        {
            return (ids ?? Enumerable.Empty<string>()).Where(s => !String.IsNullOrWhiteSpace(s)).Select(PlaylistService.BaseId).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
        }
    }

    internal static class AtomicFiles
    {
        internal static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
