namespace Solvix.Modules.Devices.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Monitoring;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>
/// Real-time endpoint monitoring. Live changes are pushed over SignalR at
/// <c>/hubs/monitoring</c> (method <c>monitoringUpdate</c>); these endpoints
/// provide the snapshot to start from and the history.
/// </summary>
[ApiVersion(1.0)]
public sealed class MonitoringController(IMonitoringQueries queries, IMonitoringAdministration administration) : ApiControllerBase
{
    /// <summary>Dashboard: counts by state, branch and OS, signals, availability, recent events.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<MonitoringSummaryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MonitoringSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await queries.GetSummaryAsync(cancellationToken));

    /// <summary>The three independent signals (agent connectivity, network, OS health), uptime/downtime and history of one device.</summary>
    [HttpGet("devices/{deviceId:guid}")]
    [ProducesResponseType<DeviceMonitoringDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceMonitoringDto>> Device(Guid deviceId, CancellationToken cancellationToken) =>
        Ok(await queries.GetDeviceAsync(deviceId, cancellationToken));

    /// <summary>Connection history.</summary>
    [HttpGet("events")]
    [ProducesResponseType<PagedResult<ConnectionEventDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ConnectionEventDto>>> Events([FromQuery] ConnectionEventQuery query, CancellationToken cancellationToken) =>
        Ok(await queries.ListEventsAsync(query, cancellationToken));

    /// <summary>Device availability report (UTC days, at most 366).</summary>
    [HttpGet("availability")]
    [ProducesResponseType<AvailabilityReportDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AvailabilityReportDto>> Availability([FromQuery] AvailabilityReportQuery query, CancellationToken cancellationToken) =>
        Ok(await queries.GetAvailabilityAsync(query, cancellationToken));

    [HttpGet("availability/export")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportAvailability([FromQuery] AvailabilityReportQuery query, CancellationToken cancellationToken)
    {
        var csv = await queries.ExportAvailabilityCsvAsync(query, cancellationToken);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", "device-availability.csv");
    }

    [HttpGet("alerts")]
    [ProducesResponseType<PagedResult<MonitoringAlertDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MonitoringAlertDto>>> Alerts([FromQuery] AlertQuery query, CancellationToken cancellationToken) =>
        Ok(await administration.ListAlertsAsync(query, cancellationToken));

    [HttpPost("alerts/{alertId:guid}/acknowledge")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<MonitoringAlertDto>> Acknowledge(Guid alertId, CancellationToken cancellationToken) =>
        Ok(await administration.AcknowledgeAlertAsync(alertId, cancellationToken));

    [HttpGet("settings")]
    [ProducesResponseType<MonitoringSettingsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MonitoringSettingsDto>> Settings(CancellationToken cancellationToken) =>
        Ok(await administration.GetSettingsAsync(cancellationToken));

    /// <summary>Tenant administrators configure thresholds, heartbeat interval and notifications.</summary>
    [HttpPut("settings")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<MonitoringSettingsDto>> UpdateSettings([FromBody] UpdateMonitoringSettingsRequest request, CancellationToken cancellationToken) =>
        Ok(await administration.UpdateSettingsAsync(request, cancellationToken));
}
