namespace Solvix.SharedKernel.Results;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 200;

    public int SafePage => Math.Max(1, Page);

    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);

    public int Skip => (SafePage - 1) * SafePageSize;
}
