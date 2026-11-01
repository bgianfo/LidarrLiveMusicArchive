using FluentValidation;
using Lidarr.Plugin.LiveMusicArchive.Core;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.LiveMusicArchive.Metadata
{
    /// <summary>Validates <see cref="LiveMusicArchiveSettings"/>.</summary>
    public class LiveMusicArchiveSettingsValidator : AbstractValidator<LiveMusicArchiveSettings>
    {
        /// <summary>Initializes a new instance of the <see cref="LiveMusicArchiveSettingsValidator"/> class.</summary>
        public LiveMusicArchiveSettingsValidator()
        {
            RuleFor(x => x.Years)
                .Must(y => YearRange.TryParse(y, out _))
                .WithMessage("Use a single year (1977) or a range (1972-1974), or leave empty for all years.");

            RuleFor(x => x.MaxRecordingsPerShow).InclusiveBetween(1, 100);
        }
    }

    /// <summary>Settings for adding Live Music Archive shows to artists (Settings → Metadata).</summary>
    public class LiveMusicArchiveSettings : IProviderConfig
    {
        private static readonly LiveMusicArchiveSettingsValidator Validator = new();

        /// <summary>Gets or sets the era filter, e.g. "1977" or "1972-1974"; empty for every year.</summary>
        [FieldDefinition(0, Label = "Years", HelpText = "Only add shows from these years, e.g. 1977 or 1972-1974. Leave empty for every year; large catalogues (Grateful Dead: ~2,000 shows) make artist refreshes slow.")]
        public string Years { get; set; } = "";

        /// <summary>Gets or sets how many recordings (most downloaded first) become releases of each show.</summary>
        [FieldDefinition(1, Label = "Max Recordings per Show", Type = FieldType.Number, HelpText = "Most-downloaded recordings to offer as releases for each show. Each one costs an archive.org request on first refresh.")]
        public int MaxRecordingsPerShow { get; set; } = 5;

        /// <summary>Converts the settings to the options the core service takes.</summary>
        /// <returns>The options.</returns>
        public LiveMusicArchiveOptions ToOptions()
        {
            YearRange.TryParse(Years, out var years);
            return new LiveMusicArchiveOptions { Years = years, MaxRecordingsPerShow = MaxRecordingsPerShow };
        }

        /// <inheritdoc />
        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
