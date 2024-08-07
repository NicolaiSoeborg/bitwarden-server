﻿using System.ComponentModel.DataAnnotations;
using Bit.Core;
using Bit.Core.Auth.Models.Api.Request.Accounts;
using Bit.Core.Auth.Models.Business.Tokenables;
using Bit.Core.Entities;
using Bit.Core.Enums;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Tokens;
using Bit.Identity.Models.Request.Accounts;
using Bit.IntegrationTestCommon.Factories;
using Bit.Test.Common.AutoFixture.Attributes;

using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Bit.Identity.IntegrationTest.Controllers;

public class AccountsControllerTests : IClassFixture<IdentityApplicationFactory>
{
    private readonly IdentityApplicationFactory _factory;

    public AccountsControllerTests(IdentityApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostRegister_Success()
    {
        var context = await _factory.RegisterAsync(new RegisterRequestModel
        {
            Email = "test+register@email.com",
            MasterPasswordHash = "master_password_hash"
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        var database = _factory.GetDatabaseContext();
        var user = await database.Users
            .SingleAsync(u => u.Email == "test+register@email.com");

        Assert.NotNull(user);
    }

    [Theory]
    [BitAutoData("invalidEmail")]
    [BitAutoData("")]
    public async Task PostRegisterSendEmailVerification_InvalidRequestModel_ThrowsBadRequestException(string email, string name, bool receiveMarketingEmails)
    {

        var model = new RegisterSendVerificationEmailRequestModel
        {
            Email = email,
            Name = name,
            ReceiveMarketingEmails = receiveMarketingEmails
        };

        var context = await _factory.PostRegisterSendEmailVerificationAsync(model);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Theory]
    [BitAutoData(true)]
    [BitAutoData(false)]
    public async Task PostRegisterSendEmailVerification_WhenGivenNewOrExistingUser__WithEnableEmailVerificationTrue_ReturnsNoContent(bool shouldPreCreateUser, string name, bool receiveMarketingEmails)
    {
        var email = $"test+register+{name}@email.com";
        if (shouldPreCreateUser)
        {
            await CreateUserAsync(email, name);
        }

        var model = new RegisterSendVerificationEmailRequestModel
        {
            Email = email,
            Name = name,
            ReceiveMarketingEmails = receiveMarketingEmails
        };

        var context = await _factory.PostRegisterSendEmailVerificationAsync(model);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }


    [Theory]
    [BitAutoData(true)]
    [BitAutoData(false)]
    public async Task PostRegisterSendEmailVerification_WhenGivenNewOrExistingUser_WithEnableEmailVerificationFalse_ReturnsNoContent(bool shouldPreCreateUser, string name, bool receiveMarketingEmails)
    {

        // Localize substitutions to this test.
        var localFactory = new IdentityApplicationFactory();
        localFactory.UpdateConfiguration("globalSettings:enableEmailVerification", "false");

        var email = $"test+register+{name}@email.com";
        if (shouldPreCreateUser)
        {
            await CreateUserAsync(email, name, localFactory);
        }

        var model = new RegisterSendVerificationEmailRequestModel
        {
            Email = email,
            Name = name,
            ReceiveMarketingEmails = receiveMarketingEmails
        };

        var context = await localFactory.PostRegisterSendEmailVerificationAsync(model);

        if (shouldPreCreateUser)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
            var body = await context.ReadBodyAsStringAsync();
            Assert.Contains($"Email {email} is already taken", body);
        }
        else
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            var body = await context.ReadBodyAsStringAsync();
            Assert.NotNull(body);
            Assert.StartsWith("BwRegistrationEmailVerificationToken_", body);
        }
    }

    [Theory, BitAutoData]
    public async Task RegistrationWithEmailVerification_WithEmailVerificationToken_Succeeds([Required] string name, bool receiveMarketingEmails,
         [StringLength(1000), Required] string masterPasswordHash, [StringLength(50)] string masterPasswordHint, [Required] string userSymmetricKey,
         [Required] KeysRequestModel userAsymmetricKeys, int kdfMemory, int kdfParallelism)
    {
        // Localize substitutions to this test.
        var localFactory = new IdentityApplicationFactory();

        // First we must substitute the mail service in order to be able to get a valid email verification token
        // for the complete registration step
        string capturedEmailVerificationToken = null;
        localFactory.SubstituteService<IMailService>(mailService =>
        {
            mailService.SendRegistrationVerificationEmailAsync(Arg.Any<string>(), Arg.Do<string>(t => capturedEmailVerificationToken = t))
                .Returns(Task.CompletedTask);

        });

        // we must first call the send verification email endpoint to trigger the first part of the process
        var email = $"test+register+{name}@email.com";
        var sendVerificationEmailReqModel = new RegisterSendVerificationEmailRequestModel
        {
            Email = email,
            Name = name,
            ReceiveMarketingEmails = receiveMarketingEmails
        };

        var sendEmailVerificationResponseHttpContext = await localFactory.PostRegisterSendEmailVerificationAsync(sendVerificationEmailReqModel);

        Assert.Equal(StatusCodes.Status204NoContent, sendEmailVerificationResponseHttpContext.Response.StatusCode);
        Assert.NotNull(capturedEmailVerificationToken);

        // Now we call the finish registration endpoint with the email verification token
        var registerFinishReqModel = new RegisterFinishRequestModel
        {
            Email = email,
            MasterPasswordHash = masterPasswordHash,
            MasterPasswordHint = masterPasswordHint,
            EmailVerificationToken = capturedEmailVerificationToken,
            Kdf = KdfType.PBKDF2_SHA256,
            KdfIterations = AuthConstants.PBKDF2_ITERATIONS.Default,
            UserSymmetricKey = userSymmetricKey,
            UserAsymmetricKeys = userAsymmetricKeys,
            KdfMemory = kdfMemory,
            KdfParallelism = kdfParallelism
        };

        var postRegisterFinishHttpContext = await localFactory.PostRegisterFinishAsync(registerFinishReqModel);

        Assert.Equal(StatusCodes.Status200OK, postRegisterFinishHttpContext.Response.StatusCode);

        var database = localFactory.GetDatabaseContext();
        var user = await database.Users
            .SingleAsync(u => u.Email == email);

        Assert.NotNull(user);

        // Assert user properties match the request model
        Assert.Equal(email, user.Email);
        Assert.Equal(name, user.Name);
        Assert.NotEqual(masterPasswordHash, user.MasterPassword);  // We execute server side hashing
        Assert.NotNull(user.MasterPassword);
        Assert.Equal(masterPasswordHint, user.MasterPasswordHint);
        Assert.Equal(userSymmetricKey, user.Key);
        Assert.Equal(userAsymmetricKeys.EncryptedPrivateKey, user.PrivateKey);
        Assert.Equal(userAsymmetricKeys.PublicKey, user.PublicKey);
        Assert.Equal(KdfType.PBKDF2_SHA256, user.Kdf);
        Assert.Equal(AuthConstants.PBKDF2_ITERATIONS.Default, user.KdfIterations);
        Assert.Equal(kdfMemory, user.KdfMemory);
        Assert.Equal(kdfParallelism, user.KdfParallelism);
    }

    [Theory, BitAutoData]
    public async Task RegistrationWithEmailVerification_WithOrgInviteToken_Succeeds(
         [StringLength(1000)] string masterPasswordHash, [StringLength(50)] string masterPasswordHint, string userSymmetricKey,
        KeysRequestModel userAsymmetricKeys, int kdfMemory, int kdfParallelism)
    {

        // Localize factory to just this test.
        var localFactory = new IdentityApplicationFactory();

        // To avoid having to call the API send org invite endpoint, I'm going to hardcode some valid org invite data:
        var email = "jsnider+local410@bitwarden.com";
        var orgInviteToken = "BwOrgUserInviteToken_CfDJ8HOzu6wr6nVLouuDxgOHsMwPcj9Guuip5k_XLD1bBGpwQS1f66c9kB6X4rvKGxNdywhgimzgvG9SgLwwJU70O8P879XyP94W6kSoT4N25a73kgW3nU3vl3fAtGSS52xdBjNU8o4sxmomRvhOZIQ0jwtVjdMC2IdybTbxwCZhvN0hKIFs265k6wFRSym1eu4NjjZ8pmnMneG0PlKnNZL93tDe8FMcqStJXoddIEgbA99VJp8z1LQmOMfEdoMEM7Zs8W5bZ34N4YEGu8XCrVau59kGtWQk7N4rPV5okzQbTpeoY_4FeywgLFGm-tDtTPEdSEBJkRjexANri7CGdg3dpnMifQc_bTmjZd32gOjw8N8v";
        var orgUserId = new Guid("5e45fbdc-a080-4a77-93ff-b19c0161e81e");

        var orgUser = new OrganizationUser { Id = orgUserId, Email = email };

        var orgInviteTokenable = new OrgUserInviteTokenable(orgUser)
        {
            ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromHours(5))
        };

        localFactory.SubstituteService<IDataProtectorTokenFactory<OrgUserInviteTokenable>>(orgInviteTokenDataProtectorFactory =>
        {
            orgInviteTokenDataProtectorFactory.TryUnprotect(Arg.Is(orgInviteToken), out Arg.Any<OrgUserInviteTokenable>())
                .Returns(callInfo =>
                {
                    callInfo[1] = orgInviteTokenable;
                    return true;
                });
        });

        var registerFinishReqModel = new RegisterFinishRequestModel
        {
            Email = email,
            MasterPasswordHash = masterPasswordHash,
            MasterPasswordHint = masterPasswordHint,
            OrgInviteToken = orgInviteToken,
            OrganizationUserId = orgUserId,
            Kdf = KdfType.PBKDF2_SHA256,
            KdfIterations = AuthConstants.PBKDF2_ITERATIONS.Default,
            UserSymmetricKey = userSymmetricKey,
            UserAsymmetricKeys = userAsymmetricKeys,
            KdfMemory = kdfMemory,
            KdfParallelism = kdfParallelism
        };

        var postRegisterFinishHttpContext = await localFactory.PostRegisterFinishAsync(registerFinishReqModel);

        Assert.Equal(StatusCodes.Status200OK, postRegisterFinishHttpContext.Response.StatusCode);

        var database = localFactory.GetDatabaseContext();
        var user = await database.Users
            .SingleAsync(u => u.Email == email);

        Assert.NotNull(user);

        // Assert user properties match the request model
        Assert.Equal(email, user.Email);
        Assert.NotEqual(masterPasswordHash, user.MasterPassword);  // We execute server side hashing
        Assert.NotNull(user.MasterPassword);
        Assert.Equal(masterPasswordHint, user.MasterPasswordHint);
        Assert.Equal(userSymmetricKey, user.Key);
        Assert.Equal(userAsymmetricKeys.EncryptedPrivateKey, user.PrivateKey);
        Assert.Equal(userAsymmetricKeys.PublicKey, user.PublicKey);
        Assert.Equal(KdfType.PBKDF2_SHA256, user.Kdf);
        Assert.Equal(AuthConstants.PBKDF2_ITERATIONS.Default, user.KdfIterations);
        Assert.Equal(kdfMemory, user.KdfMemory);
        Assert.Equal(kdfParallelism, user.KdfParallelism);
    }

    private async Task<User> CreateUserAsync(string email, string name, IdentityApplicationFactory factory = null)
    {
        var factoryToUse = factory ?? _factory;

        var userRepository = factoryToUse.Services.GetRequiredService<IUserRepository>();

        var user = new User
        {
            Email = email,
            Id = Guid.NewGuid(),
            Name = name,
            SecurityStamp = Guid.NewGuid().ToString(),
            ApiKey = "test_api_key",
        };

        await userRepository.CreateAsync(user);

        return user;
    }
}
