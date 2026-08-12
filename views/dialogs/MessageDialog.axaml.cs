using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace m_mslc_overlay.views.dialogs
{
    public partial class MessageDialog : Window
    {
        public bool Result { get; private set; } = false;

        // Constructor không tham số cho compiler
        public MessageDialog()
        {
            InitializeComponent();
        }

        public MessageDialog(string title, string message, bool showCancel = false,
            string? okText = null, string? cancelText = null) : this()
        {
            Title = title;
            if (MessageText != null)
            {
                MessageText.Text = message;
            }
            if (CancelBtn != null)
            {
                CancelBtn.IsVisible = showCancel;
                if (!string.IsNullOrWhiteSpace(cancelText))
                    CancelBtn.Content = cancelText;
            }
            if (OkBtn != null && !string.IsNullOrWhiteSpace(okText))
            {
                OkBtn.Content = okText;
            }
        }

        private void OkBtn_Click(object? sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }

        public static async Task<bool> ShowAsync(Window owner, string title, string message, bool showCancel = false,
            string? okText = null, string? cancelText = null)
        {
            var dialog = new MessageDialog(title, message, showCancel, okText, cancelText);
            await dialog.ShowDialog(owner);
            return dialog.Result;
        }
    }
}
