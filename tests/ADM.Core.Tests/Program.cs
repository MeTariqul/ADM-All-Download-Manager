using System.Security.Cryptography;
using Adm.Core;
using Adm.Core.Media;
using Adm.Core.Models;
using Adm.Core.Net;
using Adm.Core.Settings;
using Adm.Core.Storage;
using Adm.Core.Util;
using Adm.Core.Engine;
using Adm.Core.Licensing;

namespace Adm.Core.Tests;

internal static class Program
{
    // A licence token signed with a throwaway key, produced for the test suite.
    // The public key and the token are a matched pair.
    private const string FixturePublicKey =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MCowBQYDK2VwAyEAA6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg\n" +
        "-----END PUBLIC KEY-----";

    private const string FixtureToken =
        "eyJkIjoiYWRtLXRlc3QtZGV2aWNlIiwiZSI6NDEwMjQ0NDgwMDAwMCwiciI6NDEwMjQ0NDgwMDAwMCwiayI6IkFETS1URVNULUtFWSJ9." +
        "LWViXVRa_jy5XONrPC481CNzxF2GUfZ3hZ73Qz-yS-14I8fYzQ7YhXEkBSDaPIU1uN0jjQbnYJUqWsCcKNPmAw";

    private static int _failures;

    private static async Task<int> Main()
    {
        Console.WriteLine("ADM download engine tests");
        Console.WriteLine("=========================");

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("segment planner covers the file exactly", PlannerCoversFile),
            ("file name resolution and sanitising", FileNames),
            ("probe reports size, ranges and ETag", Probe),
            ("segmented download produces identical bytes", SegmentedDownload),
            ("server without Range support falls back to one stream", StreamingFallback),
            ("pause, restart the app and resume without corruption", PauseAndResume),
            ("video formats are grouped into resolution choices", VideoFormats),
            ("video format errors are readable", VideoFormatErrors),
            ("video URL detection", VideoUrls),
            ("yt-dlp progress lines parse", VideoProgress),
            ("video download records round-trip", VideoRecord),
            ("yt-dlp integration: probe and download (opt-in)", YtDlpIntegration),
            ("yt-dlp integration: pause and resume (opt-in)", YtDlpPauseResumeIntegration),
            ("checksums are computed and compared", Checksums),
            ("proxy and basic auth are built correctly", NetworkOptionTests),
            ("disk space is measured for a folder", DiskSpaceCheck),
            ("duplicate downloads follow the policy", DuplicatePolicyTests),
            ("playlist listings are parsed", PlaylistParsing),
            ("new per-download settings survive a restart", NewRecordFields),
            ("FTP addresses are parsed", FtpAddressParsing),
            ("a failing download is retried with a backoff", RetryBackoff),
            ("Ed25519 verifies RFC 8032 test vectors", Ed25519Vectors),
            ("license tokens are parsed and verified", LicenseTokens),
            ("the licence state machine works", LicenseServiceStates),
            ("the device id is stable", DeviceIdentityTests),
            ("the app key can be overridden at run time", AppKeyOverride),
            ("a live licence token verifies offline (opt-in)", LicenseLiveIntegration),
            ("FTP download against a local server (opt-in)", FtpIntegration)
        };

        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("=========================");
        Console.WriteLine(_failures == 0 ? "All tests passed." : $"{_failures} test(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static Task PlannerCoversFile()
    {
        const long total = 10_000_007;
        var plan = SegmentPlanner.Plan(total, 8);

        Assert.Equal(8, plan.Count);
        Assert.Equal(0L, plan[0].Start);
        Assert.Equal(total - 1, plan[^1].End);

        long expectedStart = 0;
        foreach (var segment in plan)
        {
            Assert.Equal(expectedStart, segment.Start);
            Assert.Equal(segment.Start, segment.Position);
            Assert.True(segment.End >= segment.Start, "segment end must be after its start");
            expectedStart = segment.End + 1;
        }

        Assert.Equal(total, expectedStart);
        Assert.Equal(1, SegmentPlanner.Plan(100, 8).Count);

        var streaming = SegmentPlanner.Plan(-1, 8);
        Assert.Equal(1, streaming.Count);
        Assert.Equal(-1L, streaming[0].End);

        return Task.CompletedTask;
    }

    private static Task FileNames()
    {
        Assert.Equal("server-name.bin",
            FileNameResolver.Resolve("server-name.bin", null, "http://example.com/other", "application/octet-stream"));
        Assert.Equal("video.mp4", FileNameResolver.Resolve(null, null, "http://example.com/media/video.mp4", "video/mp4"));
        Assert.Equal("movie.mp4", FileNameResolver.Resolve(null, "movie.mp4", "http://example.com/x", "video/mp4"));
        Assert.Equal("download.bin", FileNameResolver.Resolve(null, null, "http://example.com/", null));

        string sanitized = FileNameResolver.Sanitize("bad<>:|?*name.txt");
        Assert.False(sanitized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0, "sanitized name still has invalid characters");

        using var temp = new TempDirectory();
        string existing = Path.Combine(temp.Path, "report.pdf");
        File.WriteAllText(existing, "x");
        string unique = FileNameResolver.UniquePath(temp.Path, "report.pdf");
        Assert.Equal("report (2).pdf", Path.GetFileName(unique));

        return Task.CompletedTask;
    }

    private static async Task Probe()
    {
        var payload = CreatePayload(512 * 1024);
        await using var server = new RangeTestServer(payload, throttleMs: 0);
        using var client = HttpProbe.CreateClient();

        var result = await HttpProbe.ProbeAsync(client, server.BaseUrl + "/file", "test-agent", null, CancellationToken.None);

        Assert.True(result.SupportsRanges, "server should report Range support");
        Assert.Equal((long)payload.Length, result.TotalBytes);
        Assert.Equal("\"v1\"", result.ETag);
        Assert.Equal("server-name.bin", result.ContentDispositionFileName);
    }

    private static async Task SegmentedDownload()
    {
        var payload = CreatePayload(8 * 1024 * 1024 + 12345);
        var expectedHash = Hash(payload);

        await using var server = new RangeTestServer(payload);
        using var temp = new TempDirectory();
        using var manager = CreateManager(temp.Path, maxConnections: 8, maxConcurrent: 2);

        var item = manager.Add(server.BaseUrl + "/files/payload.bin");
        await WaitUntil(() => item.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(60),
            () => $"download did not complete (status {item.Status}, {item.SizeText})");

        Assert.True(File.Exists(item.FilePath), "final file is missing");
        Assert.Equal("server-name.bin", Path.GetFileName(item.FilePath));
        Assert.True(server.RangeRequestCount >= 2, $"expected parallel range requests, saw {server.RangeRequestCount}");
        Assert.Equal(expectedHash, Hash(File.ReadAllBytes(item.FilePath)));
        Assert.False(File.Exists(item.TempPath), ".admpart file should be removed after completion");
        Assert.Equal(payload.Length, item.BytesDownloaded);
    }

    private static async Task StreamingFallback()
    {
        var payload = CreatePayload(3 * 1024 * 1024);
        var expectedHash = Hash(payload);

        await using var server = new RangeTestServer(payload, supportRanges: false);
        using var temp = new TempDirectory();
        using var manager = CreateManager(temp.Path, maxConnections: 8, maxConcurrent: 1);

        var item = manager.Add(server.BaseUrl + "/stream.bin");
        await WaitUntil(() => item.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(60),
            () => $"streaming download did not complete (status {item.Status})");

        Assert.Equal(expectedHash, Hash(File.ReadAllBytes(item.FilePath)));
        Assert.Equal(0, server.RangeRequestCount);
        Assert.Equal(payload.Length, item.TotalBytes);
    }

    private static async Task PauseAndResume()
    {
        var payload = CreatePayload(12 * 1024 * 1024);
        var expectedHash = Hash(payload);
        string storePath;

        await using var server = new RangeTestServer(payload, throttleMs: 10, chunkSize: 16 * 1024);
        using var temp = new TempDirectory();
        storePath = Path.Combine(temp.Path, "downloads.json");

        DownloadItem paused;

        var settings = new AppSettings
        {
            DownloadFolder = temp.Path,
            MaxConnections = 8,
            MaxConcurrentDownloads = 2
        };

        using (var manager = new DownloadManager(settings, new DownloadStore(storePath)))
        {
            paused = manager.Add(server.BaseUrl + "/big.bin");

            await WaitUntil(() => paused.BytesDownloaded > payload.Length / 5, TimeSpan.FromSeconds(30),
                () => $"download never reached 20% ({paused.SizeText})");

            await manager.PauseAsync(paused);
            Assert.Equal(DownloadStatus.Paused, paused.Status);
            Assert.True(File.Exists(paused.TempPath), "partial file is missing after pause");
            Assert.Equal(payload.Length, new FileInfo(paused.TempPath).Length);
            Assert.True(paused.BytesDownloaded > 0, "no progress was recorded before pausing");

            await manager.ShutdownAsync();
        }

        // A brand new manager simulates restarting the application.
        using (var manager = new DownloadManager(settings, new DownloadStore(storePath)))
        {
            manager.Load();
            var restored = manager.Items.Single(i => i.Url.EndsWith("/big.bin", StringComparison.Ordinal));

            Assert.Equal(DownloadStatus.Paused, restored.Status);
            Assert.True(restored.Segments.Count > 0, "resume data was not restored");
            Assert.True(restored.Segments.Any(s => s.Position > s.Start), "restored segments contain no progress");

            manager.Resume(restored);
            await WaitUntil(() => restored.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(60),
                () => $"resumed download did not complete (status {restored.Status}, {restored.SizeText})");

            Assert.Equal(expectedHash, Hash(File.ReadAllBytes(restored.FilePath)));
        }
    }

    private static Task VideoFormats()
    {
        const string json = """
        {
          "title": "Sample video",
          "id": "abc123",
          "webpage_url": "https://www.youtube.com/watch?v=abc123",
          "uploader": "Tester",
          "duration": 125,
          "is_live": false,
          "formats": [
            { "format_id": "137", "ext": "mp4", "height": 1080, "fps": 30, "vcodec": "avc1.640028", "acodec": "none", "protocol": "https", "filesize": 104857600, "tbr": 4000 },
            { "format_id": "299", "ext": "mp4", "height": 1080, "fps": 60, "vcodec": "av01.0.08M.08", "acodec": "none", "protocol": "https", "filesize": 78643200, "tbr": 3000 },
            { "format_id": "248", "ext": "webm", "height": 1080, "fps": 30, "vcodec": "vp9", "acodec": "none", "protocol": "https", "filesize": 83886080, "tbr": 3500 },
            { "format_id": "137hls", "ext": "mp4", "height": 1080, "fps": 60, "vcodec": "avc1.640028", "acodec": "none", "protocol": "m3u8_native", "filesize": 900000000, "tbr": 7000 },
            { "format_id": "18", "ext": "mp4", "height": 360, "fps": 30, "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "protocol": "https", "filesize": 20971520, "tbr": 800 },
            { "format_id": "140", "ext": "m4a", "height": null, "fps": null, "vcodec": "none", "acodec": "mp4a.40.2", "protocol": "https", "filesize": 10485760, "tbr": 129, "abr": 129 },
            { "format_id": "251", "ext": "webm", "height": null, "fps": null, "vcodec": "none", "acodec": "opus", "protocol": "https", "filesize": 10200000, "tbr": 128, "abr": 128 }
          ]
        }
        """;

        var info = YtDlpFormatParser.Parse(json, "https://example.com/video");
        Assert.Equal("Sample video", info.Title);
        Assert.Equal("abc123", info.Id);
        Assert.Equal("Tester", info.Uploader);
        Assert.Equal("2:05", info.DurationText);
        Assert.Equal(5, info.Formats.Count);
        Assert.True(info.SummaryText.Contains("Tester", StringComparison.Ordinal), "summary is missing uploader");
        Assert.Equal(1, info.Formats.Count(format => format.IsRecommended));

        var top = info.Formats[0];
        Assert.True(top.IsRecommended, "the highest resolution should be recommended");
        Assert.Equal("1080p", top.ResolutionText);
        Assert.Equal("MP4 · H.264", top.FormatText);
        Assert.Equal("mp4", top.Extension);
        Assert.Equal("mp4", top.MergeContainer);
        Assert.True(top.NeedsMerge, "video-only formats must be merged with audio");
        Assert.True(top.Selector.Contains("137+bestaudio", StringComparison.Ordinal), "wrong merge selector: " + top.Selector);
        Assert.True(top.EstimatedBytes > 104857600, "audio size should be added to the video estimate");

        var webm = info.Formats[1];
        Assert.Equal("1080p", webm.ResolutionText);
        Assert.Equal("MKV · VP9", webm.FormatText);
        Assert.True(webm.Selector.Contains("248+bestaudio", StringComparison.Ordinal), "wrong webm selector: " + webm.Selector);
        Assert.False(webm.IsRecommended, "only one format should be recommended");

        var progressive = info.Formats.Single(format => format.Selector == "18");
        Assert.Equal("360p", progressive.ResolutionText);
        Assert.Equal("MP4 · H.264", progressive.FormatText);
        Assert.False(progressive.NeedsMerge, "progressive formats do not need a merge");

        var m4a = info.Formats.Single(format => format.IsAudioOnly && format.Extension == "m4a");
        Assert.Equal("Audio only", m4a.ResolutionText);
        Assert.True(m4a.Selector.Contains("bestaudio[ext=m4a]", StringComparison.Ordinal), "wrong m4a selector");
        Assert.False(m4a.IsRecommended, "a video format should be recommended when one exists");

        var mp3 = info.Formats.Single(format => format.ConvertToMp3);
        Assert.Equal("mp3", mp3.Extension);
        Assert.True(mp3.IsAudioOnly, "mp3 should be marked audio-only");

        Assert.False(info.Formats.Any(format => format.Selector.Contains("137hls", StringComparison.Ordinal)),
            "HLS duplicate should not be selected when direct HTTPS formats exist");
        Assert.False(info.Formats.Any(format => format.EstimatedBytes >= 900000000),
            "inflated HLS sizes leaked into the picker");

        return Task.CompletedTask;
    }

    private static Task VideoFormatErrors()
    {
        const string live = """
        { "id": "live1", "title": "Live now", "is_live": true, "formats": [] }
        """;
        const string playlist = """
        { "_type": "playlist", "id": "pl1", "title": "A playlist", "entries": [{ "id": "a" }, { "id": "b" }] }
        """;

        Assert.Throws<YtDlpException>(() => YtDlpFormatParser.Parse(live, "https://example.com/live"),
            "live streams should be rejected");
        Assert.Throws<YtDlpException>(() => YtDlpFormatParser.Parse(playlist, "https://example.com/list"),
            "playlists should be rejected");

        return Task.CompletedTask;
    }

    private static Task VideoUrls()
    {
        Assert.True(VideoUrlDetector.IsVideoPage("https://www.youtube.com/watch?v=aqz-KE-bpKQ"), "watch URL not recognised");
        Assert.True(VideoUrlDetector.IsVideoPage("https://youtu.be/aqz-KE-bpKQ?t=10"), "short URL not recognised");
        Assert.True(VideoUrlDetector.IsVideoPage("https://www.youtube.com/shorts/abc123"), "shorts URL not recognised");
        Assert.True(VideoUrlDetector.IsVideoPage("https://vimeo.com/123456789"), "vimeo URL not recognised");
        Assert.True(VideoUrlDetector.IsVideoPage("https://www.twitch.tv/someone/videos/123456"), "twitch URL not recognised");
        Assert.False(VideoUrlDetector.IsVideoPage("https://www.youtube.com/@somechannel"), "channel page should not be a video");
        Assert.False(VideoUrlDetector.IsVideoPage("https://vimeo.com/"), "vimeo home should not be a video");
        Assert.False(VideoUrlDetector.IsVideoPage("https://example.com/movie.mp4"), "direct files use the HTTP engine");
        Assert.False(VideoUrlDetector.IsVideoPage("https://notyoutube.com/watch?v=x"), "look-alike host should not be treated as YouTube");
        Assert.False(VideoUrlDetector.IsVideoPage("ftp://youtube.com/watch?v=x"), "non-HTTP URLs are unsupported");
        Assert.False(VideoUrlDetector.IsVideoPage("not a url"), "plain text should be rejected");

        Assert.True(VideoUrlDetector.IsDirectMediaFile("https://cdn.example.com/clip.mp4"),
            "direct mp4 was not recognised");
        Assert.True(VideoUrlDetector.IsDirectMediaFile("https://cdn.example.com/song.mp3?token=1"),
            "direct mp3 with a query was not recognised");
        Assert.False(VideoUrlDetector.IsDirectMediaFile("https://www.youtube.com/watch?v=abc"),
            "a video page is not a direct media file");
        Assert.False(VideoUrlDetector.IsDirectMediaFile("https://example.com/page"),
            "a normal page is not a direct media file");

        Assert.True(VideoUrlDetector.IsKnownVideoSite("https://www.youtube.com/watch?v=abc"),
            "youtube was not recognised as a video site");
        Assert.True(VideoUrlDetector.IsKnownVideoSite("https://m.youtube.com/shorts/abc"),
            "mobile youtube was not recognised");
        Assert.False(VideoUrlDetector.IsKnownVideoSite("https://notyoutube.com/watch?v=x"),
            "look-alike host should not be treated as a video site");
        Assert.False(VideoUrlDetector.IsKnownVideoSite("https://example.com/movie.mp4"),
            "a plain file host is not a video site");

        return Task.CompletedTask;
    }

    private static Task VideoProgress()
    {
        Assert.True(YtDlpProgress.TryParse("ADMPROG|1024|2048|NA|512.5|4", out var parsed),
            "valid progress line was rejected");
        Assert.Equal(1024L, parsed.DownloadedBytes);
        Assert.Equal(2048L, parsed.TotalBytes);
        Assert.Equal(512.5, parsed.BytesPerSecond);
        Assert.Equal(4, parsed.EtaSeconds);

        Assert.True(YtDlpProgress.TryParse("ADMPROG|1024|0|4096|NA|NA", out var estimate),
            "estimate-only progress line was rejected");
        Assert.Equal(4096L, estimate.TotalBytes);
        Assert.Equal<int?>(null, estimate.EtaSeconds);

        Assert.False(YtDlpProgress.TryParse("ADMFILE|C:\\video.mp4", out _), "file line is not progress");
        Assert.False(YtDlpProgress.TryParse("ordinary output", out _), "ordinary output is not progress");
        return Task.CompletedTask;
    }

    private static Task VideoRecord()
    {
        var item = new DownloadItem
        {
            Kind = DownloadKind.Video,
            Url = "https://www.youtube.com/watch?v=abc123",
            FilePath = @"C:\Downloads\Sample [abc123].mp4",
            Category = "Video",
            VideoSelector = "137+bestaudio[ext=m4a]/bestaudio",
            VideoFormatLabel = "1080p · MP4 · H.264",
            VideoMergeContainer = "mp4",
            VideoAudioOnly = false,
            VideoConvertToMp3 = false,
            TotalBytes = 1000
        };

        var restored = DownloadRecord.FromItem(item).ToItem();
        Assert.Equal(DownloadKind.Video, restored.Kind);
        Assert.Equal(item.VideoSelector, restored.VideoSelector);
        Assert.Equal(item.VideoFormatLabel, restored.VideoFormatLabel);
        Assert.Equal("mp4", restored.VideoMergeContainer);
        Assert.Equal("Video", restored.Category);
        Assert.False(restored.VideoAudioOnly, "audio flag was lost");

        var legacy = new DownloadRecord
        {
            Url = "https://example.com/file.bin",
            FilePath = @"C:\Downloads\file.bin",
            Status = DownloadStatus.Completed
        }.ToItem();
        Assert.Equal(DownloadKind.Http, legacy.Kind);
        Assert.Equal(DownloadStatus.Completed, legacy.Status);
        return Task.CompletedTask;
    }

    private static async Task YtDlpIntegration()
    {        if (!string.Equals(Environment.GetEnvironmentVariable("ADM_YTDLP_INTEGRATION"), "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var service = new YtDlpService();
        Assert.True(service.IsAvailable, "yt-dlp was not found (expected vendor\\yt-dlp.exe)");
        Assert.True(service.HasFfmpeg, "ffmpeg was not found on PATH");

        var info = await service.GetInfoAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ");
        Assert.Equal("aqz-KE-bpKQ", info.Id);
        Assert.True(info.Formats.Count >= 3, "not enough format choices were returned");
        Assert.True(info.Formats.Any(format => format.IsRecommended), "no format was recommended");

        var candidates = info.Formats
            .Where(format => !format.IsAudioOnly && format.NeedsMerge)
            .ToList();
        Assert.True(candidates.Count > 0, "no merged video+audio choices were returned");
        var chosen = candidates.FirstOrDefault(format => format.ResolutionText == "360p") ?? candidates[^1];

        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            DownloadFolder = temp.Path,
            MaxConnections = 4,
            MaxConcurrentDownloads = 1
        };

        using var manager = new DownloadManager(settings, new DownloadStore(Path.Combine(temp.Path, "downloads.json")));
        var item = manager.AddVideo(info, chosen);
        Assert.Equal(DownloadKind.Video, item.Kind);
        Assert.Equal("Video", item.Category);

        await WaitUntil(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            TimeSpan.FromMinutes(5),
            () => $"video download did not finish (status {item.Status}, {item.ErrorMessage ?? "no error"})");

        if (item.Status == DownloadStatus.Failed)
            throw new InvalidOperationException("video download failed: " + item.ErrorMessage);

        Assert.True(File.Exists(item.FilePath), "the video file is missing");
        Assert.True(new FileInfo(item.FilePath).Length > 1024 * 1024, "the video file is suspiciously small");
        Assert.True(Path.GetExtension(item.FilePath) is ".mp4" or ".mkv" or ".webm",
            "unexpected video extension: " + Path.GetExtension(item.FilePath));
        Assert.True(item.BytesDownloaded == new FileInfo(item.FilePath).Length,
            "progress was not finalised to the file length");
    }

    private static async Task YtDlpPauseResumeIntegration()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ADM_YTDLP_INTEGRATION"), "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var service = new YtDlpService();
        Assert.True(service.IsAvailable, "yt-dlp was not found (expected vendor\\yt-dlp.exe)");
        Assert.True(service.HasFfmpeg, "ffmpeg was not found on PATH");

        var info = await service.GetInfoAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ");
        var candidates = info.Formats
            .Where(format => !format.IsAudioOnly && format.NeedsMerge)
            .ToList();
        Assert.True(candidates.Count > 0, "no merged video+audio choices were returned");
        var chosen = candidates.FirstOrDefault(format => format.ResolutionText == "360p") ?? candidates[^1];

        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            DownloadFolder = temp.Path,
            MaxConnections = 4,
            MaxConcurrentDownloads = 1,
            SpeedLimitBytesPerSecond = 100 * 1024
        };

        using var manager = new DownloadManager(settings, new DownloadStore(Path.Combine(temp.Path, "downloads.json")));
        var item = manager.AddVideo(info, chosen);

        await WaitUntil(() => item.BytesDownloaded > 512 * 1024 ||
                             item.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            TimeSpan.FromMinutes(3),
            () => $"video never reached the pause point (status {item.Status}, {item.BytesDownloaded} bytes, " +
                  $"{item.ErrorMessage ?? "no error"})");

        Assert.True(item.Status != DownloadStatus.Completed,
            "the video finished before it could be paused; raise the test rate limit");

        await manager.PauseAsync(item);
        Assert.Equal(DownloadStatus.Paused, item.Status);

        await Task.Delay(500);
        bool hasPartial = Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Any(file => file.Contains(".part", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasPartial, "yt-dlp did not leave a resumable partial file behind");

        settings.SpeedLimitBytesPerSecond = 0;
        manager.ApplySettings();
        manager.Resume(item);

        await WaitUntil(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            TimeSpan.FromMinutes(5),
            () => $"resumed video did not finish (status {item.Status}, {item.ErrorMessage ?? "no error"})");

        if (item.Status == DownloadStatus.Failed)
            throw new InvalidOperationException("resumed video download failed: " + item.ErrorMessage);

        Assert.True(File.Exists(item.FilePath), "the resumed video file is missing");
        Assert.True(new FileInfo(item.FilePath).Length > 1024 * 1024, "the resumed video is suspiciously small");

        bool leftover = Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Any(file => file.Contains(".part", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase));
        Assert.False(leftover, "partial files were left behind after a successful resume");
    }

    private static Task Checksums()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "sample.bin");

