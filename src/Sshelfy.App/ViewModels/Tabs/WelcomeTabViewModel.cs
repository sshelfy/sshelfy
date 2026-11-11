using System.Windows.Input;

namespace Sshelfy.App.ViewModels.Tabs;

public sealed class WelcomeTabViewModel : TabViewModel
{
    public WelcomeTabViewModel(ICommand newHostCommand, ICommand quickConnectCommand, ICommand? aboutCommand = null)
    {
        Title = "Start";
        NewHostCommand = newHostCommand;
        QuickConnectCommand = quickConnectCommand;
        AboutCommand = aboutCommand;
    }

    public override AppIcon Icon => AppIcon.House;

    public ICommand NewHostCommand { get; }

    public ICommand QuickConnectCommand { get; }

    public ICommand? AboutCommand { get; }

    public string VersionText => $"Version {Services.AppInfo.Version}";
}
