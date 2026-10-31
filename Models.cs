using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace ChannelSurf
{
    public sealed class ChannelStream
    {
        public string Name { get; set; }
        public string SourceId { get; set; }
        public string Url { get; set; }
        public string UserAgent { get; set; }
        public string Referrer { get; set; }
        public string DisplayName
        {
            get
            {
                var parts = new List<string> { String.IsNullOrWhiteSpace(Name) ? "Stream" : Name.Trim() };
                int at = String.IsNullOrEmpty(SourceId) ? -1 : SourceId.IndexOf('@');
                if (at >= 0 && at + 1 < SourceId.Length)
                {
                    string feed = SourceId.Substring(at + 1).Trim();
                    if (feed.Length > 0) parts.Add(feed);
                }
                Uri uri;
                if (Uri.TryCreate(Url, UriKind.Absolute, out uri) && !String.IsNullOrEmpty(uri.Host)) parts.Add(uri.Host);
                return String.Join(" · ", parts.ToArray());
            }
        }
        public override string ToString() { return String.IsNullOrWhiteSpace(Name) ? Url : Name; }
    }

    public sealed class Channel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string LogoUrl { get; set; }
        public string Group { get; set; }
        public bool IsNsfw { get; set; }
        public List<string> Countries { get; set; }
        public List<string> Languages { get; set; }
        public List<string> Categories { get; set; }
        public List<ChannelStream> Streams { get; set; }
        public string SearchText { get; set; }
        public string CountryText { get { return String.Join(", ", Countries.ToArray()); } }
        public string LanguageText { get { return String.Join(", ", Languages.ToArray()); } }
        public string CategoryText { get { return String.Join(" / ", Categories.ToArray()); } }
        public int StreamCount { get { return Streams.Count; } }

        public Channel()
        {
            Countries = new List<string>();
            Languages = new List<string>();
            Categories = new List<string>();
            Streams = new List<ChannelStream>();
        }

        internal void UpdateSearchText()
        {
            SearchText = String.Join(" ", new[] { Name, Id, Group, CountryText, LanguageText, CategoryText }
                .Where(s => !String.IsNullOrWhiteSpace(s)).ToArray()).ToLowerInvariant();
        }

        public override string ToString() { return Name; }
    }

    public sealed class CatalogResult
    {
        public List<Channel> Channels { get; set; }
        public bool FromCache { get; set; }
        public string Warning { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    [DataContract]
    public sealed class UserState
    {
        [DataMember] public List<string> FavoriteIds { get; set; }
        [DataMember] public List<string> RecentIds { get; set; }
        [DataMember] public string LastCountry { get; set; }
        [DataMember] public string LastLanguage { get; set; }
        [DataMember] public string LastCategory { get; set; }
        [DataMember] public bool ShowAdultChannels { get; set; }
        public UserState()
        {
            FavoriteIds = new List<string>();
            RecentIds = new List<string>();
        }
    }
}
