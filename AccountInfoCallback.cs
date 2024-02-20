namespace ManifestHub;

public class AccountInfoCallback(
    string accountName,
    string? accountPassword = null,
    string? refreshToken = null,
    DateTime? lastRefresh = null,
    string? index = null) {
    public string AccountName { get; set; } = accountName;
    public string? AccountPassword { get; set; } = accountPassword;
    public string? RefreshToken { get; set; } = refreshToken;
    public DateTime? LastRefresh { get; set; } = lastRefresh;
    public string? Index { get; set; } = index;
}
