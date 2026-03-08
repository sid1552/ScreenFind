using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
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
        private List<OverlayWindow> _allOverlays = new();
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

            // Set window icon from app.ico (loaded from disk so it works in single-file publish)
            var icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(icoPath))
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(icoPath));

            _settings = Settings.Load();
            _hotkeyModifiers = _settings.HotkeyModifiers;
            _hotkeyKey = _settings.HotkeyKey;
            EnhanceOcrCheckbox.IsChecked = _settings.EnhanceOcr;
            PaddleOcrCheckbox.IsChecked = _settings.UsePaddleOcr;
            DragToSelectCheckbox.IsChecked = _settings.DragToSelect;
            HotkeyText.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            PopulateMonitorList();
            Loaded += MainWindow_Loaded;
            SetupTrayIcon();

            // If PaddleOCR was enabled from a previous session, pre-load models now
            if (_settings.UsePaddleOcr)
                PaddleOcrEngineManager.Warmup();

            // Pre-warm overlay windows after the main window finishes loading.
            // This forces HWND creation + WPF layout so the first hotkey press is fast.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(PrewarmOverlays));
        }

        /// <summary>
        /// Pre-create overlay windows for each active monitor. Show+Hide at Opacity=0
        /// forces WPF to parse XAML, build the visual tree, create the HWND, and run
        /// the first layout pass — so when the hotkey fires, we just set the bitmap and show.
        /// </summary>
        private void PrewarmOverlays()
        {
            var screens = SysForms.Screen.AllScreens
                .Where(s => !_settings.ExcludedMonitors.Contains(s.DeviceName))
                .ToArray();

            if (screens.Length == 0)
                screens = SysForms.Screen.AllScreens.Where(s => s.Primary).ToArray();

            OverlayWindow? primaryOverlay = null;

            foreach (var screen in screens)
            {
                bool isPrimary = screen.Primary || (primaryOverlay == null && !screens.Any(s => s.Primary));
                var overlay = new OverlayWindow(
                    screen,
                    _settings.EnhanceOcr, _settings.DragToSelect,
                    isPrimary, _settings.UsePaddleOcr);

                overlay.CloseAllRequested += () => DismissAllOverlays();

                overlay.Prewarm();
                _allOverlays.Add(overlay);

                if (isPrimary)
                    primaryOverlay = overlay;
            }

            // Wire search sync
            if (primaryOverlay != null)
            {
                primaryOverlay.SearchChanged += query =>
                {
                    foreach (var overlay in _allOverlays)
                    {
                        if (overlay != primaryOverlay)
                            overlay.ApplySearch(query);
                    }
                };
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Tab switching (Settings / About)
        // ────────────────────────────────────────────────────────────────
        private void SettingsTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchTab("settings");
        }

        private void AboutTab_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchTab("about");
        }

        private void SwitchTab(string tabName)
        {
            var activeColor = (Color)ColorConverter.ConvertFromString("#CDD6F4");
            var inactiveColor = (Color)ColorConverter.ConvertFromString("#585B70");
            var accentBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBA6F7"));
            var transparentBrush = Brushes.Transparent;

            if (tabName == "settings")
            {
                SettingsPanel.Visibility = Visibility.Visible;
                AboutPanel.Visibility = Visibility.Collapsed;

                SettingsTabText.Foreground = new SolidColorBrush(activeColor);
                SettingsTabBorder.BorderBrush = accentBrush;

                AboutTabText.Foreground = new SolidColorBrush(inactiveColor);
                AboutTabBorder.BorderBrush = transparentBrush;
            }
            else
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
                AboutPanel.Visibility = Visibility.Visible;

                AboutTabText.Foreground = new SolidColorBrush(activeColor);
                AboutTabBorder.BorderBrush = accentBrush;

                SettingsTabText.Foreground = new SolidColorBrush(inactiveColor);
                SettingsTabBorder.BorderBrush = transparentBrush;

                // Update the hotkey label in the About panel to reflect current hotkey
                AboutHotkeyLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  System tray icon
        // ────────────────────────────────────────────────────────────────
        private void SetupTrayIcon()
        {
            // Load custom icon from the embedded resource (app.ico in project root).
            // Falls back to the generic Windows app icon if the file isn't found.
            SysDrawing.Icon trayIcon;
            var icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(icoPath))
                trayIcon = new SysDrawing.Icon(icoPath);
            else
                trayIcon = SysDrawing.SystemIcons.Application;

            _trayIcon = new SysForms.NotifyIcon
            {
                Text = "ScreenFind",
                Icon = trayIcon,
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
            menu.Items.Add("Options", null, (s, e) =>
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

        private void PaddleOcrCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return; // fired during InitializeComponent before settings loaded
            _settings.UsePaddleOcr = PaddleOcrCheckbox.IsChecked == true;
            _settings.Save();

            // Pre-load PaddleOCR models in background so the first capture is fast.
            // Model loading takes ~1-2 seconds — doing it now means the user won't
            // wait when they trigger the overlay.
            if (_settings.UsePaddleOcr)
                PaddleOcrEngineManager.Warmup();
        }

        // ────────────────────────────────────────────────────────────────
        //  Monitor picker — dynamically build checkboxes for each screen
        // ────────────────────────────────────────────────────────────────
        private void PopulateMonitorList()
        {
            MonitorListPanel.Children.Clear();

            var screens = SysForms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                var bounds = screen.Bounds;

                // Build label like "Monitor 1 — 1920×1080 (Primary)"
                string label = $"Monitor {i + 1} — {bounds.Width}\u00D7{bounds.Height}";
                if (screen.Primary)
                    label += " (Primary)";

                bool isChecked = !_settings.ExcludedMonitors.Contains(screen.DeviceName);

                var cb = new CheckBox
                {
                    Content = label,
                    IsChecked = isChecked,
                    Tag = screen.DeviceName, // store device name for the handler
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A6ADC8")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                // Apply the same Catppuccin checkbox template as the XAML checkboxes
                cb.Style = BuildCheckboxStyle();

                cb.Checked += MonitorCheckbox_Changed;
                cb.Unchecked += MonitorCheckbox_Changed;

                MonitorListPanel.Children.Add(cb);
            }
        }

        /// <summary>
        /// Builds the Catppuccin-themed CheckBox Style in code,
        /// matching the DragToSelect / EnhanceOcr checkbox template in XAML.
        /// </summary>
        private static Style BuildCheckboxStyle()
        {
            // Colors matching the XAML theme
            var bgColor = (Color)ColorConverter.ConvertFromString("#45475A");
            var borderColor = (Color)ColorConverter.ConvertFromString("#585B70");
            var checkColor = (Color)ColorConverter.ConvertFromString("#A6E3A1");
            var hoverColor = (Color)ColorConverter.ConvertFromString("#585B70");

            // Build the ControlTemplate
            var template = new ControlTemplate(typeof(CheckBox));

            // Root: horizontal StackPanel
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            stackFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            // Box border
            var boxFactory = new FrameworkElementFactory(typeof(Border), "box");
            boxFactory.SetValue(FrameworkElement.WidthProperty, 18.0);
            boxFactory.SetValue(FrameworkElement.HeightProperty, 18.0);
            boxFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(bgColor));
            boxFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            boxFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(borderColor));
            boxFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            boxFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            boxFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));

            // Checkmark text inside the box
            var checkFactory = new FrameworkElementFactory(typeof(TextBlock), "check");
            checkFactory.SetValue(TextBlock.TextProperty, "\u2713");
            checkFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(checkColor));
            checkFactory.SetValue(TextBlock.FontSizeProperty, 13.0);
            checkFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            checkFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkFactory.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            boxFactory.AppendChild(checkFactory);
            stackFactory.AppendChild(boxFactory);

            // Content presenter
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            stackFactory.AppendChild(contentFactory);

            template.VisualTree = stackFactory;

            // Trigger: IsChecked = True → show checkmark + green border
            var checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(checkColor), "box"));
            template.Triggers.Add(checkedTrigger);

            // Trigger: IsMouseOver = True → lighter background
            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverColor), "box"));
            template.Triggers.Add(hoverTrigger);

            // Wrap in a Style
            var style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private void MonitorCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            var cb = (CheckBox)sender;
            var deviceName = (string)cb.Tag;

            if (cb.IsChecked == true)
            {
                // Checked → remove from exclusion list
                _settings.ExcludedMonitors.Remove(deviceName);
            }
            else
            {
                // Unchecked — but don't allow unchecking the last one
                int checkedCount = 0;
                foreach (CheckBox child in MonitorListPanel.Children)
                {
                    if (child.IsChecked == true)
                        checkedCount++;
                }

                if (checkedCount == 0)
                {
                    // Re-check it — can't have zero monitors
                    cb.IsChecked = true;
                    return;
                }

                // Add to exclusion list
                if (!_settings.ExcludedMonitors.Contains(deviceName))
                    _settings.ExcludedMonitors.Add(deviceName);
            }

            _settings.Save();
        }

        private void GithubLink_Click(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/sid1552/ScreenFind",
                UseShellExecute = true
            });
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
        private async void OnHotkeyPressed()
        {
            // Dismiss any visible overlays first
            DismissAllOverlays();

            try
            {
                // Rebuild overlays if the monitor config changed (monitors added/removed,
                // or user changed excluded monitors in settings)
                RebuildOverlaysIfNeeded();

                // Get active screens matching our overlays
                var screens = _allOverlays
                    .Select(o => o)
                    .ToArray();

                // Capture screens on background thread (the only slow part now)
                var screenObjects = SysForms.Screen.AllScreens
                    .Where(s => !_settings.ExcludedMonitors.Contains(s.DeviceName))
                    .ToArray();

                if (screenObjects.Length == 0)
                    screenObjects = SysForms.Screen.AllScreens.Where(s => s.Primary).ToArray();

                var bitmaps = await System.Threading.Tasks.Task.Run(() =>
                    CaptureScreens(screenObjects));

                // Activate each pre-warmed overlay with its fresh screenshot
                OverlayWindow? primaryOverlay = null;
                for (int i = 0; i < _allOverlays.Count && i < bitmaps.Count; i++)
                {
                    var overlay = _allOverlays[i];
                    overlay.Activate(bitmaps[i]);

                    if (primaryOverlay == null)
                    {
                        // First overlay that's primary (or first one if none is primary)
                        primaryOverlay = overlay;
                    }
                }
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

        /// <summary>
        /// Check if the monitor setup has changed since we pre-warmed the overlays.
        /// If so, close old overlays and create + prewarm new ones.
        /// </summary>
        private void RebuildOverlaysIfNeeded()
        {
            var activeScreens = SysForms.Screen.AllScreens
                .Where(s => !_settings.ExcludedMonitors.Contains(s.DeviceName))
                .ToArray();

            if (activeScreens.Length == 0)
                activeScreens = SysForms.Screen.AllScreens.Where(s => s.Primary).ToArray();

            // Check if overlay count matches and device names match
            if (_allOverlays.Count == activeScreens.Length)
                return; // assume same config — good enough for most cases

            // Config changed — rebuild
            foreach (var o in _allOverlays)
            {
                try { o.Close(); } catch { }
            }
            _allOverlays.Clear();

            OverlayWindow? primaryOverlay = null;

            foreach (var screen in activeScreens)
            {
                bool isPrimary = screen.Primary || (primaryOverlay == null && !activeScreens.Any(s => s.Primary));
                var overlay = new OverlayWindow(
                    screen,
                    _settings.EnhanceOcr, _settings.DragToSelect,
                    isPrimary, _settings.UsePaddleOcr);

                overlay.CloseAllRequested += () => DismissAllOverlays();
                overlay.Prewarm();
                _allOverlays.Add(overlay);

                if (isPrimary)
                    primaryOverlay = overlay;
            }

            if (primaryOverlay != null)
            {
                primaryOverlay.SearchChanged += query =>
                {
                    foreach (var overlay in _allOverlays)
                    {
                        if (overlay != primaryOverlay)
                            overlay.ApplySearch(query);
                    }
                };
            }
        }

        /// <summary>
        /// Hide all overlays (don't destroy — they'll be reused on next hotkey press).
        /// </summary>
        private void DismissAllOverlays()
        {
            foreach (var o in _allOverlays)
            {
                try { o.Dismiss(); } catch { }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Capture ALL screens using GDI+
        // ────────────────────────────────────────────────────────────────
        /// <summary>
        /// Capture specific screens using GDI+. Can run on a background thread.
        /// Returns bitmaps in the same order as the input screens array.
        /// </summary>
        private static List<SysDrawing.Bitmap> CaptureScreens(SysForms.Screen[] screens)
        {
            var bitmaps = new List<SysDrawing.Bitmap>(screens.Length);

            foreach (var screen in screens)
            {
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
                        new SysDrawing.Size(bounds.Width, bounds.Height),
                        SysDrawing.CopyPixelOperation.SourceCopy);
                }

                bitmaps.Add(bmp);
            }

            return bitmaps;
        }

        // ────────────────────────────────────────────────────────────────
        //  Cleanup
        // ────────────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            // Actually close (destroy) all cached overlays on app exit
            foreach (var o in _allOverlays)
            {
                try { o.Close(); } catch { }
            }
            _allOverlays.Clear();

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
