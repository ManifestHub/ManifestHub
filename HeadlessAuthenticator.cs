using SteamKit2;
using SteamKit2.Authentication;

namespace ManifestHub;

public class HeadlessAuthenticator(AccountInfoCallback account) : IAuthenticator {
    private AccountInfoCallback _account = account;

    public Task<bool> AcceptDeviceConfirmationAsync() {
        Console.Error.WriteLine($"STEAM GUARD! Pending device confirmation for {_account.AccountName}...");
        return Task.FromResult(true);
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
        return Task.FromException<string>(
            new AuthenticationException("Authentication failed", EResult.AccountLoginDeniedNeedTwoFactor));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) {
        return Task.FromException<string>(
            new AuthenticationException("Authentication failed", EResult.AccountLogonDeniedVerifiedEmailRequired));
    }
}
