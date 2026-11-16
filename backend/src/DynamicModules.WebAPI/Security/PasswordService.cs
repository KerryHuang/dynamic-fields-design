using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.AspNetCore.Identity;

namespace DynamicModules.WebAPI.Security;

/// <summary>框架內建的 PasswordHasher（PBKDF2）；雜湊本身不綁使用者，AppUser 只是它的型別參數。</summary>
public class PasswordService : IPasswordService
{
    private readonly PasswordHasher<AppUser> _hasher = new();
    private static readonly AppUser NoUser = new();

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(NoUser, hash, password) != PasswordVerificationResult.Failed;
}
