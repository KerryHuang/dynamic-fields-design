namespace DynamicModules.Application.Common;

public record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int CurrentPage, int PagesSize);
