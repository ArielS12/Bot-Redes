namespace TradingBots.App.Models;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "TradingBots.App";
    public string Audience { get; set; } = "TradingBots.Client";
    public string SecretKey { get; set; } = "CHANGE_ME_SUPER_SECRET_KEY_32_CHARS";
    public int ExpirationMinutes { get; set; } = 120;
}

public sealed class AdminUserSettings
{
    public const string SectionName = "AdminUser";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin123";
}
