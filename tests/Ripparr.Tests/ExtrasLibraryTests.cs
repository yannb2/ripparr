using Arrkit.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Ripparr.Arr;
using Ripparr.Jobs;
using Ripparr.Options;

namespace Ripparr.Tests;

/// <summary>The Extras page: finished extras found, their library folder found, copied, and marked.</summary>
[TestClass]
public class ExtrasLibraryTests
{
    private string _folder = null!;

    [TestInitialize]
    public void Setup() => _folder = Directory.CreateTempSubdirectory("ripparr-extras-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_folder, true);

    private (ExtrasLibrary Library, JobStore Store, LiveSettings Live) Make()
    {
        var options = new RipparrOptions
        {
            DoneFolder = Path.Combine(_folder, "done"),
            Extras = new ExtrasOptions { MoviesFolder = Path.Combine(_folder, "movies"), TvFolder = Path.Combine(_folder, "tv") }
        };
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var store = new JobStore(Path.Combine(_folder, "ripparr.db"));
        var live = new LiveSettings(wrapped, store);
        var library = new ExtrasLibrary(store, live, new RadarrClient(live, NullLogger<RadarrClient>.Instance),
            new SonarrClient(live, NullLogger<SonarrClient>.Instance), wrapped, NullLogger<ExtrasLibrary>.Instance);
        return (library, store, live);
    }

    private Job DoneExtra(JobStore store, string type, string name, bool dryRun = false)
    {
        var path = Path.Combine(_folder, "done", "Heat (1995)", type, name + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);
        return store.Add(new Job
        {
            Title = "Heat", Year = 1995, TmdbId = 949, ExtraType = type, ExtraName = name, Status = JobStatus.Done,
            OutputPath = path, DryRun = dryRun
        });
    }

    [TestMethod]
    public async Task CopiesDoneExtrasIntoTheFilmFolderFoundByName()
    {
        var (library, store, live) = Make();
        var film = Path.Combine(_folder, "movies", "live-action", "Heat (1995)");
        Directory.CreateDirectory(film);
        DoneExtra(store, "featurettes", "Making Of");
        DoneExtra(store, "trailers", "Trailer");
        DoneExtra(store, "trailers", "Dry", dryRun: true);
        store.Add(new Job { Title = "Heat", Year = 1995, TmdbId = 949, Status = JobStatus.Done, OutputPath = Path.Combine(_folder, "x.mkv") });

        var groups = library.Waiting();
        Assert.HasCount(1, groups);
        Assert.AreEqual("movie:949", groups[0].Key);
        Assert.HasCount(2, groups[0].Jobs, "the film and the dry run are not extras to copy");

        var lookup = await library.FindAsync(groups[0], CancellationToken.None);
        Assert.AreEqual(film, lookup.Path);
        Assert.AreEqual("found by name", lookup.How);

        live.Save(LiveSettings.ExtrasKey, new ExtrasOptions
        {
            MoviesFolder = Path.Combine(_folder, "movies"), TvFolder = Path.Combine(_folder, "tv"), RemoveDoneCopy = true
        });
        var result = await library.CopyAsync(groups[0], lookup.Path!, CancellationToken.None);

        Assert.IsTrue(result.Ok, result.Summary);
        Assert.AreEqual(2, result.Copied);
        Assert.IsTrue(File.Exists(Path.Combine(film, "featurettes", "Making Of.mkv")));
        Assert.IsTrue(File.Exists(Path.Combine(film, "trailers", "Trailer.mkv")));
        Assert.IsEmpty(library.Waiting());
        Assert.HasCount(2, library.Copied());
        Assert.IsFalse(Directory.Exists(Path.Combine(_folder, "done", "Heat (1995)", "featurettes")), "emptied done folders go");
        Assert.IsTrue(Directory.Exists(Path.Combine(_folder, "done", "Heat (1995)", "trailers")), "the dry run's file stays");
    }

    [TestMethod]
    public async Task WithoutTheFilmInTheLibraryItWaitsUntilAFolderIsPicked()
    {
        var (library, store, _) = Make();
        DoneExtra(store, "featurettes", "Making Of");
        var other = Path.Combine(_folder, "movies", "live-action", "Heat - Director's Cut (1995)");
        Directory.CreateDirectory(other);
        var group = library.Waiting().Single();

        var lookup = await library.FindAsync(group, CancellationToken.None);
        Assert.IsNull(lookup.Path);
        StringAssert.Contains(lookup.How, "No Heat (1995) folder");

        library.Choose(group, other);
        Assert.AreEqual(other, (await library.FindAsync(group, CancellationToken.None)).Path);

        // A root or a category is never a film folder.
        library.Choose(group, Path.Combine(_folder, "movies", "live-action"));
        Assert.IsNull((await library.FindAsync(group, CancellationToken.None)).Path);
    }
}
