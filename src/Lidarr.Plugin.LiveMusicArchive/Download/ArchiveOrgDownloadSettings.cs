using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Lidarr.Plugin.LiveMusicArchive.Download
{
    /// <summary>Validates <see cref="ArchiveOrgDownloadSettings"/>.</summary>
    public class ArchiveOrgDownloadSettingsValidator : AbstractValidator<ArchiveOrgDownloadSettings>
    {
        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgDownloadSettingsValidator"/> class.</summary>
        public ArchiveOrgDownloadSettingsValidator()
        {
            RuleFor(x => x.DownloadPath).IsValidPath();
        }
    }

    /// <summary>Settings for the Live Music Archive download client.</summary>
    public class ArchiveOrgDownloadSettings : IProviderConfig
    {
        private static readonly ArchiveOrgDownloadSettingsValidator Validator = new();

        /// <summary>Gets or sets the folder downloads are written to; each show gets its own sub-folder.</summary>
        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Folder for in-progress and completed downloads. Lidarr imports from here.")]
        public string DownloadPath { get; set; } = "";

        /// <inheritdoc />
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
