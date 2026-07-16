using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace m_mslc_overlay.views.dialogs
{
    public partial class LoadingDialog : Window
    {
        public LoadingDialog()
        {
            InitializeComponent();
        }

        public LoadingDialog(string message) : this()
        {
            if (MessageText != null)
            {
                MessageText.Text = message;
            }
        }

        // Cập nhật text trong quá trình loading
        public void UpdateMessage(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (MessageText != null)
                {
                    MessageText.Text = message;
                }
            });
        }
        
        // Cung cấp một static helper để chạy tác vụ bất đồng bộ và hiện dialog chờ
        public static async Task ShowLoadingTaskAsync(Window owner, string initialMessage, System.Func<LoadingDialog, Task> action)
        {
            var dialog = new LoadingDialog(initialMessage);
            
            // Chạy action trong background
            _ = Task.Run(async () =>
            {
                try
                {
                    await action(dialog);
                }
                finally
                {
                    // Đóng dialog khi xong (thành công hoặc lỗi)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close());
                }
            });

            // Hiện dialog dạng Modal chặn UI
            await dialog.ShowDialog(owner);
        }
    }
}
