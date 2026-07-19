using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using App.ViewModels;

namespace App;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel viewModel;
    private bool followChatTail = true;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel();
        DataContext = viewModel;
        viewModel.Chat.Messages.CollectionChanged += OnChatMessagesChanged;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
    }

    private async void OnClosed(object sender, System.EventArgs e)
    {
        await viewModel.DisposeAsync();
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A message the user just sent always jumps to the bottom.
        if (e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems?.Count > 0 &&
            e.NewItems[0] is ChatViewModel.ChatEntry { Kind: "user" })
        {
            followChatTail = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (ChatMessages.Items.Count > 0)
                {
                    ChatMessages.ScrollIntoView(ChatMessages.Items[^1]);
                }
            }));
        }
    }

    /// <summary>
    /// Follow-the-tail auto-scroll (chat UX standard): scroll on content growth only while the
    /// user is pinned to the bottom. Scrolling up releases the tail (reading history mid-stream);
    /// scrolling back to the bottom re-engages it. Never scroll inside CollectionChanged — doing
    /// layout work there desyncs the item generator (the 2026-07-19 crash).
    /// </summary>
    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer)
        {
            return;
        }

        if (e.ExtentHeightChange == 0)
        {
            // User-initiated scroll (wheel/drag/keys) or layout: follow only when at the bottom.
            followChatTail = viewer.ScrollableHeight <= 0 || viewer.VerticalOffset >= viewer.ScrollableHeight - 8;
        }
        else if (followChatTail)
        {
            // Content grew (new entry or streaming delta): keep the newest text in view.
            viewer.ScrollToEnd();
        }
    }

    private void OnChatInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (viewModel.Chat.SendCommand.CanExecute(null))
            {
                viewModel.Chat.SendCommand.Execute(null);
            }
        }
    }
}
