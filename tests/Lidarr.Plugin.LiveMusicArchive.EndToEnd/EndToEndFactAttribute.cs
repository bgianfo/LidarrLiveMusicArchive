using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.EndToEnd
{
    /// <summary>A test that only runs when LMA_E2E=1, so a plain <c>dotnet test</c> stays fast and offline.</summary>
    public sealed class EndToEndFactAttribute : FactAttribute
    {
        public EndToEndFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("LMA_E2E") != "1")
            {
                Skip = "End-to-end tests run only with LMA_E2E=1 (see docs/TESTING.md).";
            }
        }
    }
}
