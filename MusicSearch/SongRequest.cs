﻿using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using VideoLibrary;

namespace MusicSearch
{
    public class SongRequest
    {
        private static YouTubeService YTHolder = null;
        internal static YouTubeService YT
        {
            get
            {
                if (YTHolder == null)
                {
                    YTHolder = new YouTubeService(new BaseClientService.Initializer
                    {
                        ApiKey = YouTube
                    });
                }

                return YTHolder;
            }
        }
        public static string YouTube;
        public static string SoundCloud = null;
        private static readonly Regex YoutubeRegex = new Regex(@"youtu(?:\.be|be\.com)/(?:(.*)v(/|=)|(.*/)?)([a-zA-Z0-9-_]+)", RegexOptions.IgnoreCase);

        public static Func<string, Task<string>> GetTelegramUrl;

        public static async Task<string> StreamUrl(Song Song, bool AllFormats = true)
        {
            if (Song.Type == SongType.YouTube)
            {
                var Videos = VideoLibrary.YouTube.Default.GetAllVideos(Song.Url);
                YouTubeVideo MaxVid = null;
                foreach (var Vid in Videos)
                {
                    if (MaxVid == null || (Vid.AudioBitrate >= MaxVid.AudioBitrate && (AllFormats || Vid.AudioFormat == AudioFormat.Aac)))
                    {
                        MaxVid = Vid;
                    }
                }

                if (MaxVid == null)
                {
                    return string.Empty;
                }

                return await MaxVid.GetUriAsync();
            }
            else if (Song.Type == SongType.SoundCloud)
            {
                var SC = await($"http://api.soundcloud.com/resolve?url={Song.Url}&client_id={SoundCloud}").WebResponse();
                if (SC != string.Empty && SC.StartsWith("{\"kind\":\"track\""))
                {
                    return $"{JObject.Parse(SC)["stream_url"]}?client_id={SoundCloud}";
                }
            }
            else if (Song.Type == SongType.Telegram)
            {
                return await GetTelegramUrl(Song.Url);
            }

            return Song.Url;
        }

        public static async Task<List<Song>> Search(object ToSearch, bool ReturnAtOnce = false)
        {
            var Query = ((string)ToSearch).Trim();
            var Results = new List<Song>();
            
            var Match = YoutubeRegex.Match(Query);
            if (Match.Success)
            {
                var ResultData = await YouTubeParse(Match.Groups[4].Value);
                if (ResultData != null)
                {
                    Results.Add((Song)ResultData);
                }
            }
            else if (SoundCloud != null && Regex.IsMatch(Query, "(.*)(soundcloud.com|snd.sc)(.*)"))
            {
                var SC = await ($"http://api.soundcloud.com/resolve?url={Query}&client_id={SoundCloud}").WebResponse();
                if (SC != string.Empty && SC.StartsWith("{\"kind\":\"track\""))
                {
                    Results.Add(SoundCloudParse(JToken.Parse(SC)));
                }
            }
            else if (Uri.TryCreate(Query, UriKind.Absolute, out Uri Url))
            {
                Results.Add(new Song
                {
                    FullName = Path.GetFileNameWithoutExtension(Url.LocalPath),
                    Desc = $"Remote {Path.GetExtension(Url.LocalPath)} file",
                    Url = Query,
                    Type = SongType.File
                });
            }

            if (Query.Length >= 3)
            {
                var SplitQuery = Query.Split(' ');
                var Range = SongRequestLocal.GetFiles()
                    .Where(
                        x => x.Name.Length >= Query.Length && SplitQuery.All(y => x.Name.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0)
                    )
                    .Select(x => new Song
                    {
                        FullName = x.Name,
                        Desc = $"{x.Extension} file at {x.Dir}",
                        Url = x.Path,
                        Type = SongType.File
                    });

                if (Range.Count() > 3)
                {
                    Range = Range.Take(3);
                }

                Results.AddRange(Range);
            }

            if (!ReturnAtOnce || Results.Count == 0)
            {
                var SC = $"http://api.soundcloud.com/tracks/?client_id={SoundCloud}&q={System.Text.Encodings.Web.UrlEncoder.Default.Encode(Query)}".WebResponse();

                var ListRequest = YT.Search.List("snippet");
                ListRequest.Q = Query;
                ListRequest.MaxResults = 3;
                ListRequest.Type = "video";
                foreach (var Result in (await ListRequest.ExecuteAsync()).Items)
                {
                    var ResultData = await YouTubeParse(Result.Id.VideoId);
                    if (ResultData != null)
                    {
                        Results.Add((Song)ResultData);
                    }
                }

                var SCResponse = JArray.Parse(await SC);

                int i = 0;
                foreach (var Response in SCResponse)
                {
                    if (++i > 3)
                    {
                        break;
                    }

                    Results.Add(SoundCloudParse(Response));
                }
            }

            return Results;
        }

        private static async Task<Song?> YouTubeParse(string VideoId)
        {
            var Search = YT.Videos.List("contentDetails,snippet");
            Search.Id = VideoId;

            var Videos = await Search.ExecuteAsync();
            var Result = Videos.Items.FirstOrDefault();

            if (Result != null)
            {
                var Desc = Result.Snippet.Description;
                if (Desc.Length == 0)
                {
                    Desc = "No description";
                }

                return new Song
                {
                    FullName = Result.Snippet.Title,
                    Desc = $"{TimeSpanToString(XmlConvert.ToTimeSpan(Result.ContentDetails.Duration))} on YouTube | {Desc}",
                    Url = $"http://www.youtube.com/watch?v={Search.Id}",
                    Type = SongType.YouTube,
                    ThumbNail = Result.Snippet.Thumbnails.Maxres?.Url ?? Result.Snippet.Thumbnails.Default__?.Url
                };
            }

            return null;
        }

        private static string StripHtml(string In)
        {
            return Regex.Replace(In, @"<[^>]*>", string.Empty);
        }

        private static Song SoundCloudParse(JToken Response)
        {
            var Desc = Response["description"].ToString().Replace("\n", " ");
            if (Desc.Length == 0)
            {
                Desc = Response["genre"].UcWords().ToString();
            }

            var Thumb = Response["artwork_url"].ToString();
            if (Thumb == string.Empty)
            {
                Thumb = "http://i.imgur.com/eRaxycY.png";
            }

            return new Song
            {
                FullName = Response["title"].ToString(),
                Desc = $"{TimeSpanToString(new TimeSpan(0, 0, 0, 0, Response["duration"].ToObject<int>()))} on SoundCloud | {StripHtml(Desc.Trim())}",
                Url = Response["uri"].ToString(),
                Type = SongType.SoundCloud,
                ThumbNail = Thumb
            };
        }

        private static string TimeSpanToString(TimeSpan Span)
        {
            var TimeStr = $"{Span.Minutes.ToString().PadLeft(2, '0')}:{Span.Seconds.ToString().PadLeft(2, '0')}";
            var Hours = Span.Days * 24 + Span.Hours;
            if (Hours != 0)
            {
                TimeStr = $"{Hours}:{TimeStr}";
            }

            return TimeStr;
        }
    }
}
