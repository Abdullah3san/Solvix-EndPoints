namespace Solvix.Modules.Helpdesk.Application;

using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Gives every new workspace a working helpdesk: a default 24x7 SLA policy and the common categories.</summary>
internal sealed class DefaultsProvisioner(ISolvixDbContext db) : ITenantProvisioningStep
{
    public static readonly string[] Categories = ["Hardware", "Software", "Network", "Access & Accounts", "Security", "Other"];

    public Task ProvisionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        db.Set<SlaPolicy>().Add(SlaPolicy.Create(
            tenantId, "Standard", isDefault: true, businessHoursOnly: false, "UTC", 540, 1020, 0b0111110,
            [
                (TicketPriority.Critical, 30, 4 * 60),
                (TicketPriority.High, 2 * 60, 8 * 60),
                (TicketPriority.Medium, 8 * 60, 3 * 24 * 60),
                (TicketPriority.Low, 24 * 60, 7 * 24 * 60),
            ]));
        foreach (var name in Categories)
        {
            db.Set<TicketCategory>().Add(TicketCategory.Create(tenantId, name, null, null, null));
        }

        return Task.CompletedTask;
    }
}
