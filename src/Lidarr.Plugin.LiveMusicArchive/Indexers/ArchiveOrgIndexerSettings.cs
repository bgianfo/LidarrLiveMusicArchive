using FluentValidation;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Annotations;

namespace Lidarr.Plugin.LiveMusicArchive.Indexers
{
    /// <summary>Validates <see cref="ArchiveOrgIndexerSettings"/>.</summary>
    public class ArchiveOrgIndexerSettingsValidator : AbstractValidator<ArchiveOrgIndexerSettings>
    {
        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgIndexerSettingsValidator"/> class.</summary>
        public ArchiveOrgIndexerSettingsValidator()
        {
        }
    }

    /// <summary>Settings for the Live Music Archive indexer.</summary>
    public class ArchiveOrgIndexerSettings : IIndexerSettings
    {
        private static readonly ArchiveOrgIndexerSettingsValidator Validator = new();

        /// <summary>Gets or sets the archive.org address. Not user-editable; required by <see cref="IIndexerSettings"/>.</summary>
        [FieldDefinition(0, Label = "URL")]
        public string BaseUrl { get; set; } = "https://archive.org";

        /// <summary>Gets or sets the early release limit; unused because archive.org shows are always "released".</summary>
        [FieldDefinition(1, Type = FieldType.Number, Label = "Early Download Limit", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit (Currently Unused)", Unit = "days", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        /// <inheritdoc />
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
