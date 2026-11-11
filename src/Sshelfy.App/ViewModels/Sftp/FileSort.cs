using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sshelfy.App.ViewModels.Sftp;

public enum FileSortColumn
{
    Name,
    Size,
    Modified,
}

/// <summary>
/// Sort state of a file list, bound to clickable column headers. Folders always come first;
/// clicking the sorted column again flips the direction.
/// </summary>
public sealed partial class FileSort : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameArrow), nameof(SizeArrow), nameof(ModifiedArrow))]
    public partial FileSortColumn Column { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameArrow), nameof(SizeArrow), nameof(ModifiedArrow))]
    public partial bool Descending { get; private set; }

    /// <summary>Arrow shown next to a column header (none when the list is not sorted by it).</summary>
    public AppIcon NameArrow => Arrow(FileSortColumn.Name);

    public AppIcon SizeArrow => Arrow(FileSortColumn.Size);

    public AppIcon ModifiedArrow => Arrow(FileSortColumn.Modified);

    private AppIcon Arrow(FileSortColumn column) =>
        Column != column ? AppIcon.None : Descending ? AppIcon.ChevronDown : AppIcon.ChevronUp;

    /// <summary>Raised after the user picked another column or direction.</summary>
    public event EventHandler? Changed;

    [RelayCommand]
    private void SortBy(FileSortColumn column)
    {
        if (Column == column)
        {
            Descending = !Descending;
        }
        else
        {
            Column = column;
            Descending = column != FileSortColumn.Name; // newest / largest first feels natural
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<T> Apply<T>(IEnumerable<T> items, Func<T, bool> isDirectory, Func<T, string> name, Func<T, long> size, Func<T, DateTimeOffset> modified)
    {
        var folders = items.OrderByDescending(isDirectory);
        var byName = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<T> sorted = Column switch
        {
            FileSortColumn.Size => Descending ? folders.ThenByDescending(size) : folders.ThenBy(size),
            FileSortColumn.Modified => Descending ? folders.ThenByDescending(modified) : folders.ThenBy(modified),
            _ => Descending ? folders.ThenByDescending(name, byName) : folders.ThenBy(name, byName),
        };
        return Column == FileSortColumn.Name ? sorted : sorted.ThenBy(name, byName);
    }
}
