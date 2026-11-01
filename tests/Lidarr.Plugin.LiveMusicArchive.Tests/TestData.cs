using Lidarr.Plugin.LiveMusicArchive.Core;
using Newtonsoft.Json.Linq;
using NLog;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    /// <summary>Recorded archive.org and Lidarr metadata responses, plus shared test values.</summary>
    internal static class TestData
    {
        public const string GratefulDead = "6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6";

        /// <summary>A stream-only item: FLAC originals are private, MP3 derivatives are public.</summary>
        public const string StreamOnlyFlacItem = "gd1977-05-08.137570.mtx.dusborne.flac16";

        /// <summary>A soundboard item with MP3s and Shorten originals, disc/track named (d1t01).</summary>
        public const string SoundboardItem = "gd77-05-08.sbd.hicks.4982.sbeok.shnf";

        /// <summary>A Shorten-original item (Dispatch, 1996): lossless but unimportable, with wrong .shn lengths.</summary>
        public const string ShortenItem = "dispatch1996-04-16.shnf";

        /// <summary>An item whose only audio is "MPEG-4 Audio" (.m4a, AAC at ~278 kbps), with "1 of 11" track numbers.</summary>
        public const string M4aOnlyItem = "josegonzalez2006-09-08";

        public static readonly Logger Logger = LogManager.CreateNullLogger();

        public static JObject Item(string identifier) => Fixture(identifier);

        public static JObject Artist() => Fixture("artist-grateful-dead");

        public static JObject Fixture(string name) =>
            IaJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));

        /// <summary>A minimal item with explicit files, for cases the recorded fixtures don't cover.</summary>
        public static JObject SyntheticItem(string identifier, params object[] files) =>
            JObject.FromObject(new { metadata = new { identifier, title = identifier, source = "SBD > DAT" }, files });

        public static RecordingSummary Recording(string identifier, string date, long downloads = 0, string venue = "Barton Hall", string coverage = "Ithaca, NY") =>
            new() { Identifier = identifier, Date = date, Downloads = downloads, Venue = venue, Coverage = coverage };
    }
}
