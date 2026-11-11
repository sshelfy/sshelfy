using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>Read / write / execute check boxes for one of owner, group and others.</summary>
public sealed partial class PermissionRow : ViewModelBase
{
    private readonly Action _changed;

    public PermissionRow(string name, int who, int mode, Action changed)
    {
        Name = name;
        Who = who;
        _changed = changed;
        Load(mode);
    }

    public string Name { get; }

    internal int Who { get; }

    [ObservableProperty]
    public partial bool Read { get; set; }

    [ObservableProperty]
    public partial bool Write { get; set; }

    [ObservableProperty]
    public partial bool Execute { get; set; }

    internal bool Loading { get; private set; }

    internal void Load(int mode)
    {
        Loading = true;
        Read = UnixPermissions.Has(mode, Who, 0);
        Write = UnixPermissions.Has(mode, Who, 1);
        Execute = UnixPermissions.Has(mode, Who, 2);
        Loading = false;
    }

    internal int Apply(int mode)
    {
        mode = UnixPermissions.With(mode, Who, 0, Read);
        mode = UnixPermissions.With(mode, Who, 1, Write);
        return UnixPermissions.With(mode, Who, 2, Execute);
    }

    partial void OnReadChanged(bool value) => Notify();

    partial void OnWriteChanged(bool value) => Notify();

    partial void OnExecuteChanged(bool value) => Notify();

    private void Notify()
    {
        if (!Loading)
            _changed();
    }
}

/// <summary>Details of a remote file with editable permissions (chmod) and numeric owner/group (chown).</summary>
public sealed partial class RemotePropertiesDialogViewModel : DialogViewModel<bool>
{
    private readonly ISftpSession _session;
    private readonly RemoteFileEntry _entry;
    private readonly RemoteFileDetails _details;
    private int _mode;
    private bool _syncing;

    public RemotePropertiesDialogViewModel(ISftpSession session, RemoteFileEntry entry, RemoteFileDetails details)
    {
        _session = session;
        _entry = entry;
        _details = details;
        _mode = details.Mode;
        Rows =
        [
            new PermissionRow("Owner", 0, _mode, OnRowsChanged),
            new PermissionRow("Group", 1, _mode, OnRowsChanged),
            new PermissionRow("Others", 2, _mode, OnRowsChanged),
        ];
        Octal = UnixPermissions.ToOctal(_mode);
        UserId = details.UserId.ToString(CultureInfo.InvariantCulture);
        GroupId = details.GroupId.ToString(CultureInfo.InvariantCulture);
    }

    public override string Title => _entry.Name;

    public override double Width => 440;

    public string Location => RemotePath.Parent(_entry.FullPath);

    public string Kind => _entry.IsSymlink ? (_entry.IsDirectory ? "Link to a folder" : "Link") : _entry.IsDirectory ? "Folder" : "File";

    public string SizeText => _entry.IsDirectory ? "–" : $"{ByteSize.Format(_entry.Size)} ({_entry.Size.ToString("N0", CultureInfo.CurrentCulture)} bytes)";

    public string ModifiedText => _entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public IReadOnlyList<PermissionRow> Rows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Symbolic))]
    public partial string Octal { get; set; }

    public string Symbolic => UnixPermissions.ToSymbolic(_mode);

    [ObservableProperty]
    public partial string UserId { get; set; }

    [ObservableProperty]
    public partial string GroupId { get; set; }

    partial void OnOctalChanged(string value)
    {
        if (_syncing || UnixPermissions.ParseOctal(value) is not { } mode)
            return;

        // Three digits keep the special bits (setuid/setgid/sticky) the file already has.
        _mode = value.Trim().Length == 3 ? mode | (_mode & ~0b111_111_111) : mode;
        foreach (var row in Rows)
            row.Load(_mode);
        OnPropertyChanged(nameof(Symbolic));
    }

    private void OnRowsChanged()
    {
        foreach (var row in Rows)
            _mode = row.Apply(_mode);
        _syncing = true;
        Octal = UnixPermissions.ToOctal(_mode);
        _syncing = false;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (UnixPermissions.ParseOctal(Octal) is null)
        {
            Error = "Permissions must be three or four octal digits, e.g. 644 or 2755.";
            return;
        }

        if (!int.TryParse(UserId, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
            !int.TryParse(GroupId, NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
        {
            Error = "Owner and group are numeric ids (e.g. 1000).";
            return;
        }

        Error = null;
        IsBusy = true;
        try
        {
            var changed = false;
            if (_mode != _details.Mode)
            {
                await _session.SetPermissionsAsync(_entry.FullPath, _mode);
                changed = true;
            }

            if (uid != _details.UserId || gid != _details.GroupId)
            {
                await _session.SetOwnerAsync(_entry.FullPath, uid, gid);
                changed = true;
            }

            IsBusy = false;
            Close(changed);
        }
        catch (SftpOperationException ex)
        {
            IsBusy = false;
            Error = ex.Message == "Permission denied." && (uid != _details.UserId || gid != _details.GroupId)
                ? "Permission denied. Changing the owner usually needs root on the server."
                : ex.Message;
        }
    }
}
