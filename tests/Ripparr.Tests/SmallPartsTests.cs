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
    public void JobStoreKeepsJobsProfilesAndSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ripparr-{Guid.NewGuid():N}.db");
        try
        {
            var store = new JobStore(path);
            Assert.HasCount(2, store.Profiles());

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
}
