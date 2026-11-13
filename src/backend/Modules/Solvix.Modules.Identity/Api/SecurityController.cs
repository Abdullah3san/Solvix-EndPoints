namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>The tenant's security policy and its authentication audit trail.</summary>
[ApiVersion(1.0)]
public sealed class SecurityController(ISecurityPolicyService policies, IAuditQueryService auditLog) : ApiControllerBase
{
    [HttpGet("policy")]
    [RequirePermission(Permissions.Security.View)]
    [ProducesResponseType<SecurityPolicyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SecurityPolicyDto>> GetPolicy(CancellationToken cancellationToken) =>
        Ok(await policies.GetAsync(cancellationToken));

    /// <summary>Changes password rules, session lifetimes and the MFA requirement. Can only tighten the platform baseline.</summary>
    [HttpPut("policy")]
    [RequirePermission(Permissions.Security.Manage)]
    [ProducesResponseType<SecurityPolicyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SecurityPolicyDto>> UpdatePolicy([FromBody] SecurityPolicySettings settings, CancellationToken cancellationToken) =>
        Ok(await policies.UpdateAsync(settings, cancellationToken));

    /// <summary>Authentication and access events of this workspace, newest first.</summary>
    [HttpGet("audit-events")]
    [RequirePermission(Permissions.Audit.View)]
    [ProducesResponseType<PagedResult<AuditEventDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AuditEventDto>>> AuditEvents([FromQuery] AuditQuery query, CancellationToken cancellationToken) =>
        Ok(await auditLog.ListAsync(query, cancellationToken));
}
