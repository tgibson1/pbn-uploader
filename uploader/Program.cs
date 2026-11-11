using System.Globalization;
using PbnUploader;

// pbn-uploader: uploads one approved episode folder to the owner's own YouTube channel.
//
//   dotnet run -- login
//       Sign in once (opens a browser) and print the signed-in channel.
//   dotnet run -- upload <episode-folder> --at "2026-11-12 12:00" [--title "..."] [--premiere] [--no-short]
//       Upload <episode-folder>/reaction.mp4 with the title, description and tags from upload.txt, thumbnail.jpg,
//       captions.srt and a scheduled publish time (US Eastern), then short.mp4 as a preview Short 3 hours earlier.
//       --premiere uploads the video private (the API can't create Premieres; set it in YouTube Studio).
//       Progress is recorded in youtube.json, so running it again after a failure resumes without re-uploading.

var argv = args.ToList();
string? Option(string flag)
{
    var i = argv.IndexOf(flag);
    if (i < 0) return null;
    if (i + 1 >= argv.Count) throw new ArgumentException($"{flag} needs a value.");
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}

try
{
    switch (argv.FirstOrDefault())
    {
        case "login":
            Console.WriteLine($"Signed in to YouTube channel: {await YouTube.ChannelAsync()}");
            return 0;

        case "upload":
            var at = Option("--at");
            var title = Option("--title");
            bool premiere = argv.Remove("--premiere"), noShort = argv.Remove("--no-short");
            if (argv.Count < 2 || at is null) break;
            var folder = Path.GetFullPath(argv[1]);
            var text = YouTube.ParseUploadText(await File.ReadAllTextAsync(Path.Combine(folder, "upload.txt")));
            var when = YouTube.FromEastern(DateTime.Parse(at, CultureInfo.InvariantCulture));
            await YouTube.PublishAsync(folder,
                new YouTube.Plan(title ?? text.Titles.FirstOrDefault() ?? Path.GetFileName(folder), when, premiere, !noShort),
                Console.WriteLine);
            return 0;
    }
    Console.WriteLine("Usage:\n  pbn-uploader login\n  pbn-uploader upload <episode-folder> --at \"2026-11-12 12:00\" [--title \"...\"] [--premiere] [--no-short]");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
