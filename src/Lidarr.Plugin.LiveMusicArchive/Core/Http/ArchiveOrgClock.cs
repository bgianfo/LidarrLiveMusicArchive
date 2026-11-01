namespace Lidarr.Plugin.LiveMusicArchive.Core.Http
{
    /// <summary>
    /// The plugin's source of time. Lidarr's container only registers its own types, so the BCL
    /// <see cref="TimeProvider"/> is wrapped in a plugin interface; tests substitute a fake clock.
    /// </summary>
    public interface IArchiveOrgClock
    {
        /// <summary>Gets the time provider used for delays, rate limiting and cache ages.</summary>
        TimeProvider Time { get; }
    }

    /// <summary>The real clock.</summary>
    public class ArchiveOrgClock : IArchiveOrgClock
    {
        /// <inheritdoc />
        public TimeProvider Time => TimeProvider.System;
    }
}
