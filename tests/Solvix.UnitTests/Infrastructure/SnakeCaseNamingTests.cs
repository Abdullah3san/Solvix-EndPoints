namespace Solvix.UnitTests.Infrastructure;

using Solvix.Infrastructure.Persistence.Conventions;

public sealed class SnakeCaseNamingTests
{
    [Theory]
    [InlineData("TenantId", "tenant_id")]
    [InlineData("CreatedAtUtc", "created_at_utc")]
    [InlineData("NormalizedEmail", "normalized_email")]
    [InlineData("IX_branches_TenantId_Code", "ix_branches_tenant_id_code")]
    [InlineData("PK_tenants", "pk_tenants")]
    [InlineData("FK_departments_branches_TenantId_BranchId", "fk_departments_branches_tenant_id_branch_id")]
    [InlineData("Id", "id")]
    [InlineData("address_line1", "address_line1")]
    public void Converts_pascal_case_and_ef_default_names(string input, string expected) =>
        Assert.Equal(expected, SnakeCaseNaming.ToSnakeCase(input));
}
