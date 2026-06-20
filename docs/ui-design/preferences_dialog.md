# Đặc Tả Thiết Kế Hộp Thoại Cấu Hình Nâng Cao (Preferences Dialog)

Tài liệu này mô tả chi tiết quy chuẩn thiết kế, bố cục (layout) và các nhóm chức năng cho hộp thoại **Cấu hình Nâng cao (Preferences/Settings Dialog)** được gọi từ trình đơn `Edit -> Preferences...` (hoặc `Ctrl + ,`).

---

## 1. Mục Tiêu Thiết Kế
- **Trải nghiệm quen thuộc**: Sử dụng bố cục Settings hiện đại chuẩn Windows 11 (Sidebar điều hướng bên trái, nội dung cài đặt bên phải).
- **Phân nhóm rõ ràng**: Phân tách các cấu hình phức tạp (System Prompt, AI Model, IPC) ra khỏi giao diện chính để giữ MainWindow luôn gọn gàng, đơn giản.
- **Lưu trữ trạng thái**: Mọi thay đổi trong hộp thoại này sẽ được lưu vào tệp cấu hình (ví dụ: `config.json` hoặc `appsettings.json`).

---

## 2. Đặc Tả Trực Quan (Visual Specifications)

### A. Kích Thước & Bố Cục Tổng Thể
- **Window Size**: Cố định kích thước `720px` (Rộng) x `480px` (Cao). Không cho phép thay đổi kích thước (`ResizeMode="NoResize"`) để đảm bảo layout luôn đẹp mắt.
- **WindowStyle**: `SingleBorderWindow` hoặc `ToolWindow`. Mở dưới dạng hộp thoại `ShowDialog` (chặn tương tác với cửa sổ chính cho đến khi đóng).
- **Nền (Background)**: `#F9F9F9` (Màu nền tổng thể sáng nhạt).

### B. Bố Cục (Layout)
Chia làm 2 phần (Cột):
1. **Sidebar Navigation (Trái)**: Rộng `200px`.
   - Nền: `#F3F3F3`.
   - Border Right: `1px solid #E5E5E5`.
   - Chứa danh sách các tab cài đặt (ListBox/RadioButton dạng phẳng).
2. **Content Area (Phải)**: Chiếm phần diện tích còn lại (`*`).
   - Nền: `#FFFFFF`.
   - Margin nội dung: `24px` ở tất cả các viền.
   - Chứa các điều khiển (controls) tương ứng với tab đang chọn.

---

## 3. Cấu Trúc Các Tab Chức Năng

### Tab 1: Cài Đặt Chung (General)
Chứa các thiết lập cơ bản về hành vi của ứng dụng:
- **Khởi động**: Bật/Tắt "Khởi động cùng Windows".
- **Hành vi đóng**: Bật/Tắt "Thu nhỏ xuống khay hệ thống (System Tray) khi đóng cửa sổ".
- **Ngôn ngữ ứng dụng**: Dropdown chọn ngôn ngữ giao diện (Tiếng Việt, English).
- **Cập nhật**: Bật/Tắt "Tự động kiểm tra phiên bản mới khi khởi động".

### Tab 2: Dịch Thuật AI (AI Translation)
Chứa các cấu hình sâu về mô hình trí tuệ nhân tạo:
- **Mô hình AI (AI Model)**: Dropdown chọn mô hình (Gemini 1.5 Pro, Gemini Flash, Claude 3 Haiku, v.v.).
- **Khóa API (API Key)**: TextBox ẩn mật khẩu để nhập API Key riêng (Nếu người dùng muốn dùng Key cá nhân). Nút "Test Connection".
- **Prompt Hệ Thống (System Prompt)**: TextBox nhiều dòng (Multiline) để tùy chỉnh cách AI nhận diện và dịch câu.

### Tab 3: Giao Diện (Appearance)
Cấu hình về mặt hiển thị:
- **Chủ đề (Theme)**: Radio buttons (Sáng, Tối, Theo Hệ thống).
- **Mặc định chữ nổi (Overlay Defaults)**:
  - Cỡ chữ mặc định (Dropdown).
  - Độ mờ nền mặc định (Slider).
  - Vị trí hiển thị mặc định (Top, Bottom, Custom).

