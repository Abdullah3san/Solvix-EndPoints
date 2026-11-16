namespace Solvix.Modules.Reporting.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Reporting.Application;
using Solvix.SharedKernel.Web;

/// <summary>
/// Operational reports for workspace administrators. Read-only; every query runs inside the caller's tenant
/// session so row-level security limits it to the caller's own data. CSV exports neutralise spreadsheet formulas.
/// </summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/reports")]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class ReportsController(IReportService reports) : ApiControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ReportInfo>> List() => Ok(reports.List());

    [HttpGet("{key}")]
    public async Task<IActionResult> Run(string key, [FromQuery] Guid? branchId, [FromQuery] int? days, [FromQuery] string? search, [FromQuery] string? format, CancellationToken cancellationToken)
    {
        var result = await reports.RunAsync(key, new ReportQuery(branchId, days, search?.Length > 100 ? search[..100] : search), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(CsvExporter.ToCsv(result))).ToArray();
            return File(bytes, "text/csv; charset=utf-8", $"{result.Report}-{result.GeneratedAtUtc:yyyyMMdd-HHmm}.csv");
        }

        return Ok(result);
    }
}