        byte[] payload = CreatePayload(4096);
        File.WriteAllBytes(path, payload);

        string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        string actual = Checksum.Compute(path, ChecksumAlgorithm.Sha256);
        Assert.Equal(expected, actual);
        Assert.True(Checksum.Matches(expected, actual), "the checksum did not match");
        Assert.True(Checksum.Matches(expected.ToUpperInvariant(), actual), "case was not ignored");
        Assert.True(Checksum.Matches(expected, " " + expected + "\n"), "separators were not ignored");
        Assert.False(Checksum.Matches("deadbeef", actual), "a wrong checksum was accepted");

        Assert.Equal(ChecksumAlgorithm.Sha256, Checksum.ParseAlgorithm("SHA-256"));
        Assert.Equal(ChecksumAlgorithm.Md5, Checksum.ParseAlgorithm("md5"));
        Assert.Equal(ChecksumAlgorithm.None, Checksum.ParseAlgorithm("nonsense"));
        Assert.Equal(32, Checksum.Compute(path, ChecksumAlgorithm.Md5).Length);
        Assert.Equal(40, Checksum.Compute(path, ChecksumAlgorithm.Sha1).Length);

        return Task.CompletedTask;
    }

    private static Task NetworkOptionTests()
    {
        Assert.True(NetworkOptions.CreateProxy(null, null, null) is null, "no proxy was expected");
        Assert.True(NetworkOptions.CreateProxy("   ", null, null) is null, "a blank proxy was expected");

        var proxy = NetworkOptions.CreateProxy("http://proxy.local:8080", "user", "secret");
        Assert.True(proxy is not null, "the proxy was not created");

        var webProxy = proxy!;
        string target = webProxy.GetProxy(new Uri("https://example.com/file.bin"))?.AbsoluteUri ?? string.Empty;
        Assert.True(target.Contains("proxy.local"), "the proxy address was not applied");

        // A bare host and port is accepted as well.
        Assert.True(NetworkOptions.CreateProxy("proxy.local:3128", null, null) is not null,
            "a proxy without a scheme was rejected");

        string? auth = NetworkOptions.BuildBasicAuth("user", "secret");
        Assert.True(auth is not null, "the basic auth header was not created");
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("user:secret")), auth);
        Assert.True(NetworkOptions.BuildBasicAuth(null, "secret") is null, "auth without a user was created");

        return Task.CompletedTask;
    }

    private static Task DiskSpaceCheck()
    {
        using var temp = new TempDirectory();

        var info = DiskSpace.ForPath(temp.Path);
        Assert.True(info.IsKnown, "the free space of a local folder should be known");
        Assert.True(info.FreeBytes >= 0, "the free space must not be negative");

        // A file that clearly does not fit must be reported.
        string? warning = DiskSpace.Check(temp.Path, long.MaxValue / 2);
        Assert.True(warning is not null, "a download that does not fit was not reported");
        Assert.True(warning!.Contains("free space"), "the warning does not mention the free space");

        // A small download fits.
        Assert.True(DiskSpace.Check(temp.Path, 1024) is null, "a small download was reported as too large");

        return Task.CompletedTask;
    }

    private static async Task DuplicatePolicyTests()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            DownloadFolder = temp.Path,
            MaxConcurrentDownloads = 1,
            DuplicatePolicy = DuplicatePolicy.Skip
        };

        using var manager = new DownloadManager(settings, new DownloadStore(Path.Combine(temp.Path, "downloads.json")));

        const string url = "https://example.com/duplicate.bin";
        var first = manager.Add(url);
        var second = manager.Add(url);

        Assert.True(ReferenceEquals(first, second), "the duplicate policy was not applied");
        Assert.Equal(1, manager.Items.Count);

        settings.DuplicatePolicy = DuplicatePolicy.Allow;
        var third = manager.Add(url);
        Assert.False(ReferenceEquals(first, third), "a second copy should have been queued");
        Assert.Equal(2, manager.Items.Count);

        await manager.ShutdownAsync();
    }

    private static Task PlaylistParsing()
    {
        const string json = """
        {
          "_type": "playlist",
          "title": "My Playlist",
          "uploader": "Some Channel",
          "webpage_url": "https://www.youtube.com/playlist?list=PL123",
          "entries": [
            { "title": "First", "url": "https://www.youtube.com/watch?v=aaa", "id": "aaa", "duration": 125 },
            { "title": "Second", "url": "https://www.youtube.com/watch?v=bbb", "id": "bbb", "duration": 61 },
            { "title": "Third", "id": "ccc", "duration": 30 }
          ]
        }
        """;

        var playlist = YtDlpPlaylistParser.Parse(json, "https://example.com/list");
        Assert.Equal("My Playlist", playlist.Title);
        Assert.Equal("Some Channel", playlist.Uploader);
        Assert.Equal(3, playlist.Count);
        Assert.Equal("First", playlist.Entries[0].Title);
        Assert.Equal("https://www.youtube.com/watch?v=aaa", playlist.Entries[0].Url);
        Assert.Equal("2:05", playlist.Entries[0].DurationText);
        Assert.Equal("ccc", playlist.Entries[2].Id);

        // An empty playlist is an error, not an empty list.
        bool threw = false;
        try
        {
            YtDlpPlaylistParser.Parse("""{"_type":"playlist","entries":[]}""", "https://example.com/list");
        }
        catch (YtDlpException)
        {
            threw = true;
        }

        Assert.True(threw, "an empty playlist was accepted");

        // The format parser reports a playlist instead of failing when allowed.
        var info = YtDlpFormatParser.Parse(json, "https://example.com/list", allowPlaylist: true);
        Assert.True(info.IsPlaylist, "the playlist was not detected");
        Assert.Equal(3, info.Playlist!.Count);

        bool refused = false;
        try
        {
            YtDlpFormatParser.Parse(json, "https://example.com/list");
        }
        catch (YtDlpException)
        {
            refused = true;
        }

        Assert.True(refused, "a playlist should be refused when playlists are not allowed");

        return Task.CompletedTask;
    }

    private static Task NewRecordFields()
    {
        var item = new DownloadItem
        {
            Url = "https://example.com/file.bin",
            FilePath = @"C:\Downloads\file.bin",
            Cookie = "session=abc; token=def",
            Authorization = "Basic dXNlcjpwYXNz",
            Priority = 5,
            ExpectedChecksum = "abcdef",
            ChecksumAlgorithm = ChecksumAlgorithm.Sha256,
            MaxConnections = 4,
            SpeedLimitBytesPerSecond = 12345
        };

        var restored = DownloadRecord.FromItem(item).ToItem();
        Assert.Equal("session=abc; token=def", restored.Cookie);
        Assert.Equal("Basic dXNlcjpwYXNz", restored.Authorization);
        Assert.Equal(5, restored.Priority);
        Assert.Equal("abcdef", restored.ExpectedChecksum);
        Assert.Equal(ChecksumAlgorithm.Sha256, restored.ChecksumAlgorithm);
        Assert.Equal(4, restored.MaxConnections);
        Assert.Equal(12345L, restored.SpeedLimitBytesPerSecond);

        return Task.CompletedTask;
    }

    private static async Task RetryBackoff()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            DownloadFolder = temp.Path,
            MaxConcurrentDownloads = 1,
            RetryPolicy = RetryPolicy.Limited,
            MaxRetries = 2
        };

        using var manager = new DownloadManager(settings, new DownloadStore(Path.Combine(temp.Path, "downloads.json")));

        // Nothing is listening on this port, so every attempt fails.
        var item = manager.Add("http://127.0.0.1:1/missing.bin");

        await WaitUntil(() => item.RetryCount >= 1,
            TimeSpan.FromSeconds(30),
            () => $"the download was never retried (status {item.Status}, retries {item.RetryCount})");

        // The next attempt must wait for the backoff.
        Assert.True(item.RetryNotBefore > DateTimeOffset.Now,
            "the retry was scheduled without a backoff");
        Assert.Equal(DownloadStatus.Queued, item.Status);

        // The retry count never passes the configured limit.
        await Task.Delay(1500);
        Assert.True(item.RetryCount <= settings.MaxRetries,
            $"the download was retried more than {settings.MaxRetries} times");

        await manager.ShutdownAsync();
    }

    private static Task Ed25519Vectors()
    {
        // RFC 8032, section 7.1, test 1: an empty message.
        byte[] publicKey = Convert.FromBase64String("11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=");
        byte[] signature = Convert.FromBase64String(
            "5VZDAMNgrHKQhuLMgG6CioSHfx645dl02HPgZSJJAVVfuIIVkKM7rMYeOXAc+bRr0lv18FlbviRlUUFDjnoQCw==");

        Assert.True(Ed25519.Verify(publicKey, Array.Empty<byte>(), signature),
            "the RFC 8032 test vector was rejected");

        // A changed message must not verify.
        Assert.False(Ed25519.Verify(publicKey, new byte[] { 0x01 }, signature),
            "a wrong message was accepted");

        // A changed signature must not verify.
        byte[] broken = (byte[])signature.Clone();
        broken[0] ^= 0x01;
        Assert.False(Ed25519.Verify(publicKey, Array.Empty<byte>(), broken),
            "a broken signature was accepted");

        // The wrong key must not verify.
        byte[] otherKey = Convert.FromBase64String("PUAXw+hDiVqStwqnTRt+vJyYLM8uxJaMwM1V8Sr0Zgw=");
        Assert.False(Ed25519.Verify(otherKey, Array.Empty<byte>(), signature),
            "the wrong key was accepted");

        // RFC 8032, section 7.1, test 2: a one byte message.
        byte[] key2 = Convert.FromBase64String("PUAXw+hDiVqStwqnTRt+vJyYLM8uxJaMwM1V8Sr0Zgw=");
        byte[] sig2 = Convert.FromBase64String(
            "kqAJqfDUyrhyDoILX2QlQKKye1QWUD+Ps3YiI+vbadoIWsHkPhWZbkWPNhPQ8R2MOHsurrQwKu6wDSkWErsMAA==");
        Assert.True(Ed25519.Verify(key2, new byte[] { 0x72 }, sig2),
            "the second RFC 8032 test vector was rejected");

        // The PEM that ships with the app must parse.
        const string pem = "-----BEGIN PUBLIC KEY-----\n" +
                           "MCowBQYDK2VwAyEAR0bqZuQw+019bOdFLy/d73sSs1k7I6s30vXOFNN9F68=\n" +
                           "-----END PUBLIC KEY-----";
        Assert.True(Ed25519.TryGetRawKey(pem, out byte[] raw), "the shipped public key did not parse");
        Assert.Equal(32, raw.Length);
        Assert.Equal("4746EA66E430FB4D7D6CE7452F2FDDEF7B12B3593B23AB37D2F5CE14D37D17AF",
            Convert.ToHexString(raw));

        Assert.False(Ed25519.TryGetRawKey("not a key", out _), "a broken key was accepted");

        // A key whose base64 body has no padding and uses the url safe
        // alphabet must still be read.
        Assert.True(Ed25519.TryGetRawKey(FixturePublicKey, out byte[] fixtureKey),
            "the fixture public key did not parse");
        Assert.Equal("03A107BFF3CE10BE1D70DD18E74BC09967E4D6309BA50D5F1DDC8664125531B8",
            Convert.ToHexString(fixtureKey));

        return Task.CompletedTask;
    }

    private static Task LicenseTokens()
    {
        // base64url without padding.
        Assert.Equal("-_8A", LicenseToken.Base64UrlEncode(new byte[] { 0xFB, 0xFF, 0x00 }));
        Assert.True(LicenseToken.Base64UrlDecode("-_8A").SequenceEqual(new byte[] { 0xFB, 0xFF, 0x00 }),
            "base64url decoding is wrong");

        // A well formed token parses and the claims are read.
        string json = "{\"d\":\"device-1\",\"e\":1767366800000,\"r\":1767366800000,\"k\":\"KEY-1\"}";
        string encodedPayload = LicenseToken.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(json));
        string token = encodedPayload + "." + LicenseToken.Base64UrlEncode(new byte[64]);

        var parsed = LicenseToken.Parse(token);
        Assert.True(parsed is not null, "the token was not parsed");
        Assert.Equal("device-1", parsed!.Claims.DeviceId);
        Assert.Equal(1767366800000L, parsed.Claims.ExpiresAt);
        Assert.Equal("KEY-1", parsed.Claims.Key);

        // The claims are interpreted correctly.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1767366800000);
        Assert.True(parsed.Claims.IsExpired(now.AddMinutes(1)), "the expiry was ignored");
        Assert.False(parsed.Claims.IsExpired(now.AddMinutes(-1)), "the token expired too early");
        Assert.True(parsed.Claims.NeedsRecheck(now.AddMinutes(1)), "the re-check time was ignored");
        Assert.False(parsed.Claims.NeedsRecheck(now.AddMinutes(-1)), "the re-check was too early");

        // A token without an expiry never expires.
        var forever = LicenseToken.Parse(
            LicenseToken.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes("{\"d\":\"x\",\"e\":0}")) +
            "." + LicenseToken.Base64UrlEncode(new byte[64]));
        Assert.False(forever!.Claims.IsExpired(now.AddYears(5)), "a token without expiry expired");

        // Broken tokens are rejected instead of throwing.
        Assert.True(LicenseToken.Parse(null) is null, "a null token was accepted");
        Assert.True(LicenseToken.Parse("") is null, "an empty token was accepted");
        Assert.True(LicenseToken.Parse("no-dot") is null, "a token without a dot was accepted");
        Assert.True(LicenseToken.Parse("a.b") is null, "a token with a short signature was accepted");
        Assert.True(LicenseToken.Parse("!!!.###") is null, "a token with broken base64 was accepted");

        // A token with a made up signature must not verify.
        var forged = LicenseToken.Parse(encodedPayload + "." + LicenseToken.Base64UrlEncode(new byte[64]))!;
        Assert.False(forged.VerifySignature(LicenseConfiguration.PublicKeyPem),
            "a token with a made up signature was accepted");

        // The shipped public key must be usable.
        Assert.True(Ed25519.TryGetRawKey(LicenseConfiguration.PublicKeyPem, out _),
            "the shipped public key did not parse");

        return Task.CompletedTask;
    }

    private static async Task LicenseServiceStates()
    {
        const string testPublicKey = FixturePublicKey;
        const string testToken = FixtureToken;

        using var temp = new TempDirectory();
        var store = new LicenseStore(Path.Combine(temp.Path, "license.json"));

        // No licence yet.
        var service = new LicenseService(store, deviceId: "adm-test-device", publicKeyPem: testPublicKey);
        var status = await service.CheckAsync();
        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.False(status.CanRun, "an unlicensed app must not run");

        // A licence for this device, signed correctly.
        store.Save(LicenseRecord.FromItem("ADM-TEST-KEY", testToken));
        status = await service.CheckAsync();
        Assert.Equal(LicenseState.Active, status.State);
        Assert.True(status.CanRun, "a valid licence must let the app run");
        Assert.Equal("ADM-TEST-KEY", status.Key);
        Assert.True(status.ExpiresAt is not null, "the expiry was not read");

        // The same licence on a different device.
        var otherDevice = new LicenseService(store, deviceId: "another-device", publicKeyPem: testPublicKey);
        status = await otherDevice.CheckAsync();
        Assert.Equal(LicenseState.RevokedDevice, status.State);
        Assert.False(status.CanRun, "a licence for another device must not run");

        // A token that was changed after signing.
        string tampered = testToken.Substring(0, testToken.LastIndexOf('.') + 1) +
            LicenseToken.Base64UrlEncode(new byte[64]);
        store.Save(LicenseRecord.FromItem("ADM-TEST-KEY", tampered));
        status = await service.CheckAsync();
        Assert.True(status.State == LicenseState.Tampered,
            $"a tampered licence gave '{status.State}' instead of Tampered");
        Assert.False(status.CanRun, "a tampered licence must not run");

        // A token signed by a different key.
        store.Save(LicenseRecord.FromItem("ADM-TEST-KEY", testToken));
        var wrongKey = new LicenseService(store, deviceId: "adm-test-device",
            publicKeyPem: LicenseConfiguration.PublicKeyPem);
        status = await wrongKey.CheckAsync();
        Assert.True(status.State == LicenseState.Tampered,
            $"a licence signed by another key gave '{status.State}' instead of Tampered");

        // The licence can be removed again.
        service.Clear();
        status = await service.CheckAsync();
        Assert.Equal(LicenseState.NotActivated, status.State);
    }

    private static Task AppKeyOverride()
    {
        // The compiled key is used when there is no override.
        Assert.Equal(LicenseConfiguration.AppKey, LicenseConfiguration.EffectiveAppKey);
        Assert.True(LicenseConfiguration.EffectiveAppKey.StartsWith("appk_", StringComparison.Ordinal),
            "the compiled app key does not look like an app key");

        // A file next to the executable replaces it, so a build that was
        // packaged with an incomplete key can be repaired without a rebuild.
        string path = AppKeyStore.FilePath;
        bool existed = File.Exists(path);
        string? previous = existed ? File.ReadAllText(path) : null;

        try
        {
            Assert.True(AppKeyStore.TrySave("appk_probe_1234567890abcdef", out string? error),
                $"the app key could not be saved: {error}");

            Assert.Equal("appk_probe_1234567890abcdef", LicenseConfiguration.EffectiveAppKey);
            Assert.Equal("appk_probe_1234567890abcdef", AppKeyStore.Current);

            Assert.True(AppKeyStore.TryClear(out string? clearError), $"the app key could not be cleared: {clearError}");
            Assert.Equal(LicenseConfiguration.AppKey, LicenseConfiguration.EffectiveAppKey);
        }
        finally
        {
            if (previous is not null)
                File.WriteAllText(path, previous);
            else if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static async Task LicenseLiveIntegration()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ADM_LICENSE_INTEGRATION"), "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string licenseKey = Environment.GetEnvironmentVariable("ADM_LICENSE_KEY") ?? string.Empty;
        if (licenseKey.Length == 0)
            throw new InvalidOperationException("Set ADM_LICENSE_KEY to a licence key for the ADM app.");

        var client = new LicenseClient();
        var response = await client.ActivateAsync(licenseKey, DeviceIdentity.DeviceId);
        Assert.True(response.Ok, "activation failed: " + response.Error);

        var token = LicenseToken.Parse(response.Token);
        Assert.True(token is not null, "the token was not parsed");

        // The point of the whole exercise: the token the server issued
        // verifies offline with the public key that ships inside the app.
        Assert.True(token!.VerifySignature(LicenseConfiguration.PublicKeyPem),
            "the live token did not verify with the shipped public key");
        Assert.Equal(DeviceIdentity.DeviceId, token.Claims.DeviceId);
        Assert.False(token.Claims.IsExpired(DateTimeOffset.Now), "the live token was already expired");

        string tampered = token.Payload + "." + LicenseToken.Base64UrlEncode(new byte[64]);
        Assert.False(LicenseToken.Parse(tampered)!.VerifySignature(LicenseConfiguration.PublicKeyPem),
            "a tampered live token was accepted");
    }

    private static Task DeviceIdentityTests()
    {
        string id = DeviceIdentity.DeviceId;

        // The licensing service uses the Windows MachineGuid in the canonical
        // GUID form, so the id has to look like one.
        Assert.True(Guid.TryParse(id, out _), $"the device id '{id}' is not a GUID");
        Assert.Equal(36, id.Length);

        // The id is stable within a run.
        Assert.Equal(id, DeviceIdentity.DeviceId);
        Assert.Equal(id[..12].ToUpperInvariant(), DeviceIdentity.ShortId);

        return Task.CompletedTask;
    }

    private static async Task FtpIntegration()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ADM_FTP_INTEGRATION"), "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var temp = new TempDirectory();

        // The payload that the test server serves.
        byte[] payload = CreatePayload(300_000);
        File.WriteAllBytes(Path.Combine(temp.Path, "sample.bin"), payload);

        // The server is started by the caller; the default points at the
        // throwaway server used during development.
        string? url = Environment.GetEnvironmentVariable("ADM_FTP_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "ftp://adm:secret@127.0.0.1:2121/sample.bin";
        }

        var settings = new AppSettings
        {
            DownloadFolder = temp.Path,
            MaxConcurrentDownloads = 1
        };

        using var manager = new DownloadManager(settings, new DownloadStore(Path.Combine(temp.Path, "downloads.json")));
        var item = manager.Add(url);

        Assert.Equal(DownloadKind.Ftp, item.Kind);

        await WaitUntil(() => item.Status is DownloadStatus.Completed or DownloadStatus.Failed,
            TimeSpan.FromMinutes(2),
            () => $"the FTP download did not finish (status {item.Status}, {item.ErrorMessage ?? "no error"})");

        if (item.Status == DownloadStatus.Failed)
            throw new InvalidOperationException("the FTP download failed: " + item.ErrorMessage);

        Assert.True(File.Exists(item.FilePath), "the downloaded file is missing");
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(item.FilePath)));
    }

    private static Task FtpAddressParsing()
    {
        Assert.True(FtpUrl.IsFtpUrl("ftp://example.com/file.bin"), "an ftp address was not detected");
        Assert.True(FtpUrl.IsFtpUrl("FTPS://example.com/file.bin"), "an ftps address was not detected");
        Assert.False(FtpUrl.IsFtpUrl("https://example.com/file.bin"), "an http address was taken for ftp");

        Assert.True(FtpUrl.TryParse("ftp://example.com/pub/file.bin", out var plain), "the address was rejected");
        Assert.Equal("example.com", plain.UrlHost);
        Assert.Equal(21, plain.UrlPort);
        Assert.Equal("/pub/file.bin", plain.Path);
        Assert.False(plain.IsSecure, "plain ftp must not be secure");

        Assert.True(FtpUrl.TryParse("ftps://user:pass@example.com:2121/a b.bin", out var secure),
            "the secure address was rejected");
        Assert.Equal("example.com", secure.UrlHost);
        Assert.Equal(2121, secure.UrlPort);
        Assert.Equal("user", secure.UserName);
        Assert.Equal("pass", secure.Password);
        Assert.Equal("/a b.bin", secure.Path);
        Assert.True(secure.IsSecure, "ftps must be secure");

        Assert.False(FtpUrl.TryParse("https://example.com/file.bin", out _), "an http address was accepted");

        return Task.CompletedTask;
    }

    private static DownloadManager CreateManager(string folder, int maxConnections, int maxConcurrent)
    {
        var settings = new AppSettings
        {
            DownloadFolder = folder,
            MaxConnections = maxConnections,
            MaxConcurrentDownloads = maxConcurrent
        };

        return new DownloadManager(settings, new DownloadStore(Path.Combine(folder, "downloads.json")));
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        var random = new Random(42);
        random.NextBytes(payload);
        return payload;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, Func<string> describeFailure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(40);
        }

        throw new TimeoutException(describeFailure());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // Best effort cleanup.
            }
        }
    }

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }

        public static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }
    }
}
