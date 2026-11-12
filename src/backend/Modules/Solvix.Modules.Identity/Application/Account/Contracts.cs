namespace Solvix.Modules.Identity.Application.Account;

public sealed record VerifyEmailRequest(string Token);

public sealed record EmailRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Password rules to show next to a "new password" field.</summary>
public sealed record PasswordRulesDto(
    int MinLength,
    int MaxLength,
    bool RequireUppercase,
    bool RequireLowercase,
    bool RequireDigit,
    bool RequireSymbol,
    int HistoryCount);

public sealed record MfaStatusDto(bool Enabled, DateTimeOffset? EnabledAtUtc, int RecoveryCodesRemaining, bool RequiredByWorkspace);

/// <summary>Returned once when enrolment starts; the secret is never readable again.</summary>
public sealed record MfaSetupDto(string Secret, string OtpAuthUri);

public sealed record PasswordConfirmationRequest(string Password);

public sealed record MfaConfirmRequest(string Code);

public sealed record MfaProtectedRequest(string Password, string Code);

/// <summary>Recovery codes are shown exactly once, when they are generated.</summary>
public sealed record RecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);
