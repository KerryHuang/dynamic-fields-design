namespace DynamicModules.Application.Common;

public class PagedRequest
{
    public const int MaxPagesSize = 200;

    private int _pagesSize = 10;

    public int CurrentPage { get; set; } = 1;

    public int PagesSize
    {
        get => _pagesSize;
        set => _pagesSize = Math.Min(value, MaxPagesSize);
    }
}
