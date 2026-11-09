using Ripparr.Arr;
using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Media;
using Ripparr.Options;

namespace Ripparr.Tests;

[TestClass]
public class SmallPartsTests
{
    [TestMethod]
    public void ReadsHowManyBytesAProcessHasRead()
    {
        // This test process has read its own assemblies at least.
        var read = Ripparr.Jobs.ProcessReads.Read(System.Environment.ProcessId);
        Assert.IsNotNull(read);
        Assert.IsGreaterThan(0L, read.Value);
        Assert.IsNull(Ripparr.Jobs.ProcessReads.Read(int.MaxValue));
    }

    [TestMethod]
    public void RawHevcIsReadByDoviToolAsIs()
    {
        Assert.IsTrue(Ripparr.Media.DoviTool.IsRawHevc("/demux/00800.bl-el.hevc"));
        Assert.IsTrue(Ripparr.Media.DoviTool.IsRawHevc("/demux/00800 PID 4117 EL.H265"));
        Assert.IsFalse(Ripparr.Media.DoviTool.IsRawHevc("/out/Film (2020).mkv"));
        Assert.IsFalse(Ripparr.Media.DoviTool.IsRawHevc("/disc/BDMV/STREAM/00004.m2ts"));
    }

    [TestMethod]
    public void ParsesSevenZipListings()
    {
        const string output = "Path = BDMV\nFolder = +\nSize = \n\nPath = BDMV/STREAM/00055.m2ts\nFolder = -\nSize = 40000000000\n\nPath = AACS/Unit_Key_RO.inf\nFolder = -\nSize = 2\n";
        var entries = IsoReader.ParseListing(output);

        Assert.HasCount(3, entries);
        Assert.IsTrue(entries[0].Folder);
        Assert.AreEqual(40000000000, entries[1].Size);
        Assert.AreEqual("AACS/Unit_Key_RO.inf", entries[2].Path);
    }

    [TestMethod]
    public void GuessesTitleAndYearFromReleaseNames()
    {
        Assert.AreEqual(("The Matrix", 1999), ReleaseName.Guess("The.Matrix.1999.2160p.UHD.BluRay.REMUX-GRP"));
        Assert.AreEqual(("Blade Runner 2049", 2017), ReleaseName.Guess("Blade Runner 2049 (2017) UHD"));
        Assert.AreEqual(("Some Film", (int?)null), ReleaseName.Guess("SOME_FILM_UHD"));
        Assert.AreEqual(("1917", 2019), ReleaseName.Guess("1917.2019.COMPLETE.UHD.BLURAY"));
    }

    [TestMethod]
    public void ReadsSeriesAndSeasonFromTheDiscTitle()
    {
        Assert.AreEqual(("Doctor Who", 1), ReleaseName.Season("Doctor Who - Season 1 - Disc 1"));
        Assert.AreEqual(("Sherlock", 2), ReleaseName.Season("Sherlock: Series 2"));
        Assert.AreEqual(("Kaamelott", 3), ReleaseName.Season("Kaamelott Saison 3"));
        Assert.IsNull(ReleaseName.Season("The Fabelmans"));
        Assert.IsNull(ReleaseName.Season("Season 1"));
        // DVD volume labels.
        Assert.AreEqual(("Doctor Who", 1), ReleaseName.Season("DOCTOR_WHO_S1_D1"));
        Assert.AreEqual(("Long Show", 1), ReleaseName.Season("LONG_SHOW_S01_D2"));
        Assert.AreEqual(("Friends", 2), ReleaseName.Season("FRIENDS_SEASON_2_DISC_3"));
        Assert.AreEqual(("The Wire", 3), ReleaseName.Season("The.Wire.S03D1"));
        Assert.IsNull(ReleaseName.Season("STAR_WARS_EPISODE_4"));
        Assert.IsNull(ReleaseName.Season(null));
    }

    [TestMethod]
    public void LanguageHelpers()
    {
        Assert.IsTrue(Languages.Same("fre", "fra"));
        Assert.IsFalse(Languages.Same("eng", "fre"));
        Assert.AreEqual("fre", Languages.FromName("French"));
        Assert.AreEqual("eng", Languages.FromName("English"));
        Assert.AreEqual("English", Languages.Name("eng"));
    }

