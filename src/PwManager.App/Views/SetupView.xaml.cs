using System.Windows;
using System.Windows.Controls;
using PwManager.Presentation.ViewModels;

namespace PwManager.App.Views;

/// <summary>初期設定（S-01）。処理は SetupViewModel。</summary>
public partial class SetupView : UserControl
{
    public SetupView() => InitializeComponent();

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm && !vm.TpmChecked) await vm.InitializeAsync();
    }
}
