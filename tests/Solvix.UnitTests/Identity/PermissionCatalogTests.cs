namespace Solvix.UnitTests.Identity;

using System.Reflection;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void Catalog_contains_every_declared_permission_constant_exactly_once()
    {
        var constants = typeof(Permissions).GetNestedTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(constants.Count, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(constants.Order(StringComparer.Ordinal), PermissionCatalog.AllKeys.Order(StringComparer.Ordinal));
        Assert.All(PermissionCatalog.All, p => Assert.Matches("^[a-z]+(\\.[a-z_]+)+$", p.Key));
    }

    [Theory]
    [InlineData("devices.view")]
    [InlineData("devices.enroll")]
    [InlineData("devices.manage")]
    [InlineData("assets.view")]
    [InlineData("assets.create")]
    [InlineData("assets.edit")]
    [InlineData("assets.transfer")]
    [InlineData("mdm.manage")]
    [InlineData("users.manage")]
    [InlineData("security.manage")]
    [InlineData("reports.view")]
    [InlineData("billing.manage")]
    [InlineData("remote.view")]
    [InlineData("remote.desktop")]
    [InlineData("remote.desktop.control")]
    [InlineData("remote.multimonitor")]
    [InlineData("remote.terminal")]
    [InlineData("remote.powershell")]
    [InlineData("remote.execute_approved_commands")]
    [InlineData("remote.file.upload")]
    [InlineData("remote.file.download")]
    [InlineData("remote.restart")]
    [InlineData("remote.shutdown")]
    [InlineData("remote.lock")]
    [InlineData("remote.voice_call")]
    [InlineData("remote.sessions.view")]
    [InlineData("remote.sessions.terminate")]
    [InlineData("remote.approvals.manage")]
    public void Catalog_contains_the_permissions_named_in_the_specification(string key)
    {
        Assert.True(PermissionCatalog.IsKnown(key));
    }

    [Fact]
    public void Expand_adds_implied_permissions_and_ignores_unknown_keys()
    {
        Assert.Equal(
            new[] { "users.manage", "users.view" },
            PermissionCatalog.Expand(["users.manage"]).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { "devices.view", "mdm.manage" },
            PermissionCatalog.Expand(["mdm.manage"]).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "reports.view" }, PermissionCatalog.Expand(["reports.view", "admin.everything", "*"]));
        Assert.All(PermissionCatalog.All, p => Assert.All(p.Implies, i => Assert.True(PermissionCatalog.IsKnown(i))));
    }

    [Fact]
    public void Unknown_permission_cannot_be_used_to_protect_an_endpoint()
    {
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute("devices.everything"));
        Assert.Equal("perm:devices.view", new RequirePermissionAttribute(Permissions.Devices.View).Policy);
    }

    [Fact]
    public void The_seven_default_roles_exist_with_unique_keys_and_names()
    {
        Assert.Equal(
            new[]
            {
                "Super Administrator", "Tenant Administrator", "IT Manager", "IT Technician",
                "Asset Manager", "Security Administrator", "Read-Only Auditor",
            },
            SystemRoles.All.Select(r => r.Name));
        Assert.Equal(7, SystemRoles.All.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(SystemRoles.All, r =>
        {
            Assert.NotEmpty(r.Permissions);
            Assert.All(r.Permissions, p => Assert.True(PermissionCatalog.IsKnown(p)));

            // Closed under implication: holding "manage" always includes the matching "view".
            Assert.Equal(PermissionCatalog.Expand(r.Permissions).Order(StringComparer.Ordinal), r.Permissions.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void Role_permission_sets_reflect_their_duties()
    {
        Assert.Equal(PermissionCatalog.AllKeys.Order(StringComparer.Ordinal), SystemRoles.SuperAdministrator.Permissions.Order(StringComparer.Ordinal));

        var tenantAdmin = SystemRoles.TenantAdministrator.Permissions;
        Assert.DoesNotContain(Permissions.Billing.Manage, tenantAdmin);
        Assert.DoesNotContain(Permissions.Workspace.Manage, tenantAdmin);
        Assert.Contains(Permissions.Users.Manage, tenantAdmin);
        Assert.Contains(Permissions.Roles.Manage, tenantAdmin);
        Assert.Contains(Permissions.Security.Manage, tenantAdmin);

        // Only the two administrator roles can change who may do what.
        Assert.All(
            SystemRoles.All.Where(r => r.Key is not (SystemRoles.SuperAdministratorKey or SystemRoles.TenantAdministratorKey)),
            r =>
            {
                Assert.DoesNotContain(Permissions.Users.Manage, r.Permissions);
                Assert.DoesNotContain(Permissions.Roles.Manage, r.Permissions);
                Assert.DoesNotContain(Permissions.Billing.Manage, r.Permissions);
            });

        // The technician supports users remotely but cannot shut down, approve, run ad-hoc PowerShell or retire devices.
        Assert.Contains(Permissions.Remote.DesktopControl, SystemRoles.ItTechnician.Permissions);
        Assert.Contains(Permissions.Remote.ExecuteApprovedCommands, SystemRoles.ItTechnician.Permissions);
        Assert.DoesNotContain(Permissions.Remote.Shutdown, SystemRoles.ItTechnician.Permissions);
        Assert.DoesNotContain(Permissions.Remote.ApprovalsManage, SystemRoles.ItTechnician.Permissions);
        Assert.DoesNotContain(Permissions.Devices.Manage, SystemRoles.ItTechnician.Permissions);
        Assert.Contains(Permissions.Remote.Shutdown, SystemRoles.ItManager.Permissions);

        // Ad-hoc PowerShell is never part of a built-in role below the tenant administrators.
        Assert.All(
            SystemRoles.All.Where(r => r.Key is not (SystemRoles.SuperAdministratorKey or SystemRoles.TenantAdministratorKey)),
            r => Assert.DoesNotContain(Permissions.Remote.PowerShell, r.Permissions));

        // Every remote permission implies remote.view and devices.view.
        Assert.All(
            PermissionCatalog.AllKeys.Where(k => k.StartsWith("remote.", StringComparison.Ordinal)),
            k => Assert.Superset(new HashSet<string> { Permissions.Remote.View, Permissions.Devices.View }, PermissionCatalog.Expand([k]).ToHashSet()));

        // Security administration is separated from user administration.
        Assert.Contains(Permissions.Security.Manage, SystemRoles.SecurityAdministrator.Permissions);
        Assert.Contains(Permissions.Audit.View, SystemRoles.SecurityAdministrator.Permissions);

        // The auditor can read and nothing else.
        Assert.All(SystemRoles.ReadOnlyAuditor.Permissions, p => Assert.EndsWith(".view", p, StringComparison.Ordinal));
        Assert.Contains(Permissions.Audit.View, SystemRoles.ReadOnlyAuditor.Permissions);
    }
}
