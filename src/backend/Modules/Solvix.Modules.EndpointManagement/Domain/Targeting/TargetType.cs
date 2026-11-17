namespace Solvix.Modules.EndpointManagement.Domain.Targeting;

/// <summary>
/// What a deployment target or policy assignment points at. The numeric value is
/// also the specificity used to break ties between conflicting policies: a
/// setting assigned to a single device beats the same setting assigned to its
/// group, department, branch or the whole tenant.
/// </summary>
public enum TargetType
{
    AllDevices = 1,
    Branch = 2,

    /// <summary>The department and all of its sub-departments.</summary>
    Department = 3,
    DeviceGroup = 4,
    Device = 5,
}

/// <summary>A resolved target as used by the targeting engine.</summary>
public readonly record struct TargetRef(TargetType Type, Guid? Id, bool IsExclusion)
{
    public int Specificity => (int)Type;
}
