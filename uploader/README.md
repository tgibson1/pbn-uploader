# pbn-uploader: the upload script

The complete upload code used by Passport Bro News Uploader: a .NET 8 console app using the official
Google APIs client library for YouTube (`Google.Apis.YouTube.v3`).

## What it does
For one episode folder that the channel owner has reviewed and approved:
1. `videos.insert`: uploads `reaction.mp4` to the owner's channel, private until its scheduled publish time,
   with the title, description and tags from `upload.txt`, category People & Blogs, not made for kids,
   and the altered/synthetic content flag set (the narration is an AI voice).
2. `thumbnails.set`: sets `thumbnail.jpg`.
3. `captions.insert`: adds `captions.srt`.
4. `playlistItems.insert`: adds it to a playlist (only if `YOUTUBE_PLAYLIST_ID` is set).
5. `videos.insert`: uploads `short.mp4` as a preview Short, scheduled 3 hours earlier, linking to the video.

Each finished step is recorded in `youtube.json`, so a rerun after a failure resumes without uploading twice.
`channels.list` (`login`) confirms which channel is signed in. OAuth scope: `youtube.force-ssl`.

## Run it
1. Create a Desktop-app OAuth client for a Google Cloud project with the YouTube Data API v3 enabled, and save
   it as `youtube_client_secret.json` here (git-ignored, never committed).
2. `dotnet run -- login`: sign in once in the browser; prints the channel.
3. `dotnet run -- upload <episode-folder> --at "2026-11-12 14:00"` (US Eastern time).
   Add `--premiere` to upload it private for a Premiere set in YouTube Studio, `--no-short` to skip the Short.

No keys, tokens or secrets are in this code. The sign-in token is stored under `%APPDATA%\PassportBroNews\youtube-token`.
See the [Privacy Policy](../PRIVACY.md) and [Terms of Service](../TERMS.md).