    [TestMethod]
    public void ParsesRadarrLookups()
    {
        const string json = """
            [{"title":"The Matrix","year":1999,"tmdbId":603,"imdbId":"tt0133093","originalLanguage":{"id":1,"name":"English"},"id":12,"path":"/movies/live-action/The Matrix (1999)"},
             {"title":"Amélie","year":2001,"tmdbId":194,"imdbId":"tt0211915","originalLanguage":{"id":2,"name":"French"}},
             {"title":"No id","year":2001,"tmdbId":0}]
            """;
        var matches = RadarrClient.Parse(json);

        Assert.HasCount(2, matches);
        Assert.AreEqual("/movies/live-action/The Matrix (1999)", matches[0].LibraryPath);
        Assert.AreEqual("fre", matches[1].OriginalLanguage);
        Assert.IsNull(matches[1].LibraryPath);
    }

    [TestMethod]
    public void ReadsMkvmergeIdentifyAndComparesWithThePlaylist()
    {
        const string json = """
            {"container":{"properties":{"duration":7200500000000}},
             "chapters":[{"num_entries":4}],
             "tracks":[{"type":"video","properties":{"language":"eng","default_track":true}},
                       {"type":"audio","properties":{"language":"eng","track_name":"English - TrueHD Atmos 7.1","default_track":true}},
                       {"type":"subtitles","properties":{"language":"fre","forced_track":false}}]}
            """;
        var summary = Verifier.ParseIdentify(json);
        Assert.IsNotNull(summary);
        Assert.HasCount(3, summary.Tracks);
        Assert.AreEqual(7200.5, summary.Duration.TotalSeconds, 0.001);
        Assert.AreEqual(4, summary.Chapters);

        var playlist = new PlaylistInfo { Name = "00800", Mpls = Mpls.Parse(MplsBuilder.UhdFilm(120).Build()) };
        var facts = new StreamFacts();
        List<OutputTrack> outputs =
        [
            new(new Profiles.PlannedTrack { Kind = StreamKind.Video, Language = "eng" }, new DemuxedFile("/v", facts), ""),
            new(new Profiles.PlannedTrack { Kind = StreamKind.Audio, Language = "eng" }, new DemuxedFile("/a", facts), ""),
            new(new Profiles.PlannedTrack { Kind = StreamKind.Subtitle, Language = "fra" }, new DemuxedFile("/s", facts), "")
        ];
        var checks = Verifier.Compare(summary, playlist, outputs);

        Assert.IsTrue(checks.All(c => c.Passed), string.Join("; ", checks.Select(c => $"{c.Name}: {c.Detail}")));
        Assert.IsFalse(checks.Single(c => c.Name == "Duration").Warning);
    }

    [TestMethod]
    public void VerifyWarnsAboutNamesFlagsAndTitleAndFailsOnOrder()
    {
        const string json = """
            {"container":{"properties":{"duration":7200000000000,"title":"Film (2020)"}},
             "chapters":[{"num_entries":4}],
             "tracks":[{"type":"video","properties":{"language":"eng","track_name":"2160p - HEVC","default_track":true}},
                       {"type":"audio","properties":{"language":"eng","track_name":"English - AC-3 2.0","default_track":false,"flag_commentary":true}},
                       {"type":"subtitles","properties":{"language":"eng","track_name":"English (SDH) - PGS","forced_track":false,"flag_hearing_impaired":false}}]}
            """;
        var summary = Verifier.ParseIdentify(json)!;
        Assert.AreEqual("Film (2020)", summary.Title);
        Assert.IsTrue(summary.Tracks[1].Commentary);

        var playlist = new PlaylistInfo { Name = "00800", Mpls = Mpls.Parse(MplsBuilder.UhdFilm(120).Build()) };
        var facts = new StreamFacts();
        List<OutputTrack> outputs =
        [
            new(new Profiles.PlannedTrack { Kind = StreamKind.Video, Language = "eng", Default = true }, new DemuxedFile("/v", facts), "2160p - HEVC"),
            new(new Profiles.PlannedTrack { Kind = StreamKind.Audio, Language = "eng", Commentary = true }, new DemuxedFile("/a", facts), "English - AC-3 2.0"),
            new(new Profiles.PlannedTrack { Kind = StreamKind.Subtitle, Language = "eng", HearingImpaired = true }, new DemuxedFile("/s", facts), "English (SDH) - PGS")
        ];

        var checks = Verifier.Compare(summary, playlist, outputs, "Film (2020)");
        Assert.IsTrue(checks.All(c => c.Passed));
        var flags = checks.Single(c => c.Name == "Flags");
        Assert.IsTrue(flags.Warning);
        Assert.AreEqual("track 3: SDH off, planned on", flags.Detail);
        Assert.IsFalse(checks.Single(c => c.Name == "Names").Warning);
        Assert.IsFalse(checks.Single(c => c.Name == "Title").Warning);
        Assert.IsTrue(Verifier.Compare(summary, playlist, outputs, "Other (2020)").Single(c => c.Name == "Title").Warning);

        // The audio and subtitle tracks swapped: a real failure.
        List<OutputTrack> swapped = [outputs[0], outputs[2], outputs[1]];
        var order = Verifier.Compare(summary, playlist, swapped).Single(c => c.Name == "Order");
        Assert.IsFalse(order.Passed);
        StringAssert.Contains(order.Detail, "track 2 is audio, planned subtitle");
    }