### Tab 4: Nâng Cao & Hệ Thống (Advanced)
Dành cho người dùng am hiểu kỹ thuật:
- **Named Pipe Name**: TextBox (Mặc định: `MSLCCaptionPipe`).
- **Thư mục Log (Log Directory)**: Nơi lưu các tệp tin `injection.log` và lịch sử dịch thuật. Nút "Mở thư mục...".
- **Gỡ lỗi (Debug)**: Bật/Tắt "Ghi log chi tiết (Verbose Logging)".

---

## 4. Khu Vực Hành Động (Action Area)
Nằm ở phía dưới cùng bên phải của cửa sổ Dialog (Hoặc trong từng Tab nếu lưu tự động):
- Áp dụng mô hình **Tự động lưu (Auto-save)**: Mọi thay đổi sẽ có tác dụng ngay lập tức mà không cần nút "Lưu".
- **Nút "Đóng" (Close)**: Đóng hộp thoại.
- **Nút "Khôi phục mặc định" (Reset to Defaults)**: Ở góc trái dưới cùng của mỗi tab.

---

## 5. Đặc Tả Tài Nguyên Ngôn Ngữ (i18n Keys)
Các khóa dịch thuật cần thêm vào `vi-VN.json` và `en-US.json`:

```json
  "Title_Preferences": "Cấu hình Nâng cao",
  "Tab_General": "Chung",
  "Tab_AiTranslation": "Dịch thuật AI",
  "Tab_Appearance": "Giao diện",
  "Tab_Advanced": "Nâng cao",
  
  "Pref_General_Startup": "Khởi động cùng Windows",
  "Pref_General_TrayIcon": "Thu nhỏ xuống khay hệ thống khi đóng",
  "Pref_General_Language": "Ngôn ngữ giao diện",
  
  "Pref_AI_Model": "Mô hình AI",
  "Pref_AI_ApiKey": "API Key (Tùy chọn)",
  "Pref_AI_SystemPrompt": "Prompt Hệ thống (System Prompt)",
  "Pref_AI_TestConnection": "Kiểm tra kết nối",
  
  "Pref_Advanced_PipeName": "Tên luồng Named Pipe",
  "Pref_Advanced_LogDir": "Thư mục lưu nhật ký",
  "Pref_Advanced_OpenDir": "Mở thư mục"
```

---

## 6. Đề Xuất Bố Cục XAML (Avalonia UI)

```xml
<Window xmlns="https://github.com/avaloniaui"
        x:Class="m_mslc_overlay.Dialogs.PreferencesDialog"
        Title="{DynamicResource Title_Preferences}"
        Width="720" Height="480"
        WindowStartupLocation="CenterOwner"
        CanResize="False">
    <Grid ColumnDefinitions="200, *">
        <!-- Sidebar Navigation -->
        <Border Grid.Column="0" Background="#F3F3F3" BorderBrush="#E5E5E5" BorderThickness="0,0,1,0">
            <ListBox x:Name="TabSelector" Background="Transparent" BorderThickness="0" Margin="0,8">
                <!-- Styles for ListBoxItem to look like flat buttons -->
                <ListBoxItem Content="{DynamicResource Tab_General}" />
                <ListBoxItem Content="{DynamicResource Tab_AiTranslation}" />
                <ListBoxItem Content="{DynamicResource Tab_Appearance}" />
                <ListBoxItem Content="{DynamicResource Tab_Advanced}" />
            </ListBox>
        </Border>
        
        <!-- Content Area -->
        <Border Grid.Column="1" Background="#FFFFFF" Padding="24">
            <!-- Sử dụng TransitioningContentControl để chuyển đổi View theo TabSelector -->
            <TransitioningContentControl x:Name="TabContent" />
        </Border>
    </Grid>
</Window>
```

---

## 7. Bản Vẽ Wireframe (Trực Quan)

Dưới đây là thiết kế wireframe cho cửa sổ Cấu hình Nâng cao được xuất từ công cụ Pencil:

![Preferences Dialog Layout](./preferences_dialog.png)
