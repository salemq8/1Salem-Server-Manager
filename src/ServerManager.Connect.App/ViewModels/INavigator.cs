using ServerManager.Connect.App.Broker;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>The page flow: Welcome → Invite → Waiting for approval → Servers → Connection, plus Diagnostics.</summary>
public interface INavigator
{
    void ShowWelcome();

    void ShowInvite();

    void ShowWaiting(InviteRedemption redemption);

    void ShowServers();

    void ShowConnection(Membership membership);

    void ShowDiagnostics();

    /// <summary>The connection page of a server, if one was opened in this run.</summary>
    ConnectionViewModel? FindConnection(string membershipId);
}

/// <summary>Pages that poll start when shown and stop when the friend moves elsewhere.</summary>
public interface IPageLifetime
{
    void OnShown();

    void OnHidden();
}
