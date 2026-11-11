using CommunityToolkit.Mvvm.ComponentModel;

namespace Sshelfy.App.ViewModels.Sidebar;

/// <summary>One page of the left sidebar, selected from the activity rail.</summary>
public abstract partial class SidebarSectionViewModel : ViewModelBase
{
    public abstract string Title { get; }

    public abstract AppIcon Icon { get; }

    /// <summary>True while this section is shown in an open sidebar (drives the rail highlight).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
