using Lidarr.Plugin.LiveMusicArchive.Indexers;
using NSubstitute;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Profiles.Delay;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class DelayProfileRegistrationTests
    {
        [Fact]
        public void adds_the_protocol_once_and_allows_it()
        {
            var profile = new DelayProfile();

            Assert.True(DelayProfileRegistration.AddProtocol(profile));
            Assert.False(DelayProfileRegistration.AddProtocol(profile));

            var item = Assert.Single(profile.Items, i => i.Protocol == nameof(ArchiveOrgDownloadProtocol));
            Assert.True(item.Allowed);
            Assert.Equal(0, item.Delay);
        }

        [Fact]
        public void saves_only_profiles_it_changed_at_startup()
        {
            var fresh = new DelayProfile { Id = 1 };
            var done = new DelayProfile { Id = 2 };
            DelayProfileRegistration.AddProtocol(done);
            var repository = Substitute.For<IDelayProfileRepository>();
            repository.All().Returns(new[] { fresh, done });

            new DelayProfileRegistration(repository, TestData.Logger).Handle(new ApplicationStartedEvent());

            repository.Received(1).Update(fresh);
            repository.DidNotReceive().Update(done);
        }
    }
}
