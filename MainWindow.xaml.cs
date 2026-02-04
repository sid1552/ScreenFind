using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SysDrawing = System.Drawing;
using SysForms = System.Windows.Forms;

namespace ScreenFind
{
    public partial class MainWindow : Window
    {
        // ─── Win32 hotkey API ───────────────────────────────────────────
        private const int HOTKEY_ID = 9001;
        private const int WM_HOTKEY = 0x0312;

        // Modifier keys
        private const uint MOD_CTRL  = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        // Virtual key code for 'F'
        private const uint VK_F = 0x46;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ─── State ─────────────────────────────────────────────────────
        private HwndSource? _hwndSource;
        private OverlayWindow? _currentOverlay;
        private SysForms.NotifyIcon? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();
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
                MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT,
                VK_F);

            if (!ok)
            {
                MessageBox.Show(
                    "Could not register Ctrl+Shift+F.\n\n" +
                    "Another app may already be using this shortcut.\n" +
                    "You can change the hotkey in MainWindow.xaml.cs.",
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
                _currentOverlay = new OverlayWindow(bitmap);
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
