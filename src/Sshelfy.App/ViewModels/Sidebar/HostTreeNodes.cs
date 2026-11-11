using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sshelfy.Core.Hosts;

namespace Sshelfy.App.ViewModels.Sidebar;

public abstract partial class HostTreeNodeViewModel : ViewModelBase
{
    protected HostTreeNodeViewModel(HostsSidebarViewModel owner)
    {
        Owner = owner;
    }

    /// <summary>Gives item templates and context menus access to the sidebar commands.</summary>
    public HostsSidebarViewModel Owner { get; }

    public abstract Guid Id { get; }

    public abstract string Name { get; }

    public ObservableCollection<HostTreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => OnExpansionChanged(value);

    protected virtual void OnExpansionChanged(bool isExpanded)
    {
    }
}

public sealed partial class GroupNodeViewModel : HostTreeNodeViewModel
{
    private readonly bool _initializing;

    public GroupNodeViewModel(HostsSidebarViewModel owner, HostGroup group, bool isExpanded) : base(owner)
    {
        Group = group;
        _initializing = true;
        IsExpanded = isExpanded;
        _initializing = false;
    }

    public HostGroup Group { get; }

    public override Guid Id => Group.Id;

    public override string Name => Group.Name;

    public int HostCount => Children.Sum(c => c is GroupNodeViewModel g ? g.HostCount : 1);

    protected override void OnExpansionChanged(bool isExpanded)
    {
        if (!_initializing)
            Owner.OnGroupExpansionChanged(this, isExpanded);
    }
}

public sealed class HostNodeViewModel : HostTreeNodeViewModel
{
    public HostNodeViewModel(HostsSidebarViewModel owner, HostProfile host, string? keyName, string? jumpHostName = null) : base(owner)
    {
        Host = host;
        KeyName = keyName;
        JumpHostName = jumpHostName;
    }

    public string? JumpHostName { get; }

    public HostProfile Host { get; }

    public override Guid Id => Host.Id;

    public override string Name => Host.DisplayName;

    public string Endpoint => JumpHostName is null ? Host.Endpoint : $"{Host.Endpoint} via {JumpHostName}";

    public string? KeyName { get; }

    public bool UsesKey => Host.AuthMethod is AuthMethod.PrivateKey or AuthMethod.Agent;
}
