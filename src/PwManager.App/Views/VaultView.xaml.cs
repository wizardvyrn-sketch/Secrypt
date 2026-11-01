using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PwManager.Presentation.ViewModels;

namespace PwManager.App.Views;

/// <summary>解錠後のメイン画面（S-03）。処理は VaultViewModel。ここはフォーカスの移動と、一覧の選択の受け渡しだけ。</summary>
public partial class VaultView : UserControl
{
    public VaultView() => InitializeComponent();

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        if (DataContext is VaultViewModel vm) vm.Editor.PropertyChanged += Editor_PropertyChanged;
    }

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 「＋ 新規」でタイトル欄へ
        if (e.PropertyName == nameof(EntryEditorViewModel.Header) && sender is EntryEditorViewModel { IsNew: true })
            Dispatcher.BeginInvoke(() => TitleBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---------- 一覧の選択（W-04） ----------
    // ListBox に選択を変えさせてから「戻る」で戻すと、画面の選択が戻らないことがある。
    // そこで、クリック・矢印キーの時点で ListBox の処理を止め、確認と選択は ViewModel（TrySelect）に任せる。

    private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not VaultViewModel vm || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(EntryList, source) is not ListBoxItem { DataContext: EntryRowViewModel row }) return;
        e.Handled = true;
        if (vm.TrySelect(row.Id)) FocusSelected();
    }

    private void EntryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not VaultViewModel vm || vm.Items.Count == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
        int current = vm.SelectedItem is { } sel ? IndexOf(vm, sel.Id) : -1;
        int next = e.Key switch
        {
            Key.Down => Math.Min(current + 1, vm.Items.Count - 1),
            Key.Up => current < 0 ? 0 : Math.Max(current - 1, 0),
            Key.Home => 0,
            Key.End => vm.Items.Count - 1,
            _ => -2,
        };
        if (next == -2) return;
        e.Handled = true;
        if (next != current && vm.TrySelect(vm.Items[next].Id)) FocusSelected();
    }

    private static int IndexOf(VaultViewModel vm, Guid id)
    {
        for (int i = 0; i < vm.Items.Count; i++)
            if (vm.Items[i].Id == id) return i;
        return -1;
    }

    private void FocusSelected()
    {
        if (EntryList.SelectedItem is null) return;
        EntryList.ScrollIntoView(EntryList.SelectedItem);
        (EntryList.ItemContainerGenerator.ContainerFromItem(EntryList.SelectedItem) as ListBoxItem)?.Focus();
    }
}
