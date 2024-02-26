using SteamKit2.Authentication;

namespace ManifestHub;

public class HeadlessAuthenticator : IAuthenticator {
    public Task<bool> AcceptDeviceConfirmationAsync() {
        Console.Error.WriteLine("STEAM GUARD! Pending device confirmation.");
        return Task.FromResult(true);
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
        return Task.FromException<string>(
            new NotImplementedException("Device code is not supported in headless mode."));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) {
        return Task.FromException<string>(
            new NotImplementedException("Email code is not supported in headless mode."));
    }
}
