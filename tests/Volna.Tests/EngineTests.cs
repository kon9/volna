using Volna.Core;
using Xunit;

namespace Volna.Tests;

public class EngineTests
{
    [Fact]
    public void Lists_of_songs_are_cleaned_of_their_numbering()
    {
        var entries = Engine.ParseEntries("1. Ivy Lane - Halogen\n2) Mira Sol - Paper Planets\n- Foo - &Bar\n• Baz - Qux\n#5 Foo Bar\nHeat Waves\n\n   \n");
        Assert.Equal(new[] { "Ivy Lane - Halogen", "Mira Sol - Paper Planets", "Foo - &Bar", "Baz - Qux", "Foo Bar", "Heat Waves" }, entries.Select(e => e[Engine.SearchPrefix.Length..]));
    }

    [Fact]
    public void A_year_at_the_start_is_not_numbering() => Assert.Equal(Engine.SearchPrefix + "2024 - Song", Engine.ParseEntries("2024 - Song")[0]);

    [Fact]
    public void Links_and_names_can_be_mixed_and_duplicates_collapse()
    {
        var entries = Engine.ParseEntries("https://youtu.be/abc\nA - B\nhttps://youtu.be/abc\nA - B");
        Assert.Equal(new[] { "https://youtu.be/abc", Engine.SearchPrefix + "A - B" }, entries);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", true)]
    [InlineData("https://youtu.be/abc", true)]
    [InlineData("https://www.youtube.com/playlist?list=abc", true)]
    [InlineData("https://www.youtube.com/", false)]
    [InlineData("https://www.youtube.com/feed/history", false)]
    [InlineData("https://www.youtube.com/@SomeChannel", false)] // a stray copy must not queue a whole channel
    [InlineData("https://vk.com/id123", false)]
    [InlineData("https://example.com/video", false)]
    [InlineData("https://notyoutube.com/watch?v=abc", false)]
    [InlineData("https://www.tiktok.com/@someone/video/123", true)]
    [InlineData("https://www.tiktok.com/@someone", false)]
    [InlineData("https://x.com/someone/status/123", true)]
    [InlineData("https://x.com/someone", false)]
    [InlineData("https://www.instagram.com/reel/abc/", true)]
    [InlineData("https://www.instagram.com/someone/", false)]
    [InlineData("https://soundcloud.com/artist/track", true)]
    [InlineData("https://soundcloud.com/artist", false)]
    [InlineData("https://vimeo.com/123456", true)]
    [InlineData("https://vimeo.com/channels", false)]
    [InlineData("https://www.reddit.com/r/videos/comments/abc/title/", true)]
    [InlineData("https://www.reddit.com/r/videos/", false)]
    [InlineData("https://artist.bandcamp.com/track/song", true)]
    [InlineData("https://artist.bandcamp.com/", false)]
    [InlineData("https://www.twitch.tv/videos/123", true)]
    [InlineData("https://www.twitch.tv/someone", false)]
    public void Only_real_video_links_are_caught_from_the_clipboard(string url, bool expected) => Assert.Equal(expected, Engine.LooksLikeMedia(url));

    static Dictionary<string, object?> Song(string title, string artist, string? album = null) => Json.Map(("title", title), ("uploader", artist), ("album", album));

    [Fact]
    public void The_matching_song_is_chosen()
    {
        var results = new[] { Song("Glass Tide (feat. Ivy Lane and Mira Sol)", "Nova Harbor") };
        Assert.Same(results[0], MusicSearch.BestMatch("Nova Harbor - Glass Tide", results));
    }

    [Theory] // Other sites ask for an account in their own words
    [InlineData("[vimeo] 1: The web client only works when logged-in. Use --cookies, --cookies-from-browser, --username and --password")]
    [InlineData("[instagram] abc: Requested content is not available, rate-limit reached or login required. Use --cookies")]
    [InlineData("[somesite] 1: This video is only available for registered users. Use --username and --password")]
    public void A_site_asking_for_an_account_says_sign_in(string error)
    {
        Assert.True(Engine.NeedsLogin(error));
        Assert.Equal(Loc.T("error.private"), Engine.FriendlyError(error, false));
    }

    [Fact]
    public void A_video_kept_as_mkv_counts_as_downloaded()
    {
        Assert.Equal("a [x].mkv", Engine.MatchExisting(new[] { "a [x].mkv" }, false, "mp3", "mp4"));
        Assert.Null(Engine.MatchExisting(new[] { "a [x].mkv" }, true, "mp3", "mp4"));
    }

    [Theory]
    [InlineData("en", "en", "en.*,-.*-orig")]
    [InlineData("ru", "ru", "ru.*,en.*,-.*-orig")]
    [InlineData("ja", "en", "ja.*,en.*,-.*-orig")]     // a system language the interface has no table for still gets its subtitles
    [InlineData("de", "fr", "de.*,fr.*,en.*,-.*-orig")]
    [InlineData("iv", "en", "iv.*,en.*,-.*-orig")]
    public void Subtitles_follow_the_system_language(string system, string shown, string expected) => Assert.Equal(expected, Engine.SubtitleLanguages(system, shown));

    [Fact]
    public void Views_in_East_Asian_languages_count_in_ten_thousands()
    {
        Loc.Use("ja");
        try
        {
            Assert.Equal("1.2万回視聴", Format.Views(12_345));
            Assert.Equal("3.4億回視聴", Format.Views(340_000_000));
            Assert.Equal("950回視聴", Format.Views(950));
        }
        finally { Loc.Use("en"); }
    }

    [Theory]
    [InlineData("en", 1, "views.one")]
    [InlineData("en", 21, "views.many")]
    [InlineData("ru", 21, "views.one")]
    [InlineData("ru", 3, "views.few")]
    [InlineData("ru", 12, "views.many")]
    [InlineData("pl", 21, "views.many")]
    [InlineData("pl", 22, "views.few")]
    [InlineData("ja", 1, "views.one")]
    public void Counts_take_the_form_their_language_wants(string language, long n, string expected) => Assert.Equal(expected, Format.PluralKey("views", n, language));

    // ---------- Moving a library ----------

    [Fact]
    public void An_export_from_Exportify_is_read()
    {
        const string csv = "\uFEFFTrack URI,Track Name,Artist Name(s),Album Name,Duration (ms)\n"
            + "spotify:track:1,\"Glass, Tide\",Nova Harbor,Slow Lanterns,247000\n"
            + "spotify:track:2,Midnight Bus,The Paper Kites Club;Ivy Lane,Station Songs,198000\n";
        var tracks = LibraryImport.Parse(csv, "Liked");
        Assert.Equal(2, tracks.Count);
        Assert.Equal(Engine.SearchPrefix + "Nova Harbor - Glass, Tide", tracks[0].Entry);
        Assert.Equal(247, tracks[0].Seconds);
        Assert.Equal("Liked", tracks[0].List);
        Assert.Equal("The Paper Kites Club, Ivy Lane", tracks[1].Artist);
    }

    [Theory]
    [InlineData("Track name;Artist name;Album;Playlist name\nGlass Tide;Nova Harbor;Slow Lanterns;Road\n", "Road")]
    [InlineData("title,artist,album,isrc\nGlass Tide,Nova Harbor,Slow Lanterns,XX\n", "file")]
    [InlineData("Song Title\tArtist Name 1\tAlbum Title\nGlass Tide\tNova Harbor\tSlow Lanterns\n", "file")]
    public void Exports_from_other_tools_are_read(string csv, string list)
    {
        var track = Assert.Single(LibraryImport.Parse(csv, "file"));
        Assert.Equal("Nova Harbor", track.Artist);
        Assert.Equal("Glass Tide", track.Title);
        Assert.Equal(list, track.List);
    }

    [Fact] // The real header of Exportify's CSV, and how it writes a comma in an artist's name
    public void The_length_and_ISRC_in_Exportify_are_read()
    {
        const string csv = "Track URI,Track Name,Artist URI(s),Artist Name(s),Album URI,Album Name,Album Artist URI(s),Album Artist Name(s),Album Release Date,Album Image URL,Disc Number,Track Number,Track Duration (ms),Track Preview URL,Explicit,Popularity,ISRC\n"
            + "spotify:track:1,Glass Tide,spotify:artist:1,\"Nova\\, The Harbor, Ivy Lane\",spotify:album:1,Slow Lanterns,x,x,2020,x,1,1,247000,,false,5,XX1234567890\n";
        var track = Assert.Single(LibraryImport.Parse(csv));
        Assert.Equal(247, track.Seconds);
        Assert.Equal("XX1234567890", track.Isrc);
        Assert.Equal("Nova, The Harbor, Ivy Lane", track.Artist);
    }

    [Fact] // Exportify translates its headers into the browser's language
    public void Exportify_in_another_language_is_read()
    {
        const string csv = "URI du titre,Nom du titre,URI(s) de l'artiste,Nom(s) de l'artiste,URI de l'album,Nom de l'album,a,b,c,d,e,f,Durée du titre (ms),g,h,i,ISRC\n"
            + "spotify:track:1,Glass Tide,x,Nova Harbor,x,Slow Lanterns,x,x,2020,x,1,1,247000,,false,5,XX1234567890\n";
        var track = Assert.Single(LibraryImport.Parse(csv));
        Assert.Equal("Glass Tide", track.Title);
        Assert.Equal("Nova Harbor", track.Artist);
        Assert.Equal("Slow Lanterns", track.Album);
        Assert.Equal(247, track.Seconds);
    }

    [Fact]
    public void A_length_with_a_unit_is_read() =>
        Assert.Equal(157, Assert.Single(LibraryImport.Parse("title,artist,album,isrc,duration\nGlass Tide,Nova Harbor,x,y,157s\n")).Seconds);

    [Fact]
    public void A_zip_and_a_folder_of_exports_are_read()
    {
        string folder = Path.Combine(Path.GetTempPath(), "volna-import-" + Guid.NewGuid().ToString("N"));
        string inner = Path.Combine(folder, "lists");
        Directory.CreateDirectory(inner);
        try
        {
            File.WriteAllText(Path.Combine(inner, "Road.csv"), "Track Name,Artist Name\nGlass Tide,Nova Harbor\n");
            File.WriteAllText(Path.Combine(inner, "notes.pdf"), "Track Name,Artist Name\nNot,This\n");
            Assert.Equal("Road", Assert.Single(LibraryImport.ReadFile(folder)).List);
            string zip = folder + ".zip";
            System.IO.Compression.ZipFile.CreateFromDirectory(folder, zip);
            try { Assert.Equal("Road", Assert.Single(LibraryImport.ReadFile(zip)).List); }
            finally { File.Delete(zip); }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_table_with_Russian_column_names_is_read()
    {
        var track = Assert.Single(LibraryImport.Parse("Исполнитель;Название;Альбом;Длительность\nNova Harbor;Glass Tide;Slow Lanterns;4:07\n", "Моё"));
        Assert.Equal("Nova Harbor", track.Artist);
        Assert.Equal("Glass Tide", track.Title);
        Assert.Equal(247, track.Seconds);
    }

    [Fact]
    public void A_table_without_column_names_is_offered_for_checking()
    {
        Assert.Empty(LibraryImport.Parse("Nova Harbor,Glass Tide\nIvy Lane,Halogen\n"));
        var table = LibraryImport.GuessTable("Nova Harbor,Glass Tide\nIvy Lane,Halogen\nCobalt Orchard,Honey Radio\n");
        Assert.NotNull(table);
        Assert.Equal(0, table.ArtistColumn);
        Assert.Equal(1, table.TitleColumn);
        Assert.Equal(Engine.SearchPrefix + "Ivy Lane - Halogen", table.Tracks()[1].Entry);
    }

    [Fact] // Artists repeat in a library and titles rarely do, whichever comes first
    public void The_column_that_repeats_is_taken_for_the_artist()
    {
        var table = LibraryImport.GuessTable("Glass Tide;Nova Harbor;4:07\nHalogen;Nova Harbor;3:20\nHoney Radio;Nova Harbor;2:11\nPaper Planets;Ivy Lane;5:00\n");
        Assert.NotNull(table);
        Assert.Equal(1, table.ArtistColumn);
        Assert.Equal(0, table.TitleColumn);
        Assert.Equal(2, table.DurationColumn);
        Assert.False(table.FirstRowIsHeader);
        Assert.Equal(247, table.Tracks()[0].Seconds);
    }

    [Fact]
    public void Unknown_column_names_are_recognized_by_the_times_under_them()
    {
        var table = LibraryImport.GuessTable("Who,What,How long\nNova Harbor,Glass Tide,4:07\nIvy Lane,Halogen,3:20\n");
        Assert.NotNull(table);
        Assert.True(table.FirstRowIsHeader);
        Assert.Equal(2, table.Tracks().Count);
    }

    [Theory]
    [InlineData("Nova Harbor - Glass Tide\nIvy Lane - Halogen")] // an ordinary list
    [InlineData("Nova Harbor, Glass Tide")] // a single line
    [InlineData("One, two\nThree\nFour\nFive\nSix")] // a comma here and there
    [InlineData("https://a.example/1,x\nhttps://a.example/2,y")] // links
    [InlineData("{\"a\":1}")]
    public void What_is_not_a_table_stays_as_it_is(string text) => Assert.Null(LibraryImport.GuessTable(text));

    [Theory]
    [InlineData("https://open.spotify.com/playlist/abc", true)]
    [InlineData("https://music.apple.com/us/album/x/1", true)]
    [InlineData("https://vk.com/music/playlist/1_2", true)]
    [InlineData("https://vk.com/video-1_2", false)]
    [InlineData("https://www.youtube.com/watch?v=abc", false)]
    public void Services_that_give_no_files_are_recognized(string url, bool closed) => Assert.Equal(closed, Engine.IsClosedMusicService(url));

    [Fact]
    public void A_comma_inside_an_artist_stays_one_artist() =>
        Assert.Equal("Nova, The Harbor", LibraryImport.Parse("Track Name,Artist Name\nGlass Tide,\"Nova, The Harbor\"\n")[0].Artist);

    [Fact]
    public void Google_takeout_gives_links() =>
        Assert.Equal("https://music.youtube.com/watch?v=abc", Assert.Single(LibraryImport.Parse("Video ID,Time Added\nabc,2024\n")).Entry);

    [Theory]
    [InlineData("Nova Harbor - Glass Tide\nIvy Lane - Halogen")]
    [InlineData("Nova Harbor, Glass Tide")]
    [InlineData("https://example.com/watch,123\nsecond,line")]
    [InlineData("{not json")]
    [InlineData("<not xml")]
    public void An_ordinary_list_is_not_taken_for_an_export(string text) => Assert.Empty(LibraryImport.Parse(text));

    [Fact]
    public void Spotify_account_data_is_read()
    {
        const string library = """{"tracks":[{"artist":"Nova Harbor","album":"Slow Lanterns","track":"Glass Tide","uri":"x"}],"albums":[]}""";
        Assert.Equal("Nova Harbor", Assert.Single(LibraryImport.Parse(library, "YourLibrary")).Artist);
        const string lists = """{"playlists":[{"name":"Road","items":[{"track":{"trackName":"Glass Tide","artistName":"Nova Harbor","albumName":"Slow Lanterns"}}]}]}""";
        var track = Assert.Single(LibraryImport.Parse(lists, "Playlist1"));
        Assert.Equal("Glass Tide", track.Title);
        Assert.Equal("Road", track.List);
    }

    [Fact]
    public void Plays_and_hidden_songs_are_not_the_library()
    {
        Assert.Empty(LibraryImport.Parse("""[{"endTime":"2024","artistName":"Nova Harbor","trackName":"Glass Tide","msPlayed":1000}]"""));
        Assert.Empty(LibraryImport.Parse("""{"bannedTracks":[{"artist":"Nova Harbor","album":"x","track":"Glass Tide"}]}"""));
    }

    [Fact]
    public void An_Apple_Music_library_is_read()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>Tracks</key><dict>
              <key>1</key><dict><key>Name</key><string>Glass Tide</string><key>Artist</key><string>Nova Harbor</string><key>Album</key><string>Slow Lanterns</string><key>Total Time</key><integer>247000</integer></dict>
            </dict></dict></plist>
            """;
        var track = Assert.Single(LibraryImport.Parse(xml, "Library"));
        Assert.Equal("Glass Tide", track.Title);
        Assert.Equal(247, track.Seconds);
    }

    [Fact]
    public void An_Apple_Music_library_keeps_its_playlists_and_only_music()
    {
        const string xml = """
            <plist version="1.0"><dict><key>Tracks</key><dict>
              <key>1</key><dict><key>Track ID</key><integer>1</integer><key>Name</key><string>Glass Tide</string><key>Artist</key><string>Nova Harbor</string></dict>
              <key>2</key><dict><key>Track ID</key><integer>2</integer><key>Name</key><string>Some Show</string><key>Podcast</key><true/></dict>
            </dict><key>Playlists</key><array>
              <dict><key>Name</key><string>Library</string><key>Master</key><true/><key>Playlist Items</key><array><dict><key>Track ID</key><integer>1</integer></dict></array></dict>
              <dict><key>Name</key><string>Road</string><key>Playlist Items</key><array><dict><key>Track ID</key><integer>1</integer></dict><dict><key>Track ID</key><integer>2</integer></dict></array></dict>
            </array></dict></plist>
            """;
        var tracks = LibraryImport.Parse(xml, "Export");
        Assert.All(tracks, t => Assert.Equal("Glass Tide", t.Title));
        Assert.Equal(new[] { "Export", "Road" }, tracks.Select(t => t.List));
    }

    [Fact]
    public void A_playlist_file_is_read()
    {
        const string m3u = "#EXTM3U\n#EXTINF:247,Nova Harbor - Glass Tide\nC:\\Music\\a.mp3\n/home/x/Ivy Lane - Halogen.flac\n#EXTINF:-1,Stream\nhttps://example.com/v\n";
        var tracks = LibraryImport.Parse(m3u, "Road");
        Assert.Equal(3, tracks.Count);
        Assert.Equal(247, tracks[0].Seconds);
        Assert.Equal("Ivy Lane", tracks[1].Artist);
        Assert.Equal("https://example.com/v", tracks[2].Entry);
    }

    // ---------- Small parts ----------

    [Fact]
    public void The_original_soundtrack_outranks_a_dub() =>
        Assert.StartsWith("lang,", Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x", Audio = false }, Array.Empty<string>()).SkipWhile(a => a != "--format-sort").ElementAt(1));

    [Fact]
    public void A_part_of_a_video_has_its_own_name()
    {
        var args = Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x", Section = "90-120" }, Array.Empty<string>());
        Assert.Equal("*90-120", args[args.IndexOf("--download-sections") + 1]);
        Assert.Contains("[%(id)s clip 90-120]", args[args.IndexOf("--output") + 1]);
    }

    [Theory]
    [InlineData("90", 90d)]
    [InlineData("1:30", 90d)]
    [InlineData("1:02:03", 3723d)]
    [InlineData("soon", null)]
    [InlineData("1:2:3:4", null)]
    public void Times_are_understood(string text, double? seconds) => Assert.Equal(seconds, Engine.ParseTime(text));

    [Theory]
    [InlineData("HTTP Error 403: Forbidden", true)]
    [InlineData("nsig extraction failed", true)]
    [InlineData("Unable to extract uploader id", true)]
    [InlineData("Video unavailable", false)]
    [InlineData("HTTP Error 429", false)]
    public void Failures_a_newer_tool_may_fix_are_recognized(string error, bool outdated) => Assert.Equal(outdated, Engine.LooksOutdated(error));

    [Fact]
    public void Timed_lyrics_need_the_same_length()
    {
        const string reply = """[{"duration":300,"syncedLyrics":"[00:01] wrong length","plainLyrics":"plain"},{"duration":201,"syncedLyrics":"[00:01] right","plainLyrics":"p"}]""";
        Assert.Equal("[00:01] right", Lyrics.Pick(reply, 200));
        Assert.Equal("plain", Lyrics.Pick(reply, 90));
        Assert.Null(Lyrics.Pick("[]", 90));
    }

    [Fact] // Most videos only have automatic captions, and asking for subtitles alone leaves those out
    public void Subtitles_include_automatic_ones() =>
        Assert.Contains("--write-auto-subs", Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x", Subtitles = true, Audio = false }, Array.Empty<string>()));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PLxyz", "https://www.youtube.com/playlist?list=PLxyz")]
    [InlineData("https://www.youtube.com/watch?list=PLxyz&v=abc&index=2", "https://www.youtube.com/playlist?list=PLxyz")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=RDabc", null)]
    [InlineData("https://www.youtube.com/watch?v=abc", null)]
    [InlineData("https://vimeo.com/watch?list=x", null)]
    public void The_playlist_behind_a_link_is_recognized(string url, string? playlist) => Assert.Equal(playlist, Engine.PlaylistOf(url));

    [Theory]
    [InlineData("1.5 Degrees", "1.5 Degrees")]
    [InlineData("1. Glass Tide", "Glass Tide")]
    [InlineData("2) Glass Tide", "Glass Tide")]
    [InlineData("3.Glass Tide", "Glass Tide")]
    [InlineData("雨", "雨")]
    [InlineData("X", "X")]
    [InlineData("🔥", "🔥")]
    [InlineData("Glass\u200B Tide", "Glass Tide")]
    public void Odd_lines_are_understood(string line, string query) => Assert.Equal(Engine.SearchPrefix + query, Assert.Single(Engine.ParseEntries(line)));

    [Theory]
    [InlineData("---")]
    [InlineData("...")]
    [InlineData("   ")]
    public void Lines_with_nothing_in_them_are_left_out(string line) => Assert.Empty(Engine.ParseEntries(line));

    [Fact]
    public void An_address_without_https_is_still_a_link() =>
        Assert.Equal("https://youtu.be/abc", Assert.Single(Engine.ParseEntries("youtu.be/abc")));

    [Fact]
    public void A_broken_address_in_a_playlist_does_not_stop_the_rest()
    {
        var tracks = LibraryImport.Parse("#EXTM3U\nhttp://[bad\nfile:///C:/Music/Nova%20Harbor%20-%20Glass%20Tide.mp3\n");
        Assert.Equal("Nova Harbor", Assert.Single(tracks).Artist);
    }

    [Fact]
    public void Files_in_old_encodings_are_read()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Assert.Equal("Кино - Звезда", LibraryImport.ReadText(System.Text.Encoding.GetEncoding(1251).GetBytes("Кино - Звезда")));
        Assert.Equal("夜の電車", LibraryImport.ReadText(System.Text.Encoding.GetEncoding(932).GetBytes("夜の電車")));
        Assert.Equal("Кино", LibraryImport.ReadText(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("Кино")).ToArray()));
        Assert.Equal("Кино", LibraryImport.ReadText(System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("Кино")).ToArray()));
    }

    [Fact]
    public void An_account_export_brings_playlists_and_not_everything_commented_on()
    {
        string zip = Path.Combine(Path.GetTempPath(), "volna-takeout-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, string text)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
                Add("Takeout/YouTube/comments/comments.csv", "Video ID,Comment Text\nmine,hello\n");
                Add("Takeout/YouTube/playlists/Road.csv", "Video ID,Time Added\nsong,2024\n");
            }
            var track = Assert.Single(LibraryImport.ReadFile(zip));
            Assert.Equal("https://music.youtube.com/watch?v=song", track.Entry);
            Assert.Equal("Road", track.List);
        }
        finally { File.Delete(zip); }
    }

    [Theory]
    [InlineData("Artist - Song [Live 1977].mp3", "artist song live 1977")]
    [InlineData("Artist - Song [abc123].mp3", "artist song")]
    [InlineData("Song [abc clip 1-2].mp3", "song abc clip 1 2")]
    public void A_name_in_brackets_is_not_taken_for_an_id(string file, string key) => Assert.Equal(key, Engine.FileNameKey(file));

    [Fact]
    public void A_reissue_in_an_export_is_the_same_song()
    {
        var song = FakeBackend.Track("a", "Glass Tide", "Nova Harbor");
        Assert.True(MusicSearch.Matches("Nova Harbor - Glass Tide - 2011 Remaster", song));
        Assert.True(MusicSearch.Matches("Nova Harbor - Glass Tide (Remastered)", song));
        Assert.False(MusicSearch.Matches("Nova Harbor - Honey Radio - 2011 Remaster", song));
    }

    [Fact]
    public void A_reserved_name_is_not_used_for_a_file() => Assert.Equal("NUL playlist", Engine.SafeFileName("NUL"));

    [Theory]
    [InlineData("ERROR: could not find whale cookies database in \"x\"", true)]
    [InlineData("ERROR: Could not copy Chrome cookie database", true)]
    [InlineData("ERROR: Failed to decrypt with DPAPI", true)]
    [InlineData("ERROR: Video unavailable", false)]
    public void An_unreadable_browser_is_recognized(string error, bool unreadable) => Assert.Equal(unreadable, Engine.CookiesUnreadable(error));

    [Theory]
    [InlineData("[Errno 28] No space left on device", true)]
    [InlineData("[WinError 112] На диске недостаточно места", true)]
    [InlineData("HTTP Error 404", false)]
    public void A_full_disk_is_recognized(string error, bool full) => Assert.Equal(full, Engine.DiskFull(error));

    [Fact]
    public void A_block_by_country_does_not_ask_for_an_update() =>
        Assert.False(Engine.LooksOutdated("ERROR: HTTP Error 403: not available in your country"));

    [Fact]
    public void A_proxy_login_goes_separately()
    {
        var proxy = Web.Proxy("http://user:p%40ss@host.example:8080");
        Assert.Equal("host.example", proxy.Address!.Host);
        Assert.Equal("p@ss", ((System.Net.NetworkCredential)proxy.Credentials!).Password);
    }

    [Fact]
    public void Clean_names_leave_the_id_out()
    {
        var args = Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x", CleanNames = true, Original = true }, Array.Empty<string>());
        Assert.DoesNotContain("[%(id)s]", args[args.IndexOf("--output") + 1]);
        args = Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x" }, Array.Empty<string>());
        Assert.EndsWith(" [%(id)s].%(ext)s", args[args.IndexOf("--output") + 1]);
    }

    [Theory]
    [InlineData("v1.2.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("v0.9.0", "1.0.0", false)]
    [InlineData("nightly", "1.0.0", false)]
    public void A_newer_release_is_recognized(string tag, string current, bool newer) => Assert.Equal(newer, AppUpdate.IsNewer(tag, current));

    [Fact]
    public void Checksums_are_read_from_a_release_list()
    {
        string hash = new string('a', 64), other = new string('b', 64);
        Assert.Equal(hash, ToolManager.HashFor(other + "  yt-dlp_linux\n" + hash + "  yt-dlp.exe\n", "yt-dlp.exe"));
        Assert.Equal(hash, ToolManager.HashFor(hash + "  whatever\n", "deno.zip"));
        Assert.Equal(hash, ToolManager.HashFor("\nAlgorithm : SHA256\nHash      : " + hash + "\nPath      : D:\\a\\deno.zip\n", "deno.zip"));
        Assert.Null(ToolManager.HashFor("no hash here  yt-dlp.exe\n", "yt-dlp.exe"));
    }

    [Fact]
    public void A_file_name_is_safe_on_every_system() => Assert.Equal("a b  c", Engine.SafeFileName("a/b:?c.."));

    [Fact]
    public void A_recording_of_the_right_length_is_preferred()
    {
        var short_ = FakeBackend.Track("a", "Glass Tide", "Nova Harbor");
        short_["duration"] = 120d;
        var right = FakeBackend.Track("b", "Glass Tide", "Nova Harbor");
        right["duration"] = 247d;
        Assert.Equal("b", MusicSearch.BestMatch("Nova Harbor - Glass Tide", new[] { short_, right }, 247)!.Str("id"));
        Assert.Equal("a", MusicSearch.BestMatch("Nova Harbor - Glass Tide", new[] { short_, right })!.Str("id"));
    }

    [Fact] // the right artist alone must not pass
    public void Another_song_by_the_same_artist_is_not_a_match() =>
        Assert.Null(MusicSearch.BestMatch("Nova Harbor - Glass Tide", new[] { Song("Midnight Bus", "Nova Harbor", "Station Songs") }));

    [Fact]
    public void A_song_with_the_same_title_by_someone_else_is_not_a_match() =>
        Assert.Null(MusicSearch.BestMatch("Cobalt Orchard - Paper Planets", new[] { Song("Paper Planets", "Mira Sol") }));

    [Fact]
    public void A_typo_in_the_artist_still_matches() =>
        Assert.NotNull(MusicSearch.BestMatch("Novva Harbor - Glass Tide", new[] { Song("Glass Tide", "Nova Harbor") }));

    [Fact]
    public void A_title_in_another_script_is_decided_by_the_artist()
    {
        Assert.NotNull(MusicSearch.BestMatch("霞ドライブ — Night Train", new[] { Song("夜の電車", "霞ドライブ") }));
        Assert.Null(MusicSearch.BestMatch("Someone Else — Night Train", new[] { Song("夜の電車", "霞ドライブ") }));
    }

    [Fact]
    public void Other_versions_are_skipped_unless_asked_for()
    {
        var results = new[] { Song("Northern Static (Remix)", "Ivy Lane"), Song("Northern Static", "Ivy Lane") };
        Assert.Same(results[1], MusicSearch.BestMatch("Ivy Lane - Northern Static", results));
        Assert.Same(results[0], MusicSearch.BestMatch("Ivy Lane - Northern Static remix", results));
    }

    [Fact] // whole words only
    public void Words_merely_containing_a_version_name_do_not_count()
    {
        var alive = new[] { Song("Alive", "Kasumi Drive") };
        Assert.Same(alive[0], MusicSearch.BestMatch("Kasumi Drive - Alive", alive));
        var live = new[] { Song("Alive (Live)", "Kasumi Drive"), Song("Alive", "Kasumi Drive") };
        Assert.Same(live[1], MusicSearch.BestMatch("Kasumi Drive - Alive", live));
    }

    [Theory]
    [InlineData("Cobalt Orchard - Honey Radio (Official Video)", "Cobalt Orchard - Honey Radio")]
    [InlineData("Ivy Lane - Halogen [Official Music Video]", "Ivy Lane - Halogen")]
    [InlineData("Cobalt Orchard - Honey Radio (Official Video Remastered)", "Cobalt Orchard - Honey Radio")]
    public void Marked_clips_give_a_query(string title, string expected) => Assert.Equal(expected, MusicSearch.QueryForClip(Json.Map(("title", title))));

    [Theory]
    [InlineData("Best music video ever")]
    [InlineData("Dreamville - Sunny Day")]
    [InlineData("My holiday in Italy")]
    public void Other_titles_are_left_alone(string title) => Assert.Null(MusicSearch.QueryForClip(Json.Map(("title", title))));

    [Fact]
    public void YouTubes_own_music_info_wins() =>
        Assert.Equal("Cobalt Orchard - Honey Radio", MusicSearch.QueryForClip(Json.Map(("title", "whatever"), ("track", "Honey Radio"), ("artist", "Cobalt Orchard"))));

    [Fact] // The reply format of YouTube Music, including the words between artists
    public void A_music_reply_is_understood()
    {
        string reply = """
        {"contents":[{"musicResponsiveListItemRenderer":{"flexColumns":[
          {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Glass Tide","navigationEndpoint":{"watchEndpoint":{"videoId":"demoTrackId1","watchEndpointMusicSupportedConfigs":{"watchEndpointMusicConfig":{"musicVideoType":"MUSIC_VIDEO_TYPE_ATV"}}}}}]}}},
          {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[
            {"text":"Nova Harbor","navigationEndpoint":{"browseEndpoint":{}}},{"text":", "},{"text":"Ivy Lane","navigationEndpoint":{"browseEndpoint":{}}},{"text":" и "},{"text":"Mira Sol","navigationEndpoint":{"browseEndpoint":{}}},
            {"text":" • "},{"text":"Slow Lanterns","navigationEndpoint":{"browseEndpoint":{"browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}},
            {"text":" • "},{"text":"6:10"}]}}}]}}]}
        """;
        var song = Assert.Single(MusicSearch.ParseResults(reply, 5));
        Assert.Equal("demoTrackId1", song.Str("id"));
        Assert.Equal("Nova Harbor, Ivy Lane, Mira Sol", song.Str("uploader"));
        Assert.Equal("Slow Lanterns", song.Str("album"));
        Assert.Equal(370, song.Num("duration"));
        Assert.True(song.Flag("is_original"));
    }

    [Fact]
    public void Download_arguments_end_with_the_link_after_a_separator()
    {
        var args = Engine.DownloadArgs(new DownloadOptions { Url = "--evil", Folder = "x" }, Array.Empty<string>());
        Assert.Equal("--", args[^2]);
        Assert.Equal("--evil", args[^1]);
    }

    [Fact]
    public void A_height_limit_accepts_formats_of_unknown_height()
    {
        var args = Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x", Height = 1080 }, Array.Empty<string>());
        Assert.Contains("bv*[height<=?1080]+ba/b[height<=?1080]", args);
    }

    [Fact] // one video, even for a playlist link
    public void A_download_takes_one_video() => Assert.Contains("--playlist-items", Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x" }, Array.Empty<string>()));

    [Fact]
    public void The_ordinary_output_stays_on() => Assert.Contains("--no-quiet", Engine.DownloadArgs(new DownloadOptions { Url = "u", Folder = "x" }, Array.Empty<string>()));

    [Theory]
    [InlineData("MSEdgeHTM", "edge")]
    [InlineData("FirefoxURL-308046B0AF4A39CB", "firefox")]
    [InlineData("ChromeHTML", "chrome")]
    [InlineData("com.apple.safari", "safari")]
    [InlineData("org.mozilla.firefox", "firefox")]
    [InlineData("firefox_firefox.desktop", "firefox")]
    [InlineData("google-chrome.desktop", "chrome")]
    [InlineData("com.brave.Browser", "brave")]
    [InlineData("org.gnome.Epiphany.desktop", null)]
    public void Default_browsers_are_recognized(string handler, string? expected) => Assert.Equal(expected, Browsers.FromHandler(handler));

    [Fact]
    public void Only_youtube_cookies_are_kept()
    {
        Assert.NotNull(SeparateSignIn.ToLine(Json.Map(("domain", ".youtube.com"), ("name", "LOGIN_INFO"), ("value", "v"), ("path", "/"), ("secure", true), ("httpOnly", true), ("expires", 1800000000.0))));
        Assert.Null(SeparateSignIn.ToLine(Json.Map(("domain", ".google.com"), ("name", "SID"), ("value", "v"))));
        Assert.Null(SeparateSignIn.ToLine(Json.Map(("domain", ".notyoutube.com"), ("name", "x"), ("value", "v"))));
    }

    [Fact]
    public void A_cookie_line_has_the_cookies_txt_shape()
    {
        string line = SeparateSignIn.ToLine(Json.Map(("domain", ".youtube.com"), ("name", "LOGIN_INFO"), ("value", "abc"), ("path", "/"), ("secure", true), ("httpOnly", true), ("expires", 1800000000.0)))!;
        Assert.Equal("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1800000000\tLOGIN_INFO\tabc", line);
        Assert.True(Auth.IsLoginLine(line));
    }

    [Fact]
    public void Automatic_captions_lose_their_rolling_repeats()
    {
        string vtt = "WEBVTT\nKind: captions\nLanguage: en\n\n00:00:01.000 --> 00:00:03.000\nhello <00:00:01.500><c>there</c>\n\n00:00:03.000 --> 00:00:05.000\nhello there\nand welcome\n";
        Assert.Equal("hello there\nand welcome", Subtitles.VttToText(vtt, false));
        Assert.StartsWith("[00:00:01] hello there", Subtitles.VttToText(vtt, true));
    }

    [Fact]
    public void Json_round_trips_the_shapes_yt_dlp_produces()
    {
        var parsed = Json.ParseObject("""{"a":1,"b":"две","c":[1,{"d":true}],"e":null,"f":1.5}""")!;
        Assert.Equal(parsed["b"], Json.ParseObject(Json.Write(parsed))!["b"]);
        Assert.Equal(1.5, parsed.Num("f"));
        Assert.Contains("две", Json.Write(parsed)); // readable text, not escapes
        Assert.Null(Json.ParseObject("not json"));
    }

    [Fact]
    public void Every_language_has_the_same_strings()
    {
        var en = Loc.Keys("en").ToHashSet();
        Assert.NotEmpty(en);
        foreach (string language in Loc.Languages)
        {
            var other = Loc.Keys(language).ToHashSet();
            Assert.True(en.SetEquals(other), language + ": missing " + string.Join(", ", en.Except(other)) + "; extra " + string.Join(", ", other.Except(en)));
        }
    }

    [Fact]
    public void Every_translation_keeps_the_placeholders()
    {
        var holes = new System.Text.RegularExpressions.Regex(@"\{\d+[^}]*\}");
        var wrong = new List<string>();
        foreach (string language in Loc.Languages)
        {
            Loc.Use(language);
            foreach (string key in Loc.Keys("en"))
            {
                Loc.Use("en");
                var expected = holes.Matches(Loc.T(key)).Select(m => m.Value).Order().ToList();
                Loc.Use(language);
                var found = holes.Matches(Loc.T(key)).Select(m => m.Value).Order().ToList();
                if (!expected.SequenceEqual(found)) wrong.Add(language + " " + key);
            }
        }
        Loc.Use("en");
        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_string_key_used_in_the_code_exists()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder != null && !File.Exists(Path.Combine(folder, "Volna.sln"))) folder = Path.GetDirectoryName(folder);
        Assert.NotNull(folder);
        var known = Loc.Keys("en").ToHashSet();
        var missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(folder!, "src"), "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal)))
        {
            if (file.Contains("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.Contains("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?:Loc\.T\(""|l:Tr )([a-z0-9.]+)"))
                if (!known.Contains(m.Groups[1].Value)) missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
        }
        Assert.Empty(missing);
    }
}
