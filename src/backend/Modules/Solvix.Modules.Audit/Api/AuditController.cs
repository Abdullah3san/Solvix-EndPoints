namespace Solvix.Modules.Audit.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Audit.Application;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Read-only access to the tenant's append-only audit history.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class AuditController(IAuditQueryService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditEntryDto>>> List([FromQuery] AuditQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));
}
