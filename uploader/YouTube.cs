using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Newtonsoft.Json;

namespace PbnUploader;

/// <summary>
/// Uploads a packaged episode to YouTube through the Data API: the reaction video (title, description and tags from
/// upload.txt; thumbnail; captions; scheduled publish time), then its preview Short, scheduled a few hours earlier
/// and linking to the full video.
/// Each finished step is recorded in youtube.json in the episode's folder, so a retry after a failure carries on where
/// it stopped and never uploads a video twice. published.json is written once everything is done.
/// Setup (the OAuth client and Google's audit) is in docs/youtube-upload-setup.md.
/// </summary>
public static class YouTube
{
    // ---- Times: the channel schedules in US Eastern time ---------------------------------------------------------

    public static readonly TimeZoneInfo Eastern = FindEastern();

    static TimeZoneInfo FindEastern()
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.Local;
    }

    public static DateTime ToEastern(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Eastern).DateTime;

    /// <summary>A wall-clock time in Eastern time as an absolute moment.</summary>
    public static DateTimeOffset FromEastern(DateTime wall)
    {
        var w = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        return new DateTimeOffset(w, Eastern.GetUtcOffset(w));
    }

    /// <summary>The daily premiere slots (Eastern): two hours apart, so premieres don't compete for the same viewers.</summary>
    public static readonly int[] SlotHours = [12, 14, 16, 18, 20];

    /// <summary>How long before its episode a preview Short goes out.</summary>
    public static readonly TimeSpan ShortLead = TimeSpan.FromHours(3);

    /// <summary>
    /// The first free slot that leaves time for the Short to go out before it (at least the Short lead plus an hour
    /// from now). <paramref name="taken"/> are slots already booked, in Eastern wall-clock time.
    /// </summary>
    public static DateTime NextSlot(DateTime nowEastern, IEnumerable<DateTime> taken)
    {
        var booked = taken.Select(t => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0)).ToHashSet();
        var earliest = nowEastern + ShortLead + TimeSpan.FromHours(1);
        for (var day = nowEastern.Date; ; day = day.AddDays(1))
            foreach (var h in SlotHours)
            {
                var slot = day.AddHours(h);
                if (slot >= earliest && !booked.Contains(slot)) return slot;
            }
    }

    /// <summary>An episode's planned slot (plan.json in its folder), the dashboard's default when approving it.</summary>
    public sealed record PlannedSlot(DateTime At, bool Premiere);

    public static PlannedSlot? LoadPlan(string folder)
    {
        var file = Path.Combine(folder, "plan.json");
        if (!File.Exists(file)) return null;
        try
        {
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file), new Newtonsoft.Json.Linq.JsonLoadSettings());
            return DateTime.TryParse((string?)j["at"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                ? new PlannedSlot(at, (bool?)j["premiere"] ?? false) : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- upload.txt ----------------------------------------------------------------------------------------------

    public sealed record UploadText(IReadOnlyList<string> Titles, string Description, IReadOnlyList<string> Tags);

    /// <summary>Reads the title options, description and tags out of an upload.txt written by Package.</summary>
    public static UploadText ParseUploadText(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int Header(string h) => Array.FindIndex(lines, l => l.TrimEnd() == h || l.StartsWith(h + " ", StringComparison.Ordinal));
        string[] Section(int start)
        {
            if (start < 0) return [];
            var end = Array.FindIndex(lines, start + 1, l => l.Length > 0 && l == l.ToUpperInvariant() && Regex.IsMatch(l, "^[A-Z][A-Z ()]+$"));
            return lines[(start + 1)..(end < 0 ? lines.Length : end)];
        }
        var titles = Section(Header("TITLE")).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var description = string.Join("\n", Section(Header("DESCRIPTION"))).Trim();
        var tags = string.Join(",", Section(Header("TAGS"))).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        return new UploadText(titles, description, tags);
    }

    /// <summary>A title YouTube accepts: no angle brackets, at most 100 characters.</summary>
    public static string FitTitle(string title)
    {
        var t = Regex.Replace(title.Replace('<', '(').Replace('>', ')'), @"\s+", " ").Trim();
        return t.Length <= 100 ? t : t[..100].TrimEnd();
    }

    /// <summary>A description YouTube accepts: no angle brackets, at most 5,000 bytes.</summary>
    public static string FitDescription(string description)
    {
        var d = description.Replace('<', '(').Replace('>', ')');
        while (Encoding.UTF8.GetByteCount(d) > 5000) d = d[..^1];
        return d;
    }

    /// <summary>Tags in order until YouTube's 500-character total (a tag with a space counts its quotes too).</summary>
    public static List<string> FitTags(IEnumerable<string> tags)
    {
        var kept = new List<string>();
        var total = 0;
        foreach (var raw in tags)
        {
            var t = raw.Replace('<', ' ').Replace('>', ' ').Trim();
            if (t.Length == 0 || kept.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            var cost = t.Length + (t.Contains(' ') ? 2 : 0) + (kept.Count > 0 ? 1 : 0);
            if (total + cost > 500) break;
            kept.Add(t);
            total += cost;
        }
        return kept;
    }

    /// <summary>The preview Short's headline (short.json, written when the Short is built), as a Shorts title.</summary>
    public static string ShortTitle(string folder, string fallback)
    {
        var file = Path.Combine(folder, "short.json");
        if (File.Exists(file))
            try
            {
                var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                var head = $"{(string?)j["top1"]} {(string?)j["top2"]}".Trim();
                if (head.Length > 0) return FitTitle($"{head} #shorts #passportbros");
            }
            catch (JsonException) { }
        return FitTitle($"{fallback} #shorts");
    }

    // ---- State ---------------------------------------------------------------------------------------------------

    /// <summary>What has been done for one episode (youtube.json). Kept after publishing, for the record.</summary>
    public sealed class State
    {
        public string? Title { get; set; }
        public DateTimeOffset? PublishAt { get; set; }
        public bool Premiere { get; set; }
        public string? VideoId { get; set; }
        public DateTimeOffset? ShortAt { get; set; }
        public string? ShortId { get; set; }
        public List<string> Done { get; set; } = [];
    }

    public static State? LoadState(string folder) =>
        File.Exists(Path.Combine(folder, "youtube.json")) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(Path.Combine(folder, "youtube.json"))) : null;

    static void Save(string folder, State s) =>
        File.WriteAllText(Path.Combine(folder, "youtube.json"), JsonConvert.SerializeObject(s, Formatting.Indented));

    // ---- Publishing ----------------------------------------------------------------------------------------------

    /// <param name="Title">The video's title.</param>
    /// <param name="At">When it goes public (Eastern time is applied by the caller).</param>
    /// <param name="Premiere">Upload it private with no publish time: the API can't create a Premiere, so it's set in Studio.</param>
    /// <param name="WithShort">Also upload short.mp4, to go public <see cref="ShortLead"/> earlier.</param>
    public sealed record Plan(string Title, DateTimeOffset At, bool Premiere, bool WithShort);

    public static string Watch(string id) => $"https://youtu.be/{id}";
    public static string Studio(string id) => $"https://studio.youtube.com/video/{id}/edit";

    /// <summary>Signs in (a browser window the first time; the token is kept under the user's AppData) and returns the API client.</summary>
    public static async Task<YouTubeService> ConnectAsync(CancellationToken ct = default)
    {
        var secrets = Settings.YouTubeClientSecrets;
        if (!File.Exists(secrets))
            throw new InvalidOperationException($"No YouTube OAuth client at {secrets}. Follow docs/youtube-upload-setup.md, then save the downloaded client JSON there.");
        ClientSecrets client;
        await using (var s = File.OpenRead(secrets)) client = (await GoogleClientSecrets.FromStreamAsync(s, ct)).Secrets;
        var store = new FileDataStore(Settings.YouTubeTokenFolder, fullPath: true);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(client, [YouTubeService.Scope.YoutubeForceSsl], "channel", ct, store);
        // While the OAuth app is in Testing, Google expires the sign-in after 7 days. A stale token fails to refresh:
        // forget it and sign in again in the browser, instead of failing the upload.
        bool fresh;
        try { fresh = await credential.RefreshTokenAsync(ct); }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException) { fresh = false; } // invalid_grant: expired or revoked
        if (!fresh)
        {
            await store.ClearAsync();
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(client, [YouTubeService.Scope.YoutubeForceSsl], "channel", ct, store);
        }
        return new YouTubeService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Passport Bro News reaction pipeline" });
    }

    /// <summary>The signed-in channel's name, to confirm the right account (1 quota unit).</summary>
    public static async Task<string> ChannelAsync(CancellationToken ct = default)
    {
        using var yt = await ConnectAsync(ct);
        var req = yt.Channels.List("snippet");
        req.Mine = true;
        var res = await req.ExecuteAsync(ct);
        return res.Items is { Count: > 0 } items ? $"{items[0].Snippet.Title} ({items[0].Id})" : "(no channel on this account)";
    }

    /// <summary>Uploads the episode in <paramref name="folder"/> according to <paramref name="plan"/>. Safe to call again after a failure.</summary>
    public static async Task<State> PublishAsync(string folder, Plan plan, Action<string> log, CancellationToken ct = default)
    {
        string F(string name) => Path.Combine(folder, name);
        if (!File.Exists(F("reaction.mp4"))) throw new InvalidOperationException("No reaction.mp4: render it first.");
        if (!File.Exists(F("upload.txt"))) throw new InvalidOperationException("No upload.txt: run package first.");
        if (!plan.Premiere && plan.At < DateTimeOffset.Now.AddMinutes(15))
            throw new InvalidOperationException($"{ToEastern(plan.At):ddd d MMM h:mm tt} ET is in the past or too soon: pick a later time.");
        var text = ParseUploadText(File.ReadAllText(F("upload.txt")));

        var state = LoadState(folder) ?? new State();
        if (state.VideoId is not null && (state.PublishAt != plan.At || state.Premiere != plan.Premiere))
            log($"Already uploaded as {Watch(state.VideoId)}; continuing with the original schedule ({(state.Premiere ? "premiere" : ToEastern(state.PublishAt!.Value).ToString("ddd h:mm tt"))}). Change the time in Studio if needed.");
        else { state.Title = FitTitle(plan.Title); state.PublishAt = plan.At; state.Premiere = plan.Premiere; }

        using var yt = await ConnectAsync(ct);

        if (state.VideoId is null)
        {
            log("Uploading the reaction video…");
            state.VideoId = await UploadAsync(yt, F("reaction.mp4"), state.Title!, FitDescription(text.Description), FitTags(text.Tags),
                state.Premiere ? null : state.PublishAt, log, ct);
            Save(folder, state);
            log($"Uploaded: {Watch(state.VideoId)}");
        }

        if (!state.Done.Contains("thumbnail") && File.Exists(F("thumbnail.jpg")))
        {
            log("Setting the thumbnail…");
            await SetThumbnailAsync(yt, state.VideoId, F("thumbnail.jpg"), ct);
            state.Done.Add("thumbnail"); Save(folder, state);
        }

        if (!state.Done.Contains("captions") && File.Exists(F("captions.srt")))
        {
            log("Adding captions…");
            await using var srt = File.OpenRead(F("captions.srt"));
            var caption = new Caption { Snippet = new CaptionSnippet { VideoId = state.VideoId, Language = "en", Name = "English", IsDraft = false } };
            var up = await yt.Captions.Insert(caption, "snippet", srt, "application/octet-stream").UploadAsync(ct);
            if (up.Status != UploadStatus.Completed) throw new InvalidOperationException($"Captions failed: {up.Exception?.Message}");
            state.Done.Add("captions"); Save(folder, state);
        }

        if (!state.Done.Contains("playlist") && Settings.YouTubePlaylistId is { } playlist)
        {
            log("Adding it to the reactions playlist…");
            await yt.PlaylistItems.Insert(new PlaylistItem
            {
                Snippet = new PlaylistItemSnippet { PlaylistId = playlist, ResourceId = new ResourceId { Kind = "youtube#video", VideoId = state.VideoId } },
            }, "snippet").ExecuteAsync(ct);
            state.Done.Add("playlist"); Save(folder, state);
        }

        if (plan.WithShort && state.ShortId is null && File.Exists(F("short.mp4")))
        {
            var at = state.PublishAt!.Value - ShortLead;
            var when = state.Premiere ? $"premieres {ToEastern(state.PublishAt.Value):ddd h:mm tt} ET" : $"out {ToEastern(state.PublishAt.Value):ddd h:mm tt} ET";
            var description = FitDescription($"Full breakdown ({when}) 👉 {Watch(state.VideoId)}\n\n{text.Description.Split("\n\n")[0]}\n\n#Shorts #PassportBros");
            log("Uploading the preview Short…");
            state.ShortId = await UploadAsync(yt, F("short.mp4"), ShortTitle(folder, state.Title!), description, FitTags(text.Tags),
                at > DateTimeOffset.Now.AddMinutes(15) ? at : null, log, ct, publicNow: at <= DateTimeOffset.Now.AddMinutes(15));
            state.ShortAt = at;
            Save(folder, state);
            log($"Short uploaded: {Watch(state.ShortId)}");
        }

        await File.WriteAllTextAsync(F("published.json"), JsonConvert.SerializeObject(new
        {
            at = DateTime.Now,
            url = Watch(state.VideoId),
            studio = Studio(state.VideoId),
            publishAt = state.PublishAt,
            premiere = state.Premiere,
            shortUrl = state.ShortId is null ? null : Watch(state.ShortId),
            shortAt = state.ShortAt,
        }, Formatting.Indented), ct);
        log(state.Premiere
            ? $"Done. Now open {Studio(state.VideoId)} and set it to Premiere at {ToEastern(state.PublishAt!.Value):ddd d MMM h:mm tt} ET."
            : $"Done. Goes public {ToEastern(state.PublishAt!.Value):ddd d MMM h:mm tt} ET.");
        return state;
    }

    /// <summary>One resumable upload. Private until <paramref name="publishAt"/> (or kept private when null, unless <paramref name="publicNow"/>).</summary>
    static async Task<string> UploadAsync(YouTubeService yt, string file, string title, string description, List<string> tags,
        DateTimeOffset? publishAt, Action<string> log, CancellationToken ct, bool publicNow = false)
    {
        var video = new Video
        {
            Snippet = new VideoSnippet
            {
                Title = title, Description = description, Tags = tags,
                CategoryId = "22", // People & Blogs
                DefaultLanguage = "en", DefaultAudioLanguage = "en",
            },
            Status = new VideoStatus
            {
                PrivacyStatus = publicNow ? "public" : "private",
                PublishAtDateTimeOffset = publishAt,
                SelfDeclaredMadeForKids = false,
                ContainsSyntheticMedia = true, // the narration is an AI voice
                Embeddable = true,
            },
        };
        await using var stream = File.OpenRead(file);
        var request = yt.Videos.Insert(video, "snippet,status", stream, "video/*");
        string? id = null;
        request.ResponseReceived += v => id = v.Id;
        var lastShown = -1L;
        request.ProgressChanged += p =>
        {
            var pct = stream.Length == 0 ? 100 : p.BytesSent * 100 / stream.Length;
            if (p.Status == UploadStatus.Uploading && pct / 10 != lastShown) { lastShown = pct / 10; log($"  {pct}%"); }
        };
        var result = await request.UploadAsync(ct);
        if (result.Status != UploadStatus.Completed || id is null)
            throw new InvalidOperationException($"Upload of {Path.GetFileName(file)} failed: {result.Exception?.Message ?? result.Status.ToString()}");
        return id;
    }

    static async Task SetThumbnailAsync(YouTubeService yt, string videoId, string jpg, CancellationToken ct)
    {
        if (new FileInfo(jpg).Length > 2_000_000)
            throw new InvalidOperationException("thumbnail.jpg is over YouTube's 2 MB limit.");
        await using var s = File.OpenRead(jpg);
        var up = await yt.Thumbnails.Set(videoId, s, "image/jpeg").UploadAsync(ct);
        if (up.Status != UploadStatus.Completed)
            throw new InvalidOperationException($"Thumbnail failed: {up.Exception?.Message}. Custom thumbnails need a phone-verified channel (studio.youtube.com → Settings → Channel → Feature eligibility).");
    }
}
