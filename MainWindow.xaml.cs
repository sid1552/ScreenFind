using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SysDrawing = System.Drawing;
using SysForms = System.Windows.Forms;

namespace ScreenFind
{
    public partial class MainWindow : Window
    {
        // ─── Win32 hotkey API ───────────────────────────────────────────
        private const int HOTKEY_ID = 9001;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;

        // Modifier flag values for FormatHotkey / Win32
        private const uint MOD_ALT   = 0x0001;
        private const uint MOD_CTRL  = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN   = 0x0008;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ─── State ─────────────────────────────────────────────────────
        private HwndSource? _hwndSource;
        private OverlayWindow? _currentOverlay;
        private SysForms.NotifyIcon? _trayIcon;
        private Settings _settings = null!;

        // Currently active hotkey (loaded from settings)
        private uint _hotkeyModifiers;
        private uint _hotkeyKey;

        // Hotkey recording state
        private bool _isRecording;

        public MainWindow()
        {
            InitializeComponent();
            _settings = Settings.Load();
            _hotkeyModifiers = _settings.HotkeyModifiers;
            _hotkeyKey = _settings.HotkeyKey;
            EnhanceOcrCheckbox.IsChecked = _settings.EnhanceOcr;
            DragToSelectCheckbox.IsChecked = _settings.DragToSelect;
            HotkeyText.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            Loaded += MainWindow_Loaded;
            SetupTrayIcon();
        }

        // ────────────────────────────────────────────────────────────────
        //  System tray icon
        // ────────────────────────────────────────────────────────────────
        private void SetupTrayIcon()
        {
            _trayIcon = new SysForms.NotifyIcon
            {
                Text = "ScreenFind",
                Icon = SysDrawing.SystemIcons.Application,
                Visible = false
            };

            // Double-click tray icon to restore window
            _trayIcon.DoubleClick += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                _trayIcon.Visible = false;
            };

