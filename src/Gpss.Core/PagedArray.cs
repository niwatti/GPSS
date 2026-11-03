namespace Gpss.Core;

/// <summary>Fixed maximum length, allocated on first write in bounded pages; single writer only.</summary>
internal sealed class PagedArray<T> where T : struct
{
    private const int PageShift = 13;
    private const int PageSize = 1 << PageShift;
    private readonly T[]?[] _pages;
    private readonly int _length;
    private readonly T _initial;
    public PagedArray(int length, T initial = default)
    {
        _length = length; _initial = initial;
        _pages = new T[]?[(length + PageSize - 1) / PageSize];
    }
    public T this[int index]
    {
        get
        {
            if ((uint)index >= _length) throw new IndexOutOfRangeException();
            return _pages[index >> PageShift] is { } page ? page[index & (PageSize - 1)] : _initial;
        }
        set
        {
            if ((uint)index >= _length) throw new IndexOutOfRangeException();
            var pageIndex = index >> PageShift;
            var page = _pages[pageIndex];
            if (page is null)
            {
                page = new T[Math.Min(PageSize, _length - (pageIndex << PageShift))];
                if (!EqualityComparer<T>.Default.Equals(_initial, default)) Array.Fill(page, _initial);
                _pages[pageIndex] = page;
            }
            page[index & (PageSize - 1)] = value;
        }
    }
    public int PageCount => _pages.Length;
    public int AllocatedCapacity => _pages.Sum(page => page?.Length ?? 0);
    public static int GetPageIndex(int index) => index >> PageShift;
    public void ReleasePage(int pageIndex) => _pages[pageIndex] = null;
    public void Clear() => Array.Clear(_pages);
}
