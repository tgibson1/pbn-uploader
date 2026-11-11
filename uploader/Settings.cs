namespace PbnUploader;

/// <summary>Where the uploader finds its OAuth client and keeps its sign-in. Nothing secret is in source.</summary>
public static class Settings
{
    /// <summary>The OAuth client (Desktop app) downloaded from Google Cloud. Never commit it.</summary>
    public static string YouTubeClientSecrets =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("YOUTUBE_CLIENT_SECRETS") ?? "youtube_client_secret.json");

    /// <summary>The signed-in token, kept under the user's AppData (outside the repo).</summary>
    public static string YouTubeTokenFolder =>
        Environment.GetEnvironmentVariable("YOUTUBE_TOKEN_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PassportBroNews", "youtube-token");

    /// <summary>Optional: a playlist each uploaded video is added to.</summary>
    public static string? YouTubePlaylistId =>
        Environment.GetEnvironmentVariable("YOUTUBE_PLAYLIST_ID") is { Length: > 0 } p ? p : null;
}