            // Right-click context menu
            var menu = new SysForms.ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                _trayIcon.Visible = false;
            });
            menu.Items.Add("Exit", null, (s, e) => Close());
            _trayIcon.ContextMenuStrip = menu;
        }

        private void DragToSelectCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return; // fired during InitializeComponent before settings loaded
            _settings.DragToSelect = DragToSelectCheckbox.IsChecked == true;
            _settings.Save();
        }

        private void EnhanceOcrCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return; // fired during InitializeComponent before settings loaded
            _settings.EnhanceOcr = EnhanceOcrCheckbox.IsChecked == true;
            _settings.Save();
        }

        private void TrayButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            if (_trayIcon != null)
                _trayIcon.Visible = true;
        }

        // ────────────────────────────────────────────────────────────────
        //  Setup: register the global hotkey once the window has an HWND
        // ────────────────────────────────────────────────────────────────
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            _hwndSource.AddHook(WndProc);

            bool ok = RegisterHotKey(
                helper.Handle,
                HOTKEY_ID,
                _hotkeyModifiers | MOD_NOREPEAT,
                _hotkeyKey);

            if (!ok)
            {
                MessageBox.Show(
                    $"Could not register {FormatHotkey(_hotkeyModifiers, _hotkeyKey)}.\n\n" +
                    "Another app may already be using this shortcut.\n" +
                    "Click the hotkey display to choose a different one.",
                    "ScreenFind",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Listen for the hotkey message
        // ────────────────────────────────────────────────────────────────
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                OnHotkeyPressed();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ────────────────────────────────────────────────────────────────
        //  Hotkey recording (click-to-change)
        // ────────────────────────────────────────────────────────────────
        private void HotkeyBorder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isRecording) return;
            _isRecording = true;
            HotkeyText.Text = "Press a key combo...";
            HotkeyText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F9E2AF"));
            HotkeyHint.Text = "Esc to cancel";
            PreviewKeyDown += HotkeyCapture_PreviewKeyDown;
        }

        private void HotkeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;

            // Escape cancels recording
            if (e.Key == Key.Escape)
            {
                StopRecording();
                return;
            }

            // Resolve system keys (e.g. Alt produces Key.System)
            var key = (e.Key == Key.System) ? e.SystemKey : e.Key;

            // Ignore lone modifier presses — wait for a real key
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LWin || key == Key.RWin)
                return;

            // Build Win32 modifier flags from current keyboard state
            uint mods = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CTRL;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))   mods |= MOD_SHIFT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))     mods |= MOD_ALT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;

            // Require at least one modifier
            if (mods == 0)
            {
                HotkeyText.Text = "Need a modifier (Ctrl/Alt/Shift)...";
                return;
            }

            // Convert WPF Key to Win32 virtual key code
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            // Try to register the new hotkey
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);

            bool ok = RegisterHotKey(helper.Handle, HOTKEY_ID, mods | MOD_NOREPEAT, vk);
            if (ok)
            {
                // Success — save new hotkey
                _hotkeyModifiers = mods;
                _hotkeyKey = vk;
                _settings.HotkeyModifiers = mods;
                _settings.HotkeyKey = vk;
                _settings.Save();
                StopRecording();
            }
            else
            {
                // Failed — re-register the old hotkey
                RegisterHotKey(helper.Handle, HOTKEY_ID, _hotkeyModifiers | MOD_NOREPEAT, _hotkeyKey);
                HotkeyText.Text = $"{FormatHotkey(mods, vk)} is taken!";
                HotkeyText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                // Let them try again (still recording)
            }
        }

        private void StopRecording()
        {
            _isRecording = false;
            PreviewKeyDown -= HotkeyCapture_PreviewKeyDown;
            HotkeyText.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            HotkeyText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5E0DC"));
            HotkeyHint.Text = "click to change";
        }

        /// <summary>Builds a display string like "Ctrl + Shift + F" from Win32 modifier flags and VK code.</summary>
        private static string FormatHotkey(uint modifiers, uint vk)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((modifiers & MOD_CTRL)  != 0) parts.Add("Ctrl");
            if ((modifiers & MOD_ALT)   != 0) parts.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & MOD_WIN)   != 0) parts.Add("Win");
            parts.Add(KeyInterop.KeyFromVirtualKey((int)vk).ToString());
            return string.Join(" + ", parts);
        }

        // ────────────────────────────────────────────────────────────────
        //  Hotkey pressed → capture screen → show overlay
        // ────────────────────────────────────────────────────────────────
        private void OnHotkeyPressed()
        {
            // Close any existing overlay first
            if (_currentOverlay != null)
            {
                _currentOverlay.Close();
                _currentOverlay = null;
            }

            try
            {
                // Capture the primary screen (physical pixels)
                var bitmap = CaptureScreen();

                // Show the search overlay
                _currentOverlay = new OverlayWindow(bitmap, _settings.EnhanceOcr, _settings.DragToSelect);
                _currentOverlay.Closed += (s, e) => _currentOverlay = null;
                _currentOverlay.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Screen capture failed:\n{ex.Message}",
                    "ScreenFind",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Capture the primary screen using GDI+
        // ────────────────────────────────────────────────────────────────
        private static SysDrawing.Bitmap CaptureScreen()
        {
            var screen = SysForms.Screen.PrimaryScreen!;
            var bounds = screen.Bounds; // physical pixels (app is DPI-aware)

            var bmp = new SysDrawing.Bitmap(
                bounds.Width,
                bounds.Height,
                SysDrawing.Imaging.PixelFormat.Format32bppArgb);

            using (var g = SysDrawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(
                    bounds.X, bounds.Y,
                    0, 0,
                    bounds.Size,
                    SysDrawing.CopyPixelOperation.SourceCopy);
            }

            return bmp;
        }

        // ────────────────────────────────────────────────────────────────
        //  Cleanup
        // ────────────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            _currentOverlay?.Close();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);

            _hwndSource?.RemoveHook(WndProc);
            base.OnClosed(e);

            // Shut down the whole app when the main window closes
            Application.Current.Shutdown();
        }
    }
}
