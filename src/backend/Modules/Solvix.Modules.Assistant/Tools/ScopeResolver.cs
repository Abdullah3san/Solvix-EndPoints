namespace Solvix.Modules.Assistant.Tools;

using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Fleet;

/// <summary>
/// Turns names the model/user used ("Beirut", "Finance") into ids through the authorised Organization
/// services. The model never supplies raw ids it invented; unknown names produce a helpful error listing real options.
/// </summary>
public sealed class ScopeResolver(IBranchService branches, IDepartmentService departments)
{
    public async Task<FleetScope> ResolveAsync(string? branch, string? department, CancellationToken cancellationToken)
    {
        Guid? branchId = null;
        string? branchName = null;
        if (branch is not null)
        {
            var b = await ResolveBranchAsync(branch, cancellationToken);
            branchId = b.Id;
            branchName = b.Name;
        }

        IReadOnlyList<Guid>? departmentIds = null;
        if (department is not null)
        {
            var list = await departments.ListAsync(new DepartmentListQuery(branchId, null, false, 1, 200), cancellationToken);
            var exact = list.Items.Where(d => string.Equals(d.Name, department, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Code, department, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0
                ? exact
                : list.Items.Where(d => d.Name.Contains(department, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                var known = string.Join(", ", list.Items.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(30));
                throw new ToolInputException($"No department matches '{department}'{(branchName is null ? string.Empty : $" in branch {branchName}")}. Known departments: {(known.Length == 0 ? "none" : known)}.");
            }

            departmentIds = matches.Select(d => d.Id).ToList();
        }

        return new FleetScope(branchId, departmentIds);
    }

    public async Task<BranchDto> ResolveBranchAsync(string branch, CancellationToken cancellationToken)
    {
        var list = await branches.ListAsync(new BranchListQuery(null, false, 1, 200), cancellationToken);
        var exact = list.Items.Where(b => string.Equals(b.Name, branch, StringComparison.OrdinalIgnoreCase) || string.Equals(b.Code, branch, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : list.Items.Where(b => b.Name.Contains(branch, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        var options = string.Join(", ", list.Items.Select(b => $"{b.Name} ({b.Code})"));
        throw new ToolInputException(matches.Count == 0
            ? $"No branch matches '{branch}'. Available branches: {options}."
            : $"'{branch}' matches several branches ({string.Join(", ", matches.Select(m => m.Name))}). Use the exact name or code.");
    }
}
