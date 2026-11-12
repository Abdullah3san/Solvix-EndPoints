namespace Solvix.SharedKernel.Web;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Base for all versioned API controllers. Routes are shaped as
/// <c>/api/v{version}/{module}/...</c>. Every controller is authenticated and
/// tenant-scoped by default; anonymous endpoints opt out explicitly.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.TenantMember)]
[Produces("application/json")]
[Route("api/v{version:apiVersion}/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
}
