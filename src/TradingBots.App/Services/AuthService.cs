using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IAuthService
{
    LoginResponse? Login(LoginRequest request);
}

public sealed class AuthService(
    IOptions<JwtSettings> jwtOptions,
    IOptions<AdminUserSettings> adminUserOptions) : IAuthService
{
    private readonly JwtSettings _jwt = jwtOptions.Value;
    private readonly AdminUserSettings _adminUser = adminUserOptions.Value;

    public LoginResponse? Login(LoginRequest request)
    {
        if (!string.Equals(request.Username, _adminUser.Username, StringComparison.Ordinal) ||
            !string.Equals(request.Password, _adminUser.Password, StringComparison.Ordinal))
        {
            return null;
        }

        var expiresAt = request.RememberMe
            ? DateTime.UtcNow.AddDays(Math.Max(1, _jwt.RememberMeExpirationDays))
            : DateTime.UtcNow.AddMinutes(_jwt.ExpirationMinutes);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, request.Username),
            new(ClaimTypes.Role, "Admin")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new LoginResponse
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAtUtc = expiresAt
        };
    }
}
