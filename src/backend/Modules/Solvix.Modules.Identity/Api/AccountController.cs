namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Mfa;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Web;

/// <summary>The caller's own credentials: e-mail verification, password and multi-factor authentication.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/auth")]
public sealed class AccountController(IAccountService account, IMfaService mfa) : ApiControllerBase
{
    [HttpPost("verify-email")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Credentials)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        await account.VerifyEmailAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>Always answers 202, whether or not the address belongs to an unverified account.</summary>
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Email)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendVerification([FromBody] EmailRequest request, CancellationToken cancellationToken)
    {
        await account.ResendVerificationAsync(request, cancellationToken);
        return Accepted();
    }

    /// <summary>Always answers 202, whether or not the address belongs to an account.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Email)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword([FromBody] EmailRequest request, CancellationToken cancellationToken)
    {
        await account.RequestPasswordResetAsync(request, cancellationToken);
        return Accepted();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Credentials)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await account.ResetPasswordAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>The platform's baseline password rules, for the sign-up form.</summary>
    [HttpGet("password-rules")]
    [AllowAnonymous]
    [ProducesResponseType<PasswordRulesDto>(StatusCodes.Status200OK)]
    public ActionResult<PasswordRulesDto> PasswordRules() => Ok(account.GetBaselinePasswordRules());

    /// <summary>The rules the caller's next password must satisfy (strictest across their workspaces).</summary>
    [HttpGet("me/password-rules")]
    [AuthenticatedOnly]
    [ProducesResponseType<PasswordRulesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PasswordRulesDto>> MyPasswordRules(CancellationToken cancellationToken) =>
        Ok(await account.GetMyPasswordRulesAsync(cancellationToken));

    [HttpPost("change-password")]
    [AuthenticatedOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await account.ChangePasswordAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpGet("mfa")]
    [AuthenticatedOnly]
    [ProducesResponseType<MfaStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MfaStatusDto>> MfaStatus(CancellationToken cancellationToken) =>
        Ok(await mfa.GetStatusAsync(cancellationToken));

    /// <summary>Starts authenticator enrolment. The secret in the response is shown this one time only.</summary>
    [HttpPost("mfa/totp/setup")]
    [AuthenticatedOnly]
    [ProducesResponseType<MfaSetupDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MfaSetupDto>> BeginMfaSetup([FromBody] PasswordConfirmationRequest request, CancellationToken cancellationToken) =>
        Ok(await mfa.BeginSetupAsync(request, cancellationToken));

    /// <summary>Turns MFA on once the authenticator produces a valid code. Returns the recovery codes, once.</summary>
    [HttpPost("mfa/totp/confirm")]
    [AuthenticatedOnly]
    [ProducesResponseType<RecoveryCodesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RecoveryCodesDto>> ConfirmMfaSetup([FromBody] MfaConfirmRequest request, CancellationToken cancellationToken) =>
        Ok(await mfa.ConfirmSetupAsync(request, cancellationToken));

    [HttpPost("mfa/disable")]
    [AuthenticatedOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DisableMfa([FromBody] MfaProtectedRequest request, CancellationToken cancellationToken)
    {
        await mfa.DisableAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("mfa/recovery-codes")]
    [AuthenticatedOnly]
    [ProducesResponseType<RecoveryCodesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RecoveryCodesDto>> RegenerateRecoveryCodes([FromBody] MfaProtectedRequest request, CancellationToken cancellationToken) =>
        Ok(await mfa.RegenerateRecoveryCodesAsync(request, cancellationToken));
}
