﻿using Bit.Core;
using Bit.Core.Auth.Models.Api.Request.Accounts;
using Bit.Core.Auth.Models.Business.Tokenables;
using Bit.Core.Auth.Services;
using Bit.Core.Auth.UserFeatures.Registration;
using Bit.Core.Auth.UserFeatures.WebAuthnLogin;
using Bit.Core.Context;
using Bit.Core.Entities;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Models.Data;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Tokens;
using Bit.Core.Tools.Enums;
using Bit.Core.Tools.Models.Business;
using Bit.Core.Tools.Services;
using Bit.Identity.Controllers;
using Bit.Identity.Models.Request.Accounts;
using Bit.Test.Common.AutoFixture.Attributes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Xunit;

namespace Bit.Identity.Test.Controllers;

public class AccountsControllerTests : IDisposable
{

    private readonly AccountsController _sut;
    private readonly ICurrentContext _currentContext;
    private readonly ILogger<AccountsController> _logger;
    private readonly IUserRepository _userRepository;
    private readonly IUserService _userService;
    private readonly ICaptchaValidationService _captchaValidationService;
    private readonly IDataProtectorTokenFactory<WebAuthnLoginAssertionOptionsTokenable> _assertionOptionsDataProtector;
    private readonly IGetWebAuthnLoginCredentialAssertionOptionsCommand _getWebAuthnLoginCredentialAssertionOptionsCommand;
    private readonly ISendVerificationEmailForRegistrationCommand _sendVerificationEmailForRegistrationCommand;
    private readonly IReferenceEventService _referenceEventService;

    public AccountsControllerTests()
    {
        _currentContext = Substitute.For<ICurrentContext>();
        _logger = Substitute.For<ILogger<AccountsController>>();
        _userRepository = Substitute.For<IUserRepository>();
        _userService = Substitute.For<IUserService>();
        _captchaValidationService = Substitute.For<ICaptchaValidationService>();
        _assertionOptionsDataProtector = Substitute.For<IDataProtectorTokenFactory<WebAuthnLoginAssertionOptionsTokenable>>();
        _getWebAuthnLoginCredentialAssertionOptionsCommand = Substitute.For<IGetWebAuthnLoginCredentialAssertionOptionsCommand>();
        _sendVerificationEmailForRegistrationCommand = Substitute.For<ISendVerificationEmailForRegistrationCommand>();
        _referenceEventService = Substitute.For<IReferenceEventService>();
        _sut = new AccountsController(
            _currentContext,
            _logger,
            _userRepository,
            _userService,
            _captchaValidationService,
            _assertionOptionsDataProtector,
            _getWebAuthnLoginCredentialAssertionOptionsCommand,
            _sendVerificationEmailForRegistrationCommand,
            _referenceEventService
        );
    }

    public void Dispose()
    {
        _sut?.Dispose();
    }

    [Fact]
    public async Task PostPrelogin_WhenUserExists_ShouldReturnUserKdfInfo()
    {
        var userKdfInfo = new UserKdfInformation
        {
            Kdf = KdfType.PBKDF2_SHA256,
            KdfIterations = AuthConstants.PBKDF2_ITERATIONS.Default
        };
        _userRepository.GetKdfInformationByEmailAsync(Arg.Any<string>()).Returns(Task.FromResult(userKdfInfo));

        var response = await _sut.PostPrelogin(new PreloginRequestModel { Email = "user@example.com" });

        Assert.Equal(userKdfInfo.Kdf, response.Kdf);
        Assert.Equal(userKdfInfo.KdfIterations, response.KdfIterations);
    }

    [Fact]
    public async Task PostPrelogin_WhenUserDoesNotExist_ShouldDefaultToPBKDF()
    {
        _userRepository.GetKdfInformationByEmailAsync(Arg.Any<string>()).Returns(Task.FromResult<UserKdfInformation>(null!));

        var response = await _sut.PostPrelogin(new PreloginRequestModel { Email = "user@example.com" });

        Assert.Equal(KdfType.PBKDF2_SHA256, response.Kdf);
        Assert.Equal(AuthConstants.PBKDF2_ITERATIONS.Default, response.KdfIterations);
    }

    [Fact]
    public async Task PostRegister_ShouldRegisterUser()
    {
        var passwordHash = "abcdef";
        var token = "123456";
        var userGuid = new Guid();
        _userService.RegisterUserAsync(Arg.Any<User>(), passwordHash, token, userGuid)
                    .Returns(Task.FromResult(IdentityResult.Success));
        var request = new RegisterRequestModel
        {
            Name = "Example User",
            Email = "user@example.com",
            MasterPasswordHash = passwordHash,
            MasterPasswordHint = "example",
            Token = token,
            OrganizationUserId = userGuid
        };

        await _sut.PostRegister(request);

        await _userService.Received(1).RegisterUserAsync(Arg.Any<User>(), passwordHash, token, userGuid);
    }

    [Fact]
    public async Task PostRegister_WhenUserServiceFails_ShouldThrowBadRequestException()
    {
        var passwordHash = "abcdef";
        var token = "123456";
        var userGuid = new Guid();
        _userService.RegisterUserAsync(Arg.Any<User>(), passwordHash, token, userGuid)
                    .Returns(Task.FromResult(IdentityResult.Failed()));
        var request = new RegisterRequestModel
        {
            Name = "Example User",
            Email = "user@example.com",
            MasterPasswordHash = passwordHash,
            MasterPasswordHint = "example",
            Token = token,
            OrganizationUserId = userGuid
        };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PostRegister(request));
    }

    [Theory]
    [BitAutoData]
    public async Task PostRegisterSendEmailVerification_WhenTokenReturnedFromCommand_Returns200WithToken(string email, string name, bool receiveMarketingEmails)
    {
        // Arrange
        var model = new RegisterSendVerificationEmailRequestModel
        {
            Email = email,
            Name = name,
            ReceiveMarketingEmails = receiveMarketingEmails
        };

        var token = "fakeToken";

        _sendVerificationEmailForRegistrationCommand.Run(email, name, receiveMarketingEmails).Returns(token);

        // Act
        var result = await _sut.PostRegisterSendVerificationEmail(model);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);
        Assert.Equal(token, okResult.Value);

        await _referenceEventService.Received(1).RaiseEventAsync(Arg.Is<ReferenceEvent>(e => e.Type == ReferenceEventType.SignupEmailSubmit));
    }

    [Theory]
    [BitAutoData]
    public async Task PostRegisterSendEmailVerification_WhenNoTokenIsReturnedFromCommand_Returns204NoContent(string email, string name, bool receiveMarketingEmails)
    {
        // Arrange
        var model = new RegisterSendVerificationEmailRequestModel
        {
            Email = email,
            Name = name,
            ReceiveMarketingEmails = receiveMarketingEmails
        };

        _sendVerificationEmailForRegistrationCommand.Run(email, name, receiveMarketingEmails).ReturnsNull();

        // Act
        var result = await _sut.PostRegisterSendVerificationEmail(model);

        // Assert
        var noContentResult = Assert.IsType<NoContentResult>(result);
        Assert.Equal(204, noContentResult.StatusCode);
        await _referenceEventService.Received(1).RaiseEventAsync(Arg.Is<ReferenceEvent>(e => e.Type == ReferenceEventType.SignupEmailSubmit));
    }
}
