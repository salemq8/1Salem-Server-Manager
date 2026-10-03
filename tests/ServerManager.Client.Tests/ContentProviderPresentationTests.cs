using ServerManager.Client.Controls;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

public sealed class ContentProviderPresentationTests
{
    [Fact]
    public void OneFailedAndOneEmptySuccess_DoesNotClaimBothUnavailable()
    {
        var result = Result(new(ContentProviderId.Hangar, false, "Offline"), new(ContentProviderId.Modrinth, true));
        Assert.False(ContentProviderPresentation.BothUnavailable(result));
        Assert.Equal([ContentProviderId.Hangar], ContentProviderPresentation.FailedProviders(result));
    }

    [Fact]
    public void UnattemptedProvider_IsNotAFailure()
    {
        var result = Result(new ContentProviderStatus(ContentProviderId.Modrinth, false, "Offline"));
        Assert.False(ContentProviderPresentation.BothUnavailable(result));
    }

    [Fact]
    public void BothMustExplicitlyFail()
    {
        Assert.True(ContentProviderPresentation.BothUnavailable(Result(
            new(ContentProviderId.Hangar, false, "Offline"), new(ContentProviderId.Modrinth, false, "Offline"))));
    }

    [Fact]
    public void VisibleResults_CannotBeDescribedAsBothUnavailable()
    {
        var result = Result(new(ContentProviderId.Hangar, false), new(ContentProviderId.Modrinth, false)) with
        {
            Projects = [new(ContentProviderId.Modrinth, "id", "slug", "Loaded project", null, null, null, null, null, [], [])]
        };
        Assert.False(ContentProviderPresentation.BothUnavailable(result));
    }

    [Fact]
    public void OldAgentResponse_UsesOnlyItsNamedErrors()
    {
        var result = new ContentSearchResult([], 0, 30, 0, ["Hangar:Offline"]);
        Assert.False(ContentProviderPresentation.BothUnavailable(result));
        Assert.Equal([ContentProviderId.Hangar], ContentProviderPresentation.FailedProviders(result));
    }

    private static ContentSearchResult Result(params ContentProviderStatus[] statuses) => new([], 0, 30, 0, [], statuses);
}