    [TestMethod]
    public void JobStoreKeepsJobsProfilesAndSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}.db");
        try
        {
            var store = new JobStore(path);
            Assert.HasCount(3, store.Profiles());

            var job = store.Add(new Job { Title = "Film", Year = 2020, Playlist = "00800", Profile = store.Profiles()[0] });
            Assert.IsGreaterThan(0, job.Id);
            Assert.AreEqual(job.Id, store.NextQueued()?.Id);

            job.Status = JobStatus.Done;
            job.Checks.Add(new VerifyCheck("Tracks", true, "ok"));
            store.Update(job);
            var read = store.Get(job.Id)!;
            Assert.AreEqual(JobStatus.Done, read.Status);
            Assert.AreEqual("Tracks", read.Checks.Single().Name);
            Assert.IsNull(store.NextQueued());
            Assert.HasCount(1, store.List(10, [JobStatus.Done]));

            var copy = store.Profiles()[0].Copy("Mine");
            store.SaveProfile(copy);
            Assert.AreEqual("Mine", store.Profile(copy.Id)?.Name);

            store.SetSetting("a", "b");
            Assert.AreEqual("b", store.Setting("a"));
            store.SetSetting("a", null);
            Assert.IsNull(store.Setting("a"));
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [TestMethod]
    public void UsesAToolPlacedInTheConfigFolder()
    {
        var folder = Directory.CreateTempSubdirectory("ripparr-tools").FullName;
        try
        {
            Assert.AreEqual("dgdemux", RipparrOptions.InToolsFolder(folder, "dgdemux", "dgdemux"));
            var file = Path.Combine(folder, "dgdemux");
            File.WriteAllText(file, "");
            Assert.AreEqual(file, RipparrOptions.InToolsFolder(folder, "dgdemux", "dgdemux"));
            // A path set by hand stays.
            Assert.AreEqual("/opt/dgdemux", RipparrOptions.InToolsFolder(folder, "dgdemux", "/opt/dgdemux"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void FindsFoldersItCannotWrite()
    {
        var folder = Directory.CreateTempSubdirectory("ripparr-folders").FullName;
        try
        {
            Assert.IsNull(FolderCheck.Check(Path.Combine(folder, "work")));
            Assert.IsTrue(Directory.Exists(Path.Combine(folder, "work")));
            // A path under a file can never be made.
            File.WriteAllText(Path.Combine(folder, "file"), "");
            Assert.IsNotNull(FolderCheck.Check(Path.Combine(folder, "file", "work")));
            StringAssert.StartsWith(FolderCheck.Hint([new FolderProblem("Work folder", "/downloads/ripparr/work", "not writable")]), "sudo chown -R ");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void QueuesADryRunForReal()
    {
        var dry = new Ripparr.Jobs.Job
        {
            Id = 7, DryRun = true, Status = Ripparr.Jobs.JobStatus.Done, SourcePath = "/downloads/Film.iso", Playlist = "00800",
            Title = "Film", Year = 2020, Error = "x", Warnings = ["w"], Step = "Finish",
            Tags = [new() { Name = "RELEASE_GROUP", Value = "FraMeSToR" }]
        };
        dry.Plan.Tracks.Add(new Ripparr.Profiles.PlannedTrack { Pid = 4113, Keep = true, Name = "Main" });
        var real = dry.ForReal();

        Assert.IsFalse(real.DryRun);
        Assert.AreEqual(0, real.Id);
        Assert.AreEqual(Ripparr.Jobs.JobStatus.Queued, real.Status);
        Assert.IsNull(real.Error);
        Assert.IsNull(real.Step);
        Assert.IsEmpty(real.Warnings);
        Assert.AreEqual("/downloads/Film.iso", real.SourcePath);
        Assert.AreEqual("FraMeSToR", real.Tags.Single().Value);
        Assert.AreEqual("Main", real.Plan.Tracks.Single().Name);

        // A copy: changing it leaves the dry run as it was.
        real.Plan.Tracks[0].Name = "Other";
        Assert.AreEqual("Main", dry.Plan.Tracks[0].Name);
    }

    [TestMethod]
    public void ExtrasGoNextToTheFilmInTheirTypeFolder()
    {
        var values = new Dictionary<string, string?> { ["title"] = "Wolf Children", ["year"] = "2012" };
        var film = new Ripparr.Jobs.Job { Title = "Wolf Children", Year = 2012 };
        var extra = new Ripparr.Jobs.Job { Title = "Wolf Children", Year = 2012, ExtraType = "featurettes", ExtraName = "Making Of" };

        Assert.AreEqual("/done/Wolf Children (2012)", Ripparr.Jobs.JobPipeline.DoneFolder("/done", film, values));
        Assert.AreEqual("/done/Wolf Children (2012)/featurettes", Ripparr.Jobs.JobPipeline.DoneFolder("/done", extra, values));
        Assert.AreEqual("Wolf Children (2012) · Making Of", extra.DisplayTitle);
        Assert.AreEqual("featurettes", extra.ForReal().ExtraType);
        Assert.AreEqual("Deleted Scene 2", Arrkit.Library.ExtraTypes.DefaultName("deleted scenes", 2));
        Assert.IsTrue(Arrkit.Library.ExtraTypes.All.All(t => t.Folder == t.Folder.ToLowerInvariant()), "Jellyfin's folder names are lower case");
    }

    [TestMethod]
    public void PicturesPointsFallInTheRightClip()
    {
        var playlist = Ripparr.Disc.Mpls.Parse(MplsBuilder.Feature(("00002", 60), ("00003", 40)).Build());
        Assert.AreEqual(("00002", 10.0), Ripparr.Disc.Thumbnails.Locate(playlist, 0.1));
        Assert.AreEqual("00003", Ripparr.Disc.Thumbnails.Locate(playlist, 0.9).Clip);
        Assert.AreEqual(30.0, Ripparr.Disc.Thumbnails.Locate(playlist, 0.9).Seconds, 0.01);
    }

    [TestMethod]
    public void AnInstallThatHadProfilesGetsTheExtrasProfileOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}.db");
        try
        {
            var store = new Ripparr.Jobs.JobStore(Microsoft.Extensions.Options.Options.Create(new Ripparr.Options.RipparrOptions { DatabasePath = path }));
            Assert.IsTrue(store.Profiles().Any(p => p.Id == Ripparr.Profiles.RipProfile.ExtrasId));
            store.DeleteProfile(Ripparr.Profiles.RipProfile.ExtrasId);

            var again = new Ripparr.Jobs.JobStore(Microsoft.Extensions.Options.Options.Create(new Ripparr.Options.RipparrOptions { DatabasePath = path }));
            Assert.IsFalse(again.Profiles().Any(p => p.Id == Ripparr.Profiles.RipProfile.ExtrasId), "a deleted Extras profile stays deleted");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EpisodesAreNamedAndFiledAsSonarrDoes()
    {
        var job = new Ripparr.Jobs.Job
        {
            Title = "Test Show", Year = 2019, TvdbId = 12345, TmdbId = 678, Season = 1, Episode = 5, AbsoluteEpisode = 5,
            EpisodeTitle = "Chapter 5: The Bridge", Profile = new Ripparr.Profiles.RipProfile()
        };
        var values = Ripparr.Jobs.MuxBuilder.NameValues(job, []);
        values["resolution"] = "1080p";

        Assert.AreEqual("/done/Test Show (2019)/Season 01", Ripparr.Jobs.JobPipeline.DoneFolder("/done", job, values));
        Assert.AreEqual("Test Show S01E05 (005) [1080p][BD Remux] - Chapter 5 - The Bridge.mkv", Ripparr.Jobs.JobPipeline.FileName(job, values));
        Assert.AreEqual("Test Show S01E05 - Chapter 5: The Bridge", Ripparr.Jobs.JobPipeline.MkvTitle(job, values));
        Assert.AreEqual("Test Show (2019) · S01E05 - Chapter 5: The Bridge", job.DisplayTitle);

        var tags = Ripparr.Jobs.MuxBuilder.TagsXml(job, []);
        StringAssert.Contains(tags, "<Name>TMDB</Name><String>tv/678</String>");
        StringAssert.Contains(tags, "<Name>TVDB2</Name><String>series/12345</String>");
        StringAssert.Contains(tags, "<Name>ABSOLUTE_EPISODE</Name><String>5</String>");

        // Not anime: no absolute number in the name. Season 0 is Sonarr's Specials folder.
        job.AbsoluteEpisode = null;
        job.Season = 0;
        values = Ripparr.Jobs.MuxBuilder.NameValues(job, []);
        Assert.AreEqual("/done/Test Show (2019)/Specials", Ripparr.Jobs.JobPipeline.DoneFolder("/done", job, values));
        StringAssert.StartsWith(Ripparr.Jobs.JobPipeline.FileName(job, values), "Test Show S00E05 [BD Remux]");

        // A TV extra put with its season.
        var extra = new Ripparr.Jobs.Job { Title = "Test Show", Year = 2019, TvdbId = 12345, Season = 1, ExtraType = "extras", ExtraName = "Creditless Opening" };
        Assert.AreEqual("/done/Test Show (2019)/Season 01/extras",
            Ripparr.Jobs.JobPipeline.DoneFolder("/done", extra, Ripparr.Jobs.MuxBuilder.NameValues(extra, [])));
        Assert.IsFalse(extra.IsEpisode);
    }

    [TestMethod]
    public void ReadsSonarrSeriesAndEpisodes()
    {
        var series = Ripparr.Arr.SonarrClient.ParseSeries("""
            [{"title":"Test Show","year":2019,"tvdbId":12345,"tmdbId":678,"imdbId":"tt0000001","id":7,"path":"/tv/anime/Test Show (2019)",
              "seriesType":"anime","originalLanguage":{"id":8,"name":"Japanese"},"seasons":[{"seasonNumber":1},{"seasonNumber":0}]},
             {"title":"Not Added","year":2021,"tvdbId":99,"seriesType":"standard","originalLanguage":{"name":"English"}},
             {"title":"No TVDB","tvdbId":0}]
            """);
        Assert.HasCount(2, series);
        Assert.IsTrue(series[0].Anime);
        Assert.AreEqual(7, series[0].SonarrId);
        Assert.AreEqual("jpn", series[0].OriginalLanguage);
        CollectionAssert.AreEqual(new[] { 0, 1 }, series[0].Seasons.ToArray());
        Assert.IsNull(series[1].SonarrId);
        Assert.IsNull(series[1].LibraryPath);

        var episodes = Ripparr.Arr.SonarrClient.ParseEpisodes("""
            [{"seasonNumber":1,"episodeNumber":2,"absoluteEpisodeNumber":2,"title":"Two","runtime":45,"airDate":"2005-06-11","overview":" The Doctor takes Rose to the year five billion. "},
             {"seasonNumber":1,"episodeNumber":1,"title":"One","runtime":0}]
            """);
        Assert.AreEqual("One", episodes[0].Title);
        Assert.IsNull(episodes[0].Absolute);
        Assert.IsNull(episodes[0].Runtime, "0 is Sonarr's unknown");
        Assert.AreEqual(2, episodes[1].Absolute);
        Assert.AreEqual(45, episodes[1].Runtime);
        Assert.AreEqual(new DateOnly(2005, 6, 11), episodes[1].AirDate);
        Assert.AreEqual("The Doctor takes Rose to the year five billion.", episodes[1].Overview);
        Assert.IsNull(episodes[0].Overview);
    }

    [TestMethod]
    public void TheQueueKeepsWhatIsActiveOrNeedsALook()
    {
        Assert.IsTrue(new Ripparr.Jobs.Job { Status = Ripparr.Jobs.JobStatus.Running }.InQueue);
        Assert.IsTrue(new Ripparr.Jobs.Job { Status = Ripparr.Jobs.JobStatus.Failed }.InQueue);
        Assert.IsFalse(new Ripparr.Jobs.Job { Status = Ripparr.Jobs.JobStatus.Failed, Dismissed = true }.InQueue);
        Assert.IsFalse(new Ripparr.Jobs.Job { Status = Ripparr.Jobs.JobStatus.Done }.InQueue);
        Assert.IsFalse(new Ripparr.Jobs.Job { Status = Ripparr.Jobs.JobStatus.Cancelled }.InQueue);
    }
}
