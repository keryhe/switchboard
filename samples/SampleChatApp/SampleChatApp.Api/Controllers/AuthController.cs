using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace SampleChatApp.Api.Controllers;

/// <summary>
/// Dev-only login: issues a user JWT with no password check, purely so the sample has something
/// for <see cref="Hubs.ChatHub"/>'s [Authorize] and Context.UserIdentifier to work against. A real
/// app would authenticate against its own identity store here.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(IConfiguration configuration) : ControllerBase
{
    public sealed record LoginRequest(string Username);

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest("Username is required.");
        }

        var key = configuration["Auth:UserTokenSigningKey"]!;
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "sample-chat-app",
            audience: "sample-chat-app-user",
            claims: [new Claim(ClaimTypes.NameIdentifier, request.Username)],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);

        return Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token) });
    }
}
