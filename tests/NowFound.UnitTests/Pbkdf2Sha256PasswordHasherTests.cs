using Microsoft.AspNetCore.Identity;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class Pbkdf2Sha256PasswordHasherTests
{
    private readonly Pbkdf2Sha256PasswordHasher _hasher = new();
    private readonly AppUser _user = new();

    [Fact]
    public void Verify_CorrectPassword_Succeeds()
    {
        var hash = _hasher.HashPassword(_user, "password123!");

        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(_user, hash, "password123!"));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hash = _hasher.HashPassword(_user, "password123!");

        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(_user, hash, "wrong"));
    }

    [Fact]
    public void Hash_SamePassword_ProducesDifferentHashesBySalt()
    {
        Assert.NotEqual(_hasher.HashPassword(_user, "abc12345"), _hasher.HashPassword(_user, "abc12345"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("0.AAAA.AAAA")]
    public void Verify_MalformedHash_Fails(string hash)
    {
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(_user, hash, "x"));
    }

    [Fact]
    public void Verify_LowerIterationHash_RequestsRehash()
    {
        var salt = new byte[16];
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "pw", salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var hash = $"1000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, _hasher.VerifyHashedPassword(_user, hash, "pw"));
    }
}
