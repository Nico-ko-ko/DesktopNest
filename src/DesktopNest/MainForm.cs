using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WpfBitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;
using WpfBitmapDecoder = System.Windows.Media.Imaging.BitmapDecoder;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;
using WpfFormatConvertedBitmap = System.Windows.Media.Imaging.FormatConvertedBitmap;
using WpfInt32Rect = System.Windows.Int32Rect;
using WpfPixelFormats = System.Windows.Media.PixelFormats;
using WpfTransformedBitmap = System.Windows.Media.Imaging.TransformedBitmap;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace DesktopNest
{
    internal sealed class MainForm : Form
    {
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int HTCAPTION = 2;

        private readonly AppSettingsStore settingsStore;
        private readonly AppSettings settings;
        private readonly ShortcutImporter importer;
        private readonly ShortcutLauncher launcher;
        private readonly IconService iconService;
        private readonly BackgroundSettingsStore backgroundStore;
        private BackgroundSettings backgroundSettings;
        private readonly BackgroundSurface backgroundSurface;
        private readonly Panel scrollHost;
        private readonly IconGrid grid;
        private readonly TitleBarIconWindow titleBarIconWindow;
        private readonly ArrangeIconWindow arrangeIconWindow;
        private readonly BagIconWindow bagIconWindow;
        private readonly Panel settingsPanel;
        private readonly Panel arrangePanel;
        private readonly Panel capacityPanel;
        private readonly SettingsOutsideClickFilter outsideClickFilter;
        private readonly WinFormsTimer saveTimer;
        private readonly WinFormsTimer sourceMoveTimer;
        private readonly List<PendingSourceMove> pendingSourceMoves;
        private string startupWarning;
        private Button colorModeButton;
        private Button gradientModeButton;
        private Button imageModeButton;
        private Button gradientFromButton;
        private Button gradientToButton;
        private ComboBox gradientDirectionBox;
        private TextBox urlTextBox;
        private Button urlDownloadButton;
        private Label backgroundStatusLabel;
        private Button arrangeFreeButton;
        private Button arrangeCompactButton;
        private TrackBar iconScaleTrackBar;
        private Label iconScaleValueLabel;
        private TrackBar capacityTrackBar;
        private TextBox capacityValueTextBox;
        private Label capacityUsedLabel;
        private bool saveErrorShown;
        private bool downloadingBackground;
        private bool updatingBackgroundControls;
        private bool updatingArrangeControls;
        private bool updatingCapacityControls;

        public MainForm(
            AppSettingsStore settingsStore,
            AppSettings settings,
            string startupWarning)
        {
            this.settingsStore = settingsStore;
            this.settings = settings;
            this.startupWarning = startupWarning;
            backgroundStore = new BackgroundSettingsStore(
                Path.Combine(Application.StartupPath, "config"));
            string backgroundWarning;
            backgroundSettings = backgroundStore.Load(out backgroundWarning);
            if (!String.IsNullOrWhiteSpace(backgroundWarning))
            {
                this.startupWarning = CombineWarnings(this.startupWarning, backgroundWarning);
            }

            importer = new ShortcutImporter(settingsStore.ItemsDirectory);
            launcher = new ShortcutLauncher();
            iconService = new IconService();

            InitializeWindow();

            backgroundSurface = new BackgroundSurface(backgroundSettings);
            backgroundSurface.Dock = DockStyle.Fill;
            backgroundSurface.AllowDrop = true;
            backgroundSurface.DragEnter += HandleDragEnter;
            backgroundSurface.DragDrop += HandleDragDrop;
            backgroundSurface.ImageLoadFailed += delegate(string message)
            {
                SetStatus("背景图暂时无法显示：" + message);
            };

            scrollHost = new TransparentPanel();
            scrollHost.Dock = DockStyle.Fill;
            scrollHost.AutoScroll = true;
            scrollHost.BackColor = Color.Transparent;
            scrollHost.AllowDrop = true;
            scrollHost.Resize += delegate { LayoutGrid(); };
            scrollHost.DragEnter += HandleDragEnter;
            scrollHost.DragDrop += HandleDragDrop;

            grid = new IconGrid(iconService, settingsStore);
            grid.SetLayoutSettings(settings.FreeArrange, settings.IconScale);
            grid.AllowDrop = true;
            grid.DragEnter += HandleDragEnter;
            grid.DragDrop += HandleDragDrop;
            grid.ItemActivated += LaunchItem;
            grid.ItemContextRequested += ShowItemContextMenu;
            grid.BlankContextRequested += ShowBlankContextMenu;
            grid.ItemLayoutChanged += delegate
            {
                LayoutGrid();
                ScheduleSave();
            };
            scrollHost.Controls.Add(grid);

            Controls.Add(backgroundSurface);
            backgroundSurface.Controls.Add(scrollHost);

            settingsPanel = CreateSettingsPanel();
            settingsPanel.Visible = false;
            Controls.Add(settingsPanel);

            arrangePanel = CreateArrangePanel();
            arrangePanel.Visible = false;
            Controls.Add(arrangePanel);

            capacityPanel = CreateCapacityPanel();
            capacityPanel.Visible = false;
            Controls.Add(capacityPanel);

            backgroundSurface.SendToBack();
            settingsPanel.BringToFront();
            arrangePanel.BringToFront();
            capacityPanel.BringToFront();

            titleBarIconWindow = new TitleBarIconWindow(this);
            arrangeIconWindow = new ArrangeIconWindow(this);
            bagIconWindow = new BagIconWindow(this);
            outsideClickFilter = new SettingsOutsideClickFilter(this);
            Application.AddMessageFilter(outsideClickFilter);
            Resize += delegate
            {
                LayoutFloatingControls();
                titleBarIconWindow.UpdatePosition();
                arrangeIconWindow.UpdatePosition();
                bagIconWindow.UpdatePosition();
            };

            AllowDrop = true;
            DragEnter += HandleDragEnter;
            DragDrop += HandleDragDrop;

            saveTimer = new WinFormsTimer();
            saveTimer.Interval = 500;
            saveTimer.Tick += delegate
            {
                saveTimer.Stop();
                SaveSettings(false);
            };

            pendingSourceMoves = new List<PendingSourceMove>();
            sourceMoveTimer = new WinFormsTimer();
            sourceMoveTimer.Interval = 350;
            sourceMoveTimer.Tick += delegate
            {
                sourceMoveTimer.Stop();
                ProcessPendingSourceMoves();
            };

            ResizeEnd += delegate { ScheduleSave(); };
            LocationChanged += delegate
            {
                ScheduleSave();
                titleBarIconWindow.UpdatePosition();
                arrangeIconWindow.UpdatePosition();
                bagIconWindow.UpdatePosition();
            };
            Activated += delegate
            {
                titleBarIconWindow.UpdatePosition();
                arrangeIconWindow.UpdatePosition();
                bagIconWindow.UpdatePosition();
            };
            FormClosing += delegate
            {
                ProcessPendingSourceMoves();
                SaveSettings(false);
            };
            FormClosed += delegate
            {
                saveTimer.Dispose();
                sourceMoveTimer.Dispose();
                backgroundSurface.Dispose();
                titleBarIconWindow.Dispose();
                arrangeIconWindow.Dispose();
                bagIconWindow.Dispose();
                Application.RemoveMessageFilter(outsideClickFilter);
                iconService.Dispose();
            };
            Shown += delegate
            {
                titleBarIconWindow.UpdatePosition();
                arrangeIconWindow.UpdatePosition();
                bagIconWindow.UpdatePosition();
                LayoutFloatingControls();
                backgroundSurface.RefreshBackground();
                LayoutGrid();
                EnsureAllItemsVisible();
                LayoutGrid();
                RefreshArrangeControlValues();
                if (!String.IsNullOrWhiteSpace(startupWarning))
                {
                    MessageBox.Show(
                        this,
                        startupWarning,
                        "DesktopNest",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            };

            RefreshGrid();
        }

        public void ActivateFromSecondInstance()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            Show();
            Activate();
            BringToFront();
            NativeMethods.SetForegroundWindow(Handle);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WM_NCLBUTTONDBLCLK && message.WParam.ToInt32() == HTCAPTION)
            {
                BeginInvoke(new MethodInvoker(RenameTitle));
                return;
            }

            base.WndProc(ref message);
        }

        private void InitializeWindow()
        {
            Text = settings.Title;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(WindowPlacement.MinimumWidth, WindowPlacement.MinimumHeight);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ShowInTaskbar = true;
            MaximizeBox = true;
            MinimizeBox = true;
            KeyPreview = true;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            Rectangle primaryBounds = Screen.PrimaryScreen.Bounds;
            List<Rectangle> screens = new List<Rectangle>();
            Screen[] allScreens = Screen.AllScreens;
            for (int i = 0; i < allScreens.Length; i++)
            {
                screens.Add(allScreens[i].Bounds);
            }

            Bounds = WindowPlacement.Normalize(settings.GetBounds(), screens, primaryBounds);
            if (settings.IsMaximized)
            {
                Shown += delegate { WindowState = FormWindowState.Maximized; };
            }
        }

        private static string CombineWarnings(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first))
            {
                return second;
            }

            if (String.IsNullOrWhiteSpace(second))
            {
                return first;
            }

            return first + Environment.NewLine + Environment.NewLine + second;
        }

        private Panel CreateSettingsPanel()
        {
            Panel panel = new Panel();
            panel.Size = new Size(352, 536);
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.AutoScroll = true;

            Label title = new Label();
            title.Text = "背景设置";
            title.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(31, 41, 55);
            title.AutoSize = true;
            title.Location = new Point(14, 11);
            panel.Controls.Add(title);

            Button closeButton = CreateSmallButton("×", 28);
            closeButton.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular);
            closeButton.Location = new Point(311, 7);
            closeButton.Click += delegate { HideSettingsPanel(); };
            panel.Controls.Add(closeButton);

            Label modeLabel = CreateSectionLabel("背景模式");
            modeLabel.Location = new Point(14, 48);
            panel.Controls.Add(modeLabel);

            colorModeButton = CreateModeButton("纯色", 14);
            colorModeButton.Click += delegate
            {
                SelectBackgroundMode(BackgroundModes.Color);
            };
            panel.Controls.Add(colorModeButton);

            gradientModeButton = CreateModeButton("渐变", 112);
            gradientModeButton.Click += delegate
            {
                SelectBackgroundMode(BackgroundModes.Gradient);
            };
            panel.Controls.Add(gradientModeButton);

            imageModeButton = CreateModeButton("背景图", 210);
            imageModeButton.Click += delegate
            {
                SelectBackgroundMode(BackgroundModes.Image);
            };
            panel.Controls.Add(imageModeButton);

            Label colorLabel = CreateSectionLabel("背景颜色");
            colorLabel.Location = new Point(14, 111);
            panel.Controls.Add(colorLabel);

            Color[] swatches = new Color[]
            {
                ColorTranslator.FromHtml("#FFFFFF"),
                ColorTranslator.FromHtml("#F5F7FA"),
                ColorTranslator.FromHtml("#E2E8F0"),
                ColorTranslator.FromHtml("#DCE6F2"),
                ColorTranslator.FromHtml("#336699"),
                ColorTranslator.FromHtml("#2F855A"),
                ColorTranslator.FromHtml("#C05621"),
                ColorTranslator.FromHtml("#7C3AED")
            };
            for (int i = 0; i < swatches.Length; i++)
            {
                Button swatch = new Button();
                swatch.Size = new Size(29, 29);
                swatch.Location = new Point(14 + i * 34, 133);
                swatch.BackColor = swatches[i];
                swatch.FlatStyle = FlatStyle.Flat;
                swatch.FlatAppearance.BorderColor = Color.FromArgb(180, 185, 195);
                swatch.Cursor = Cursors.Hand;
                Color selectedColor = swatches[i];
                swatch.Click += delegate
                {
                    backgroundSettings.Color = ColorToHex(selectedColor);
                    SelectBackgroundMode(BackgroundModes.Color);
                };
                panel.Controls.Add(swatch);
            }

            Button customColorButton = CreateActionButton("自定义取色", 112);
            customColorButton.Location = new Point(14, 169);
            customColorButton.Click += delegate { ChooseCustomColor(); };
            panel.Controls.Add(customColorButton);

            Label gradientLabel = CreateSectionLabel("线性渐变");
            gradientLabel.Location = new Point(14, 207);
            panel.Controls.Add(gradientLabel);

            gradientFromButton = CreateColorButton("起点");
            gradientFromButton.Location = new Point(80, 204);
            gradientFromButton.Click += delegate { ChooseGradientColor(true); };
            panel.Controls.Add(gradientFromButton);

            gradientToButton = CreateColorButton("终点");
            gradientToButton.Location = new Point(178, 204);
            gradientToButton.Click += delegate { ChooseGradientColor(false); };
            panel.Controls.Add(gradientToButton);

            gradientDirectionBox = new ComboBox();
            gradientDirectionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            gradientDirectionBox.Size = new Size(170, 26);
            gradientDirectionBox.Location = new Point(80, 238);
            gradientDirectionBox.Items.Add(new DirectionOption("↓ 上到下", BackgroundDirections.TopToBottom));
            gradientDirectionBox.Items.Add(new DirectionOption("↑ 下到上", BackgroundDirections.BottomToTop));
            gradientDirectionBox.Items.Add(new DirectionOption("→ 左到右", BackgroundDirections.LeftToRight));
            gradientDirectionBox.Items.Add(new DirectionOption("↘ 对角", BackgroundDirections.Diagonal));
            gradientDirectionBox.SelectedIndexChanged += delegate
            {
                if (updatingBackgroundControls)
                {
                    return;
                }

                DirectionOption option = gradientDirectionBox.SelectedItem as DirectionOption;
                if (option != null)
                {
                    backgroundSettings.Gradient.Direction = option.Value;
                    SelectBackgroundMode(BackgroundModes.Gradient);
                }
            };
            panel.Controls.Add(gradientDirectionBox);

            Label uploadLabel = CreateSectionLabel("更换背景");
            uploadLabel.Location = new Point(14, 278);
            panel.Controls.Add(uploadLabel);

            Button localImageButton = CreateActionButton("本地上传", 150);
            localImageButton.Location = new Point(14, 300);
            localImageButton.Click += delegate { ChooseLocalBackground(); };
            panel.Controls.Add(localImageButton);

            Button wallpaperButton = CreateActionButton("使用桌面壁纸", 150);
            wallpaperButton.Location = new Point(174, 300);
            wallpaperButton.Click += delegate { UseDesktopWallpaper(); };
            panel.Controls.Add(wallpaperButton);

            Label urlLabel = CreateSectionLabel("图片链接");
            urlLabel.Location = new Point(14, 342);
            panel.Controls.Add(urlLabel);

            urlTextBox = new TextBox();
            urlTextBox.Size = new Size(218, 25);
            urlTextBox.Location = new Point(14, 364);
            urlTextBox.Text = backgroundSettings.Image.OriginalUrl ?? String.Empty;
            panel.Controls.Add(urlTextBox);

            urlDownloadButton = CreateActionButton("确定", 96);
            urlDownloadButton.Location = new Point(240, 363);
            urlDownloadButton.Click += delegate { StartUrlBackgroundDownload(false); };
            panel.Controls.Add(urlDownloadButton);

            Button redownloadButton = CreateActionButton("重新下载", 96);
            redownloadButton.Location = new Point(240, 397);
            redownloadButton.Click += delegate { StartUrlBackgroundDownload(true); };
            panel.Controls.Add(redownloadButton);

            backgroundStatusLabel = new Label();
            backgroundStatusLabel.AutoSize = false;
            backgroundStatusLabel.Size = new Size(322, 44);
            backgroundStatusLabel.Location = new Point(14, 442);
            backgroundStatusLabel.ForeColor = Color.FromArgb(90, 98, 112);
            backgroundStatusLabel.Text = "背景设置会立即生效并自动保存。";
            panel.Controls.Add(backgroundStatusLabel);

            SelectBackgroundModeButtonStates();
            RefreshBackgroundControlValues();
            return panel;
        }

        private static Label CreateSectionLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.ForeColor = Color.FromArgb(55, 65, 81);
            label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            return label;
        }

        private static Button CreateModeButton(string text, int left)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(left, 68);
            button.Size = new Size(92, 31);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button CreateActionButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(width, 29);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.BackColor = Color.FromArgb(248, 250, 252);
            button.ForeColor = Color.FromArgb(31, 41, 55);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button CreateColorButton(string fallbackText)
        {
            Button button = new Button();
            button.Text = fallbackText;
            button.Size = new Size(84, 29);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(180, 185, 195);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button CreateSmallButton(string text, int size)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(size, size);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(75, 85, 99);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void LayoutFloatingControls()
        {
            if (settingsPanel == null)
            {
                return;
            }

            const int margin = 12;
            int panelHeight = Math.Min(536, Math.Max(180, ClientSize.Height - 20));
            settingsPanel.Size = new Size(352, panelHeight);
            settingsPanel.Location = new Point(
                Math.Max(8, ClientSize.Width - settingsPanel.Width - margin),
                8);

            if (arrangePanel != null)
            {
                int arrangeHeight = Math.Min(248, Math.Max(180, ClientSize.Height - 20));
                arrangePanel.Size = new Size(352, arrangeHeight);
                arrangePanel.Location = new Point(
                    Math.Max(8, ClientSize.Width - arrangePanel.Width - margin),
                    8);
            }

            if (capacityPanel != null)
            {
                int capacityHeight = Math.Min(220, Math.Max(180, ClientSize.Height - 20));
                capacityPanel.Size = new Size(352, capacityHeight);
                capacityPanel.Location = new Point(
                    Math.Max(8, ClientSize.Width - capacityPanel.Width - margin),
                    8);
            }
        }

        private void ToggleSettingsPanel()
        {
            if (settingsPanel.Visible)
            {
                HideSettingsPanel();
                return;
            }

            HideArrangePanel();
            HideCapacityPanel();
            LayoutFloatingControls();
            RefreshBackgroundControlValues();
            settingsPanel.Visible = true;
            settingsPanel.BringToFront();
        }

        private void HideSettingsPanel()
        {
            if (settingsPanel != null)
            {
                settingsPanel.Visible = false;
            }
        }

        private void ToggleArrangePanel()
        {
            if (arrangePanel != null && arrangePanel.Visible)
            {
                HideArrangePanel();
                return;
            }

            HideSettingsPanel();
            HideCapacityPanel();
            LayoutFloatingControls();
            RefreshArrangeControlValues();
            arrangePanel.Visible = true;
            arrangePanel.BringToFront();
        }

        private void HideArrangePanel()
        {
            if (arrangePanel != null)
            {
                arrangePanel.Visible = false;
            }
        }

        private void ToggleCapacityPanel()
        {
            if (capacityPanel != null && capacityPanel.Visible)
            {
                HideCapacityPanel();
                return;
            }

            HideSettingsPanel();
            HideArrangePanel();
            LayoutFloatingControls();
            RefreshCapacityControlValues();
            capacityPanel.Visible = true;
            capacityPanel.BringToFront();
        }

        private void HideCapacityPanel()
        {
            if (capacityPanel != null)
            {
                capacityPanel.Visible = false;
            }
        }

        private void HideFloatingPanels()
        {
            HideSettingsPanel();
            HideArrangePanel();
            HideCapacityPanel();
        }

        internal bool ShouldCloseSettingsPanelForClick(Point screenPoint)
        {
            bool settingsVisible = settingsPanel != null && settingsPanel.Visible;
            bool arrangeVisible = arrangePanel != null && arrangePanel.Visible;
            bool capacityVisible = capacityPanel != null && capacityPanel.Visible;
            if (!settingsVisible && !arrangeVisible && !capacityVisible)
            {
                return false;
            }

            Rectangle gearButtonBounds = titleBarIconWindow != null && titleBarIconWindow.Visible
                ? titleBarIconWindow.Bounds
                : Rectangle.Empty;
            Rectangle arrangeButtonBounds = arrangeIconWindow != null && arrangeIconWindow.Visible
                ? arrangeIconWindow.Bounds
                : Rectangle.Empty;
            Rectangle bagButtonBounds = bagIconWindow != null && bagIconWindow.Visible
                ? bagIconWindow.Bounds
                : Rectangle.Empty;

            if (settingsVisible)
            {
                Rectangle panelBounds = settingsPanel.RectangleToScreen(
                    settingsPanel.ClientRectangle);
                if (!panelBounds.Contains(screenPoint)
                    && !gearButtonBounds.Contains(screenPoint))
                {
                    return true;
                }
            }

            if (arrangeVisible)
            {
                Rectangle panelBounds = arrangePanel.RectangleToScreen(
                    arrangePanel.ClientRectangle);
                if (!panelBounds.Contains(screenPoint)
                    && !arrangeButtonBounds.Contains(screenPoint))
                {
                    return true;
                }
            }

            if (capacityVisible)
            {
                Rectangle panelBounds = capacityPanel.RectangleToScreen(
                    capacityPanel.ClientRectangle);
                if (!panelBounds.Contains(screenPoint)
                    && !bagButtonBounds.Contains(screenPoint))
                {
                    return true;
                }
            }

            return false;
        }

        private Panel CreateArrangePanel()
        {
            Panel panel = new Panel();
            panel.Size = new Size(352, 248);
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.AutoScroll = true;

            Label title = new Label();
            title.Text = "图标排列";
            title.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(31, 41, 55);
            title.AutoSize = true;
            title.Location = new Point(14, 11);
            panel.Controls.Add(title);

            Button closeButton = CreateSmallButton("×", 28);
            closeButton.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular);
            closeButton.Location = new Point(311, 7);
            closeButton.Click += delegate { HideArrangePanel(); };
            panel.Controls.Add(closeButton);

            Label modeLabel = CreateSectionLabel("排列方式");
            modeLabel.Location = new Point(14, 48);
            panel.Controls.Add(modeLabel);

            arrangeFreeButton = CreateModeButton("自由摆放", 14);
            arrangeFreeButton.Click += delegate { SelectArrangeMode(true); };
            panel.Controls.Add(arrangeFreeButton);

            arrangeCompactButton = CreateModeButton("紧凑排列", 112);
            arrangeCompactButton.Click += delegate { SelectArrangeMode(false); };
            panel.Controls.Add(arrangeCompactButton);

            Label hintLabel = new Label();
            hintLabel.Text = "自由摆放：拖动图标到任意格子。";
            hintLabel.AutoSize = false;
            hintLabel.Size = new Size(322, 20);
            hintLabel.ForeColor = Color.FromArgb(120, 128, 140);
            hintLabel.Location = new Point(14, 106);
            panel.Controls.Add(hintLabel);

            Label sizeLabel = CreateSectionLabel("图标大小");
            sizeLabel.Location = new Point(14, 134);
            panel.Controls.Add(sizeLabel);

            iconScaleTrackBar = new TrackBar();
            iconScaleTrackBar.Minimum = 50;
            iconScaleTrackBar.Maximum = 200;
            iconScaleTrackBar.TickFrequency = 25;
            iconScaleTrackBar.SmallChange = 5;
            iconScaleTrackBar.LargeChange = 25;
            iconScaleTrackBar.TickStyle = TickStyle.None;
            iconScaleTrackBar.Size = new Size(212, 40);
            iconScaleTrackBar.Location = new Point(14, 156);
            iconScaleTrackBar.ValueChanged += delegate
            {
                if (updatingArrangeControls)
                {
                    return;
                }

                settings.IconScale = iconScaleTrackBar.Value / 100.0;
                ApplyArrangeSettingsToGrid();
            };
            panel.Controls.Add(iconScaleTrackBar);

            iconScaleValueLabel = new Label();
            iconScaleValueLabel.AutoSize = true;
            iconScaleValueLabel.ForeColor = Color.FromArgb(31, 41, 55);
            iconScaleValueLabel.Location = new Point(234, 165);
            panel.Controls.Add(iconScaleValueLabel);

            Button resetScaleButton = CreateActionButton("恢复默认", 96);
            resetScaleButton.Location = new Point(232, 190);
            resetScaleButton.Click += delegate
            {
                settings.IconScale = 1.0;
                ApplyArrangeSettingsToGrid();
            };
            panel.Controls.Add(resetScaleButton);

            Label tipLabel = new Label();
            tipLabel.Text = "调整会立即生效并自动保存。";
            tipLabel.AutoSize = false;
            tipLabel.Size = new Size(322, 20);
            tipLabel.ForeColor = Color.FromArgb(120, 128, 140);
            tipLabel.Location = new Point(14, 208);
            panel.Controls.Add(tipLabel);

            RefreshArrangeControlValues();
            return panel;
        }

        private void SelectArrangeMode(bool free)
        {
            settings.FreeArrange = free;
            grid.SetLayoutSettings(settings.FreeArrange, settings.IconScale);
            UpdateArrangeModeButtonStates();
            LayoutGrid();
            grid.Invalidate();
            ScheduleSave();
        }

        private void ApplyArrangeSettingsToGrid()
        {
            RefreshArrangeControlValues();
            grid.SetLayoutSettings(settings.FreeArrange, settings.IconScale);
            LayoutGrid();
            grid.Invalidate();
            ScheduleSave();
        }

        private void RefreshArrangeControlValues()
        {
            if (iconScaleTrackBar == null)
            {
                return;
            }

            updatingArrangeControls = true;
            try
            {
                double scale = settings.IconScale;
                if (Double.IsNaN(scale) || scale <= 0)
                {
                    scale = 1.0;
                }

                int percent = (int)Math.Round(scale * 100.0);
                if (percent < iconScaleTrackBar.Minimum)
                {
                    percent = iconScaleTrackBar.Minimum;
                }

                if (percent > iconScaleTrackBar.Maximum)
                {
                    percent = iconScaleTrackBar.Maximum;
                }

                iconScaleTrackBar.Value = percent;
                if (iconScaleValueLabel != null)
                {
                    iconScaleValueLabel.Text = percent + "%";
                }
            }
            finally
            {
                updatingArrangeControls = false;
            }

            UpdateArrangeModeButtonStates();
        }

        private void UpdateArrangeModeButtonStates()
        {
            SetModeButtonState(arrangeFreeButton, settings.FreeArrange);
            SetModeButtonState(arrangeCompactButton, !settings.FreeArrange);
        }

        private Panel CreateCapacityPanel()
        {
            Panel panel = new Panel();
            panel.Size = new Size(352, 220);
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.AutoScroll = true;

            Label title = new Label();
            title.Text = "收纳容量";
            title.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(31, 41, 55);
            title.AutoSize = true;
            title.Location = new Point(14, 11);
            panel.Controls.Add(title);

            Button closeButton = CreateSmallButton("×", 28);
            closeButton.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular);
            closeButton.Location = new Point(311, 7);
            closeButton.Click += delegate { HideCapacityPanel(); };
            panel.Controls.Add(closeButton);

            capacityUsedLabel = new Label();
            capacityUsedLabel.Text = "已收纳 0 个图标";
            capacityUsedLabel.AutoSize = false;
            capacityUsedLabel.Size = new Size(322, 20);
            capacityUsedLabel.ForeColor = Color.FromArgb(90, 98, 112);
            capacityUsedLabel.Location = new Point(14, 44);
            panel.Controls.Add(capacityUsedLabel);

            Label capLabel = CreateSectionLabel("最多可收纳");
            capLabel.Location = new Point(14, 76);
            panel.Controls.Add(capLabel);

            capacityTrackBar = new TrackBar();
            capacityTrackBar.Minimum = 4;
            capacityTrackBar.Maximum = 100;
            capacityTrackBar.TickFrequency = 8;
            capacityTrackBar.SmallChange = 1;
            capacityTrackBar.LargeChange = 4;
            capacityTrackBar.TickStyle = TickStyle.None;
            capacityTrackBar.Size = new Size(240, 40);
            capacityTrackBar.Location = new Point(14, 98);
            capacityTrackBar.ValueChanged += delegate
            {
                if (updatingCapacityControls)
                {
                    return;
                }

                settings.Capacity = capacityTrackBar.Value;
                RefreshCapacityLabels();
                ScheduleSave();
            };
            panel.Controls.Add(capacityTrackBar);

            // 数值输入框：与滑条双向同步（回车或失焦生效, 超范围自动钳位 4~100）
            capacityValueTextBox = new TextBox();
            capacityValueTextBox.Font = new Font("Microsoft YaHei UI", 9.5F);
            capacityValueTextBox.ForeColor = Color.FromArgb(31, 41, 55);
            capacityValueTextBox.Location = new Point(262, 103);
            capacityValueTextBox.Size = new Size(46, 26);
            capacityValueTextBox.TextAlign = HorizontalAlignment.Center;
            capacityValueTextBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ApplyCapacityInput();
                }
            };
            capacityValueTextBox.LostFocus += delegate { ApplyCapacityInput(); };
            panel.Controls.Add(capacityValueTextBox);

            Label capacityUnitLabel = new Label();
            capacityUnitLabel.Text = "个";
            capacityUnitLabel.AutoSize = true;
            capacityUnitLabel.ForeColor = Color.FromArgb(90, 98, 112);
            capacityUnitLabel.Location = new Point(312, 108);
            panel.Controls.Add(capacityUnitLabel);

            Button resetCapacityButton = CreateActionButton("恢复默认", 96);
            resetCapacityButton.Location = new Point(232, 148);
            resetCapacityButton.Click += delegate
            {
                settings.Capacity = 20;
                RefreshCapacityControlValues();
                ScheduleSave();
            };
            panel.Controls.Add(resetCapacityButton);

            Label tipLabel = new Label();
            tipLabel.Text = "只限新拖入，已有图标不删。";
            tipLabel.AutoSize = false;
            tipLabel.Size = new Size(322, 20);
            tipLabel.ForeColor = Color.FromArgb(120, 128, 140);
            tipLabel.Location = new Point(14, 186);
            panel.Controls.Add(tipLabel);

            RefreshCapacityControlValues();
            return panel;
        }

        private void RefreshCapacityControlValues()
        {
            if (capacityTrackBar == null)
            {
                return;
            }

            updatingCapacityControls = true;
            try
            {
                int capacity = settings.Capacity;
                if (capacity < capacityTrackBar.Minimum)
                {
                    capacity = capacityTrackBar.Minimum;
                }

                if (capacity > capacityTrackBar.Maximum)
                {
                    capacity = capacityTrackBar.Maximum;
                }

                capacityTrackBar.Value = capacity;
                RefreshCapacityLabels();
            }
            finally
            {
                updatingCapacityControls = false;
            }
        }

        private void RefreshCapacityLabels()
        {
            if (capacityTrackBar == null)
            {
                return;
            }

            int capacity = capacityTrackBar.Value;
            if (capacityValueTextBox != null)
            {
                string capacityText = capacity.ToString();
                if (capacityValueTextBox.Text != capacityText)
                {
                    // 仅在内容不同时写入, 避免用户正在输入时光标被打断
                    capacityValueTextBox.Text = capacityText;
                }
            }

            if (capacityUsedLabel != null)
            {
                capacityUsedLabel.Text = "已收纳 "
                    + settings.Items.Count
                    + " / " + capacity + " 个图标";
            }
        }

        // 应用输入框里的容量数值: 非法则回退当前值, 超范围钳位到 4~100
        private void ApplyCapacityInput()
        {
            if (capacityValueTextBox == null || capacityTrackBar == null || updatingCapacityControls)
            {
                return;
            }

            int parsed;
            if (!int.TryParse(capacityValueTextBox.Text.Trim(), out parsed))
            {
                capacityValueTextBox.Text = capacityTrackBar.Value.ToString();
                return;
            }

            if (parsed < capacityTrackBar.Minimum)
            {
                parsed = capacityTrackBar.Minimum;
            }

            if (parsed > capacityTrackBar.Maximum)
            {
                parsed = capacityTrackBar.Maximum;
            }

            if (parsed != capacityTrackBar.Value)
            {
                settings.Capacity = parsed;
                RefreshCapacityControlValues();
                ScheduleSave();
            }
            else
            {
                capacityValueTextBox.Text = parsed.ToString();
            }
        }

        private static Color SampleCaptionPixelColor(int screenX, int screenY)
        {
            try
            {
                IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try
                    {
                        int colorRef = NativeMethods.GetPixel(dc, screenX, screenY);
                        if (colorRef != -1)
                        {
                            return Color.FromArgb(
                                colorRef & 0xFF,
                                (colorRef >> 8) & 0xFF,
                                (colorRef >> 16) & 0xFF);
                        }
                    }
                    finally
                    {
                        NativeMethods.ReleaseDC(IntPtr.Zero, dc);
                    }
                }
            }
            catch
            {
            }

            return Color.FromArgb(243, 243, 243);
        }

        private void SelectBackgroundMode(string mode)
        {
            backgroundSettings.Mode = mode;
            RefreshBackgroundControlValues();
            SaveAndApplyBackground();
        }

        private void SelectBackgroundModeButtonStates()
        {
            SetModeButtonState(
                colorModeButton,
                String.Equals(
                    backgroundSettings.Mode,
                    BackgroundModes.Color,
                    StringComparison.Ordinal));
            SetModeButtonState(
                gradientModeButton,
                String.Equals(
                    backgroundSettings.Mode,
                    BackgroundModes.Gradient,
                    StringComparison.Ordinal));
            SetModeButtonState(
                imageModeButton,
                String.Equals(
                    backgroundSettings.Mode,
                    BackgroundModes.Image,
                    StringComparison.Ordinal));
        }

        private static void SetModeButtonState(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }

            button.BackColor = selected
                ? Color.FromArgb(51, 102, 153)
                : Color.FromArgb(248, 250, 252);
            button.ForeColor = selected ? Color.White : Color.FromArgb(31, 41, 55);
            button.FlatAppearance.BorderColor = selected
                ? Color.FromArgb(51, 102, 153)
                : Color.FromArgb(203, 213, 225);
        }

        private void RefreshBackgroundControlValues()
        {
            if (gradientFromButton != null)
            {
                Color from = ParseColor(
                    backgroundSettings.Gradient.From,
                    ColorTranslator.FromHtml(BackgroundSettings.DefaultGradientFrom));
                gradientFromButton.BackColor = from;
                gradientFromButton.ForeColor = GetContrastingTextColor(from);
                gradientFromButton.Text = "起点";
            }

            if (gradientToButton != null)
            {
                Color to = ParseColor(
                    backgroundSettings.Gradient.To,
                    ColorTranslator.FromHtml(BackgroundSettings.DefaultGradientTo));
                gradientToButton.BackColor = to;
                gradientToButton.ForeColor = GetContrastingTextColor(to);
                gradientToButton.Text = "终点";
            }

            if (gradientDirectionBox != null)
            {
                updatingBackgroundControls = true;
                try
                {
                    for (int i = 0; i < gradientDirectionBox.Items.Count; i++)
                    {
                        DirectionOption option = gradientDirectionBox.Items[i] as DirectionOption;
                        if (option != null
                            && String.Equals(
                                option.Value,
                                backgroundSettings.Gradient.Direction,
                                StringComparison.Ordinal))
                        {
                            gradientDirectionBox.SelectedIndex = i;
                            break;
                        }
                    }
                }
                finally
                {
                    updatingBackgroundControls = false;
                }
            }

            if (urlTextBox != null
                && !String.Equals(
                    urlTextBox.Text,
                    backgroundSettings.Image.OriginalUrl,
                    StringComparison.Ordinal))
            {
                urlTextBox.Text = backgroundSettings.Image.OriginalUrl ?? String.Empty;
            }

            SelectBackgroundModeButtonStates();
        }

        private void ChooseCustomColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = ParseColor(
                    backgroundSettings.Color,
                    ColorTranslator.FromHtml(BackgroundSettings.DefaultColor));
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    backgroundSettings.Color = ColorToHex(dialog.Color);
                    SelectBackgroundMode(BackgroundModes.Color);
                }
            }
        }

        private void ChooseGradientColor(bool from)
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                string current = from
                    ? backgroundSettings.Gradient.From
                    : backgroundSettings.Gradient.To;
                dialog.Color = ParseColor(
                    current,
                    ColorTranslator.FromHtml(
                        from
                            ? BackgroundSettings.DefaultGradientFrom
                            : BackgroundSettings.DefaultGradientTo));
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    if (from)
                    {
                        backgroundSettings.Gradient.From = ColorToHex(dialog.Color);
                    }
                    else
                    {
                        backgroundSettings.Gradient.To = ColorToHex(dialog.Color);
                    }

                    SelectBackgroundMode(BackgroundModes.Gradient);
                }
            }
        }

        private void SaveAndApplyBackground()
        {
            try
            {
                backgroundStore.Save(backgroundSettings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "背景设置保存失败：" + Environment.NewLine + ex.Message,
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            backgroundSurface.SetSettings(backgroundSettings);
            RefreshBackgroundControlValues();
        }

        private void ChooseLocalBackground()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择背景图片";
                dialog.CheckFileExists = true;
                dialog.Filter =
                    "支持的图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.apng|所有文件|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string extension = DetectImageExtension(dialog.FileName);
                    if (String.IsNullOrWhiteSpace(extension))
                    {
                        ShowUnsupportedImageMessage();
                        return;
                    }

                    string destination = Path.Combine(
                        backgroundStore.CacheDirectory,
                        "local." + extension);
                    File.Copy(dialog.FileName, destination, true);
                    backgroundSettings.Mode = BackgroundModes.Image;
                    backgroundSettings.Image.Source = BackgroundImageSources.Local;
                    backgroundSettings.Image.Path = destination;
                    backgroundSettings.Image.OriginalUrl = String.Empty;
                    SetStatus("本地背景已应用。");
                    SaveAndApplyBackground();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "背景图片载入失败：" + Environment.NewLine + ex.Message,
                        "DesktopNest",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void StartUrlBackgroundDownload(bool force)
        {
            if (downloadingBackground)
            {
                return;
            }

            string url = urlTextBox.Text.Trim();
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(
                    this,
                    "请输入有效的 http 或 https 图片链接。",
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!force
                && String.Equals(
                    backgroundSettings.Image.Source,
                    BackgroundImageSources.Url,
                    StringComparison.Ordinal)
                && String.Equals(
                    backgroundSettings.Image.OriginalUrl,
                    url,
                    StringComparison.Ordinal)
                && File.Exists(backgroundSettings.Image.Path))
            {
                backgroundSettings.Mode = BackgroundModes.Image;
                SetStatus("已使用本地缓存。");
                SaveAndApplyBackground();
                return;
            }

            downloadingBackground = true;
            urlDownloadButton.Enabled = false;
            SetStatus("下载中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                string downloadedPath = DownloadBackgroundUrl(url, out error);
                if (IsDisposed)
                {
                    return;
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        CompleteUrlDownload(url, downloadedPath, error);
                    }));
                }
                catch
                {
                }
            });
        }

        private string DownloadBackgroundUrl(string url, out string error)
        {
            error = null;
            string tempPath = Path.Combine(
                backgroundStore.CacheDirectory,
                "url_download." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 15000;
                request.ReadWriteTimeout = 30000;
                request.UserAgent = "DesktopNest/1.0";

                string contentExtension = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (FileStream fileStream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    contentExtension = GetExtensionFromContentType(response.ContentType);
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fileStream.Write(buffer, 0, read);
                    }
                }

                string detectedExtension = DetectImageExtension(tempPath);
                if (String.IsNullOrWhiteSpace(detectedExtension))
                {
                    throw new InvalidDataException("链接内容不是支持的图片格式。");
                }

                if (String.IsNullOrWhiteSpace(contentExtension))
                {
                    contentExtension = detectedExtension;
                }

                string cachePath = Path.Combine(
                    backgroundStore.CacheDirectory,
                    "url_cache." + contentExtension);
                File.Copy(tempPath, cachePath, true);
                TryDeleteFile(tempPath);
                return cachePath;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDeleteFile(tempPath);
                return null;
            }
        }

        private void CompleteUrlDownload(string url, string cachedPath, string error)
        {
            downloadingBackground = false;
            urlDownloadButton.Enabled = true;

            if (String.IsNullOrWhiteSpace(cachedPath))
            {
                SetStatus("下载失败");
                MessageBox.Show(
                    this,
                    "下载失败，请检查链接或网络。"
                        + Environment.NewLine
                        + (error ?? String.Empty),
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            backgroundSettings.Mode = BackgroundModes.Image;
            backgroundSettings.Image.Source = BackgroundImageSources.Url;
            backgroundSettings.Image.Path = cachedPath;
            backgroundSettings.Image.OriginalUrl = url;
            SetStatus("链接背景已下载并应用。");
            SaveAndApplyBackground();
        }

        private void UseDesktopWallpaper()
        {
            try
            {
                string wallpaperPath = NativeMethods.GetDesktopWallpaper();
                if (String.IsNullOrWhiteSpace(wallpaperPath) || !File.Exists(wallpaperPath))
                {
                    MessageBox.Show(
                        this,
                        "未能获取桌面壁纸",
                        "DesktopNest",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                string extension = DetectImageExtension(wallpaperPath);
                if (String.IsNullOrWhiteSpace(extension))
                {
                    extension = NormalizeImageExtension(Path.GetExtension(wallpaperPath));
                }

                if (String.IsNullOrWhiteSpace(extension))
                {
                    throw new InvalidDataException("桌面壁纸格式不受支持。");
                }

                string destination = Path.Combine(
                    backgroundStore.CacheDirectory,
                    "wallpaper." + extension);
                File.Copy(wallpaperPath, destination, true);
                backgroundSettings.Mode = BackgroundModes.Image;
                backgroundSettings.Image.Source = BackgroundImageSources.Wallpaper;
                backgroundSettings.Image.Path = destination;
                backgroundSettings.Image.OriginalUrl = String.Empty;
                SetStatus("已同步当前桌面壁纸。");
                SaveAndApplyBackground();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "未能获取桌面壁纸"
                        + Environment.NewLine
                        + ex.Message,
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void SetStatus(string text)
        {
            if (backgroundStatusLabel != null)
            {
                backgroundStatusLabel.Text = text;
            }
        }

        private void ShowUnsupportedImageMessage()
        {
            MessageBox.Show(
                this,
                "不支持的文件格式",
                "DesktopNest",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static Color ParseColor(string value, Color fallback)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(value))
                {
                    return fallback;
                }

                return ColorTranslator.FromHtml(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static string ColorToHex(Color color)
        {
            return String.Format(
                "#{0:X2}{1:X2}{2:X2}",
                color.R,
                color.G,
                color.B);
        }

        private static Color GetContrastingTextColor(Color color)
        {
            int brightness = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            return brightness >= 150 ? Color.FromArgb(31, 41, 55) : Color.White;
        }

        private static string GetExtensionFromContentType(string contentType)
        {
            string value = (contentType ?? String.Empty).Split(';')[0].Trim().ToLowerInvariant();
            if (value == "image/jpeg" || value == "image/jpg" || value == "image/pjpeg")
            {
                return "jpg";
            }

            if (value == "image/png" || value == "image/apng")
            {
                return "png";
            }

            if (value == "image/gif")
            {
                return "gif";
            }

            if (value == "image/webp")
            {
                return "webp";
            }

            if (value == "image/bmp" || value == "image/x-ms-bmp")
            {
                return "bmp";
            }

            return null;
        }

        private static string NormalizeImageExtension(string extension)
        {
            string value = (extension ?? String.Empty).Trim().ToLowerInvariant();
            if (value == ".jpeg")
            {
                return "jpg";
            }

            if (value == ".apng")
            {
                return "png";
            }

            if (value == ".jpg"
                || value == ".png"
                || value == ".webp"
                || value == ".bmp"
                || value == ".gif")
            {
                return value.Substring(1);
            }

            return null;
        }

        private static string DetectImageExtension(string path)
        {
            byte[] header = new byte[16];
            int length;
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                length = stream.Read(header, 0, header.Length);
            }

            if (length >= 3
                && header[0] == 0xFF
                && header[1] == 0xD8
                && header[2] == 0xFF)
            {
                return "jpg";
            }

            if (length >= 8
                && header[0] == 0x89
                && header[1] == 0x50
                && header[2] == 0x4E
                && header[3] == 0x47
                && header[4] == 0x0D
                && header[5] == 0x0A
                && header[6] == 0x1A
                && header[7] == 0x0A)
            {
                return "png";
            }

            if (length >= 6
                && header[0] == 0x47
                && header[1] == 0x49
                && header[2] == 0x46
                && header[3] == 0x38
                && (header[4] == 0x37 || header[4] == 0x39)
                && header[5] == 0x61)
            {
                return "gif";
            }

            if (length >= 2 && header[0] == 0x42 && header[1] == 0x4D)
            {
                return "bmp";
            }

            if (length >= 12
                && header[0] == 0x52
                && header[1] == 0x49
                && header[2] == 0x46
                && header[3] == 0x46
                && header[8] == 0x57
                && header[9] == 0x45
                && header[10] == 0x42
                && header[11] == 0x50)
            {
                return "webp";
            }

            return null;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private void RefreshGrid()
        {
            grid.SetItems(settings.Items);
            LayoutGrid();
            if (Visible)
            {
                EnsureAllItemsVisible();
                LayoutGrid();
            }
            grid.Invalidate();
        }

        private void EnsureAllItemsVisible()
        {
            if (WindowState == FormWindowState.Maximized || settings.Items.Count == 0)
            {
                return;
            }

            if (Width < 640)
            {
                Width = 640;
                PerformLayout();
                LayoutGrid();
            }

            int viewportWidth = Math.Max(1, scrollHost.ClientSize.Width);
            int desiredClientHeight = grid.CalculateContentHeight(
                settings.Items.Count,
                viewportWidth);
            int chromeHeight = Math.Max(0, Height - ClientSize.Height);
            int desiredHeight = desiredClientHeight + chromeHeight;
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            desiredHeight = Math.Min(desiredHeight, workingArea.Height);

            if (desiredHeight <= Height)
            {
                return;
            }

            if (Top + desiredHeight > workingArea.Bottom)
            {
                Top = Math.Max(workingArea.Top, workingArea.Bottom - desiredHeight);
            }

            Height = desiredHeight;
        }

        private void LayoutGrid()
        {
            if (scrollHost == null || grid == null)
            {
                return;
            }

            int width = Math.Max(1, scrollHost.ClientSize.Width);
            int contentHeight = grid.CalculateContentHeight(settings.Items.Count, width);
            int height = Math.Max(scrollHost.ClientSize.Height, contentHeight);

            grid.SetBounds(0, 0, width, height);
            scrollHost.AutoScrollMinSize = new Size(0, height);
        }

        private void ScheduleSave()
        {
            if (saveTimer != null && Visible)
            {
                saveTimer.Stop();
                saveTimer.Start();
            }
        }

        private void SaveSettings(bool showError)
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal
                    ? Bounds
                    : RestoreBounds;

                if (bounds.Width >= MinimumSize.Width && bounds.Height >= MinimumSize.Height)
                {
                    settings.SetBounds(bounds);
                }

                settings.IsMaximized = WindowState == FormWindowState.Maximized;
                settingsStore.Save(settings);
                saveErrorShown = false;
            }
            catch (Exception ex)
            {
                if (showError || !saveErrorShown)
                {
                    saveErrorShown = true;
                    MessageBox.Show(
                        this,
                        "保存设置失败：" + Environment.NewLine + ex.Message
                            + Environment.NewLine + Environment.NewLine
                            + settingsStore.SettingsPath,
                        "DesktopNest",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null
                && e.Data.GetDataPresent(DataFormats.FileDrop)
                && (e.AllowedEffect & DragDropEffects.Copy) == DragDropEffects.Copy)
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            ImportPaths(paths);
        }

        private void ImportPaths(string[] paths)
        {
            int added = 0;
            List<string> errors = new List<string>();
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    if (settings.Items.Count >= settings.Capacity)
                    {
                        errors.Add("收纳容量已满（" + settings.Capacity
                            + " 个）。可点标题栏书包按钮调大容量。");
                        break;
                    }

                    try
                    {
                        ImportedShortcut imported = importer.Import(paths[i]);
                        settings.Items.Add(imported.Item);
                        QueueSourceMove(imported.Item);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        string name = Path.GetFileName(paths[i]);
                        if (String.IsNullOrWhiteSpace(name))
                        {
                            name = paths[i];
                        }

                        errors.Add(name + "：" + ex.Message);
                    }
                }
            }
            finally
            {
                Cursor = previousCursor;
            }

            if (added > 0)
            {
                RefreshGrid();
                SaveSettings(true);
                StartSourceMoveTimer();
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(
                    this,
                    "以下项目添加失败：" + Environment.NewLine + Environment.NewLine
                        + String.Join(Environment.NewLine, errors.ToArray()),
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void QueueSourceMove(ShortcutItem item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.OriginalPath))
            {
                return;
            }

            PendingSourceMove pending = new PendingSourceMove();
            pending.SourcePath = item.OriginalPath;
            pending.RemainingAttempts = 8;
            pendingSourceMoves.Add(pending);
        }

        private void StartSourceMoveTimer()
        {
            if (pendingSourceMoves.Count == 0)
            {
                return;
            }

            sourceMoveTimer.Stop();
            sourceMoveTimer.Start();
        }

        private void ProcessPendingSourceMoves()
        {
            List<PendingSourceMove> retry = new List<PendingSourceMove>();
            for (int i = 0; i < pendingSourceMoves.Count; i++)
            {
                PendingSourceMove pending = pendingSourceMoves[i];
                if (!File.Exists(pending.SourcePath) && !Directory.Exists(pending.SourcePath))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(pending.SourcePath))
                    {
                        File.Delete(pending.SourcePath);
                    }
                    else
                    {
                        Directory.Delete(pending.SourcePath, true);
                    }
                }
                catch
                {
                    pending.RemainingAttempts--;
                    if (pending.RemainingAttempts > 0)
                    {
                        retry.Add(pending);
                    }
                    else
                    {
                        MessageBox.Show(
                            this,
                            "项目已收纳，但无法从桌面移除：" + Environment.NewLine
                                + pending.SourcePath,
                            "DesktopNest",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }

            pendingSourceMoves.Clear();
            pendingSourceMoves.AddRange(retry);
            if (pendingSourceMoves.Count > 0)
            {
                sourceMoveTimer.Start();
            }
        }

        private void LaunchItem(int index)
        {
            if (index < 0 || index >= settings.Items.Count)
            {
                return;
            }

            ShortcutItem item = settings.Items[index];
            string storedPath = settingsStore.GetStoredPath(item);
            LaunchResult result = launcher.Open(storedPath, item.TargetPathHint);
            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.ErrorMessage,
                    item.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void ShowItemContextMenu(int index, Point screenLocation)
        {
            if (index < 0 || index >= settings.Items.Count)
            {
                return;
            }

            ShortcutItem item = settings.Items[index];
            ContextMenuStrip menu = new ContextMenuStrip();
            // 注意：不要在 Closed 事件里 Dispose 菜单。WinForms 在 Closed 触发之后
            // 内部仍会继续访问该菜单对象，立即释放会抛出 ObjectDisposedException
            // （"无法访问已释放的对象: ContextMenuStrip"）。菜单失去引用后由 GC 回收。
            menu.Items.Add("打开", null, delegate { LaunchItem(index); });
            menu.Items.Add("重命名", null, delegate { RenameItem(index); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("移出收纳", null, delegate { RemoveItem(index); });
            menu.Show(screenLocation);
        }

        private void ShowBlankContextMenu(Point screenLocation)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            // 同上：不要在 Closed 里 Dispose，交给 GC 回收。
            menu.Items.Add("从文件添加", null, delegate { AddFromFileDialog(); });
            menu.Items.Add("一键移出", null, delegate { MoveAllItemsOut(); });
            menu.Items.Add("打开数据目录", null, delegate { OpenDataDirectory(); });
            menu.Show(screenLocation);
        }

        private void AddFromFileDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要添加到 DesktopNest 的文件或文件夹";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.Filter = "支持的快捷方式和文件|*.lnk;*.exe;*.url|所有文件|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    ImportPaths(dialog.FileNames);
                }
            }
        }

        private void OpenDataDirectory()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "explorer.exe";
                startInfo.Arguments = "\"" + settingsStore.RootDirectory + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "打开数据目录失败：" + Environment.NewLine + ex.Message,
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void RenameTitle()
        {
            using (TextInputDialog dialog = new TextInputDialog(
                "修改工具名称",
                "名称",
                settings.Title,
                AppConstants.DefaultTitle))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    settings.Title = dialog.Value;
                    Text = settings.Title;
                    SaveSettings(true);
                }
            }
        }

        private void RenameItem(int index)
        {
            if (index < 0 || index >= settings.Items.Count)
            {
                return;
            }

            ShortcutItem item = settings.Items[index];
            using (TextInputDialog dialog = new TextInputDialog(
                "重命名收纳项",
                "显示名称",
                item.DisplayName,
                "未命名"))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    item.DisplayName = dialog.Value;
                    RefreshGrid();
                    SaveSettings(true);
                }
            }
        }

        private void RemoveItem(int index)
        {
            if (index < 0 || index >= settings.Items.Count)
            {
                return;
            }

            ShortcutItem item = settings.Items[index];
            string storedPath = settingsStore.GetStoredPath(item);
            CancelPendingSourceMove(item.OriginalPath);

            try
            {
                string destination = GetDesktopDestination(item, storedPath);
                if (destination == null)
                {
                    DeleteStoredItem(storedPath);
                }
                else
                {
                    MoveStoredItemTo(storedPath, destination);
                }

                settings.Items.RemoveAt(index);
                RefreshGrid();
                SaveSettings(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "移出收纳失败：" + Environment.NewLine + ex.Message,
                    item.DisplayName,
                    MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            }
        }

        private void MoveAllItemsOut()
        {
            if (settings.Items.Count == 0)
            {
                return;
            }

            List<string> failures = new List<string>();
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;

            try
            {
                for (int i = settings.Items.Count - 1; i >= 0; i--)
                {
                    ShortcutItem item = settings.Items[i];
                    string storedPath = settingsStore.GetStoredPath(item);
                    CancelPendingSourceMove(item.OriginalPath);

                    try
                    {
                        string destination = GetDesktopDestination(item, storedPath);
                        if (destination == null)
                        {
                            DeleteStoredItem(storedPath);
                        }
                        else
                        {
                            MoveStoredItemTo(storedPath, destination);
                        }

                        settings.Items.RemoveAt(i);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(item.DisplayName + "：" + ex.Message);
                    }
                }
            }
            finally
            {
                Cursor = previousCursor;
            }

            RefreshGrid();
            SaveSettings(true);

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    this,
                    "以下项目未能移出，已保留在收纳袋中："
                        + Environment.NewLine
                        + Environment.NewLine
                        + String.Join(Environment.NewLine, failures.ToArray()),
                    "DesktopNest",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string GetDesktopDestination(
            ShortcutItem item,
            string storedPath)
        {
            string desktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (String.IsNullOrWhiteSpace(desktop))
            {
                throw new DirectoryNotFoundException("找不到当前用户的桌面目录。");
            }

            if (!String.IsNullOrWhiteSpace(item.OriginalPath)
                && IsOnUserDesktop(item.OriginalPath))
            {
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
                {
                    return null;
                }

                return Path.GetFullPath(item.OriginalPath);
            }

            string displayName = item.DisplayName;
            if (String.IsNullOrWhiteSpace(displayName))
            {
                displayName = "未命名";
            }

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalidCharacters.Length; i++)
            {
                displayName = displayName.Replace(invalidCharacters[i], '_');
            }

            string extension = Directory.Exists(storedPath)
                ? String.Empty
                : Path.GetExtension(storedPath);
            return GetUniquePath(Path.Combine(desktop, displayName + extension));
        }

        private static bool IsOnUserDesktop(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string desktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (String.IsNullOrWhiteSpace(desktop))
            {
                return false;
            }

            string root = Path.GetFullPath(desktop)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetUniquePath(string desiredPath)
        {
            if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
            {
                return desiredPath;
            }

            string directory = Path.GetDirectoryName(desiredPath);
            string fileName = Path.GetFileNameWithoutExtension(desiredPath);
            string extension = Path.GetExtension(desiredPath);
            for (int index = 2; ; index++)
            {
                string candidate = Path.Combine(
                    directory,
                    fileName + " (" + index + ")" + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        private void CancelPendingSourceMove(string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            for (int i = pendingSourceMoves.Count - 1; i >= 0; i--)
            {
                if (String.Equals(
                    pendingSourceMoves[i].SourcePath,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    pendingSourceMoves.RemoveAt(i);
                }
            }
        }

        private static void MoveStoredItemTo(string storedPath, string destinationPath)
        {
            string destination = Path.GetFullPath(destinationPath);
            string destinationDirectory = Path.GetDirectoryName(destination);
            if (!String.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (Directory.Exists(storedPath))
            {
                MoveDirectory(storedPath, destination);
            }
            else if (File.Exists(storedPath))
            {
                MoveFile(storedPath, destination);
            }
            else
            {
                throw new FileNotFoundException("收纳中的项目已丢失。", storedPath);
            }
        }

        private static void MoveFile(string sourcePath, string destinationPath)
        {
            try
            {
                File.Move(sourcePath, destinationPath);
            }
            catch (IOException)
            {
                File.Copy(sourcePath, destinationPath, false);
                File.Delete(sourcePath);
            }
        }

        private static void MoveDirectory(string sourcePath, string destinationPath)
        {
            try
            {
                Directory.Move(sourcePath, destinationPath);
            }
            catch (IOException)
            {
                CopyDirectory(sourcePath, destinationPath);
                Directory.Delete(sourcePath, true);
            }
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            string[] files = Directory.GetFiles(sourcePath);
            for (int i = 0; i < files.Length; i++)
            {
                string destinationFile = Path.Combine(
                    destinationPath,
                    Path.GetFileName(files[i]));
                File.Copy(files[i], destinationFile, false);
            }

            string[] directories = Directory.GetDirectories(sourcePath);
            for (int i = 0; i < directories.Length; i++)
            {
                string destinationDirectory = Path.Combine(
                    destinationPath,
                    Path.GetFileName(directories[i]));
                CopyDirectory(directories[i], destinationDirectory);
            }
        }

        private static void DeleteStoredItem(string storedPath)
        {
            if (File.Exists(storedPath))
            {
                File.Delete(storedPath);
            }
            else if (Directory.Exists(storedPath))
            {
                Directory.Delete(storedPath, true);
            }
        }

        private sealed class TransparentPanel : Panel
        {
            public TransparentPanel()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                        | ControlStyles.OptimizedDoubleBuffer
                        | ControlStyles.UserPaint
                        | ControlStyles.ResizeRedraw
                        | ControlStyles.SupportsTransparentBackColor,
                    true);
                BackColor = Color.Transparent;
                DoubleBuffered = true;
            }
        }

        private sealed class IconGrid : Control
        {
            private const double BaseTileWidth = 72;
            private const double BaseTileHeight = 82;
            private const double BaseOuterPadding = 16;
            private const double BaseIconSize = 40;

            private readonly IconService iconService;
            private readonly AppSettingsStore settingsStore;
            private readonly ToolTip toolTip;
            private List<ShortcutItem> items;
            private bool freeArrange;
            private double iconScale = 1.0;
            private int hoveredIndex = -1;

            // 内部拖拽状态：按住图标 → 超过系统拖拽阈值进入拖拽 → 松手落位
            private bool dragPending;
            private bool dragging;
            private int dragIndex = -1;
            private Point dragStartPoint;
            private Point dragCurrentPoint;
            private Point dragGrabOffset;

            // 布局缓存：列数或条目数变化时重算
            private Point[] resolvedPositions;
            private int resolvedColumns = -1;
            private int resolvedItemCount = -1;
            private Font textFont;

            public IconGrid(IconService iconService, AppSettingsStore settingsStore)
            {
                this.iconService = iconService;
                this.settingsStore = settingsStore;
                items = new List<ShortcutItem>();
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                    | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;

                toolTip = new ToolTip();
                toolTip.InitialDelay = 350;
                toolTip.ReshowDelay = 100;
                toolTip.ShowAlways = true;
                UpdateTextFont();
            }

            public event Action<int> ItemActivated;
            public event Action<int, Point> ItemContextRequested;
            public event Action<Point> BlankContextRequested;
            public event Action ItemLayoutChanged;

            public void SetItems(List<ShortcutItem> value)
            {
                items = value ?? new List<ShortcutItem>();
                hoveredIndex = -1;
                CancelDrag();
                ResetLayoutCache();
                toolTip.SetToolTip(this, String.Empty);
            }

            public void SetLayoutSettings(bool free, double scale)
            {
                if (Double.IsNaN(scale) || scale <= 0)
                {
                    scale = 1.0;
                }

                if (scale < 0.5)
                {
                    scale = 0.5;
                }

                if (scale > 2.0)
                {
                    scale = 2.0;
                }

                bool changed = free != freeArrange
                    || Math.Abs(scale - iconScale) > 0.0001;
                if (!changed)
                {
                    return;
                }

                freeArrange = free;
                iconScale = scale;
                ResetLayoutCache();
                UpdateTextFont();
                Invalidate();
            }

            public int CalculateContentHeight(int itemCount, int width)
            {
                int rows;
                if (freeArrange && items.Count > 0)
                {
                    Point[] positions = ResolvePositions(CalculateColumns(width));
                    int maxRow = 0;
                    for (int i = 0; i < positions.Length; i++)
                    {
                        if (positions[i].Y > maxRow)
                        {
                            maxRow = positions[i].Y;
                        }
                    }

                    rows = maxRow + 1;
                }
                else
                {
                    int columns = CalculateColumns(width);
                    rows = itemCount <= 0 ? 0 : (itemCount + columns - 1) / columns;
                }

                return Math.Max(120, PaddingSize * 2 + rows * TileHeight);
            }

            private int TileWidth
            {
                get { return Math.Max(24, (int)Math.Round(BaseTileWidth * iconScale)); }
            }

            private int TileHeight
            {
                get { return Math.Max(28, (int)Math.Round(BaseTileHeight * iconScale)); }
            }

            private int PaddingSize
            {
                get { return Math.Max(4, (int)Math.Round(BaseOuterPadding * iconScale)); }
            }

            private int IconSize
            {
                get { return Math.Max(12, (int)Math.Round(BaseIconSize * iconScale)); }
            }

            private int IconTopOffset
            {
                get { return Math.Max(2, (int)Math.Round(10 * iconScale)); }
            }

            private int NameGap
            {
                get { return Math.Max(2, (int)Math.Round(6 * iconScale)); }
            }

            private int NameHeight
            {
                get { return Math.Max(14, (int)Math.Round(22 * iconScale)); }
            }

            private int Columns
            {
                get { return CalculateColumns(ClientSize.Width); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                if (items.Count == 0)
                {
                    int promptWidth = Math.Min(300, Math.Max(1, ClientSize.Width - 48));
                    Rectangle promptCard = new Rectangle(
                        (ClientSize.Width - promptWidth) / 2,
                        Math.Max(24, (ClientSize.Height - 72) / 2),
                        promptWidth,
                        72);
                    using (GraphicsPath path = CreateRoundedRectangle(promptCard, 10))
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(218, 255, 255, 255)))
                    using (Pen pen = new Pen(Color.FromArgb(80, 255, 255, 255)))
                    {
                        e.Graphics.FillPath(brush, path);
                        e.Graphics.DrawPath(pen, path);
                    }

                    Rectangle textBounds = new Rectangle(
                        promptCard.X + 12,
                        promptCard.Y + 8,
                        Math.Max(1, promptCard.Width - 24),
                        promptCard.Height - 16);
                    TextRenderer.DrawText(
                        e.Graphics,
                        "将桌面快捷方式拖到这里",
                        Font,
                        textBounds,
                        Color.FromArgb(75, 85, 99),
                        TextFormatFlags.HorizontalCenter
                            | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.WordBreak);
                    return;
                }

                Point[] positions = ResolvePositions(Columns);

                // 拖拽中：先高亮落点格子
                if (dragging)
                {
                    Point targetCell = GetCellAt(dragCurrentPoint, true);
                    if (targetCell.X >= 0 && targetCell.Y >= 0)
                    {
                        Rectangle targetRect = GetTileBounds(targetCell);
                        targetRect.Inflate(-3, -3);
                        using (GraphicsPath path = CreateRoundedRectangle(targetRect, 8))
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(90, 213, 230, 250)))
                        using (Pen pen = new Pen(Color.FromArgb(150, 88, 140, 220)))
                        {
                            pen.DashStyle = DashStyle.Dash;
                            e.Graphics.FillPath(brush, path);
                            e.Graphics.DrawPath(pen, path);
                        }
                    }
                }

                for (int i = 0; i < items.Count; i++)
                {
                    if (dragging && i == dragIndex)
                    {
                        continue; // 拖拽中的项最后单独画（浮在顶层）
                    }

                    Rectangle tile = GetTileBounds(positions[i]);
                    if (!tile.IntersectsWith(ClientRectangle))
                    {
                        continue;
                    }

                    DrawItem(e.Graphics, i, tile, i == hoveredIndex);
                }

                if (dragging && dragIndex >= 0 && dragIndex < items.Count)
                {
                    Rectangle floatRect = new Rectangle(
                        dragCurrentPoint.X - dragGrabOffset.X,
                        dragCurrentPoint.Y - dragGrabOffset.Y,
                        TileWidth,
                        TileHeight);
                    using (GraphicsPath shadowPath = CreateRoundedRectangle(
                        new Rectangle(floatRect.X + 4, floatRect.Y + 4, floatRect.Width - 8, floatRect.Height - 8),
                        8))
                    using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(36, 0, 0, 0)))
                    {
                        e.Graphics.FillPath(shadowBrush, shadowPath);
                    }

                    using (GraphicsPath cardPath = CreateRoundedRectangle(
                        new Rectangle(floatRect.X + 2, floatRect.Y + 2, floatRect.Width - 4, floatRect.Height - 4),
                        8))
                    using (SolidBrush cardBrush = new SolidBrush(Color.FromArgb(206, 255, 255, 255)))
                    {
                        e.Graphics.FillPath(cardBrush, cardPath);
                    }

                    DrawItemContent(e.Graphics, dragIndex, floatRect);
                }
            }

            private void DrawItem(Graphics graphics, int index, Rectangle tile, bool hovered)
            {
                if (hovered)
                {
                    using (GraphicsPath path = CreateRoundedRectangle(
                        new Rectangle(tile.X + 4, tile.Y + 4, tile.Width - 8, tile.Height - 8),
                        8))
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(226, 235, 247)))
                    {
                        graphics.FillPath(brush, path);
                    }
                }

                DrawItemContent(graphics, index, tile);
            }

            private void DrawItemContent(Graphics graphics, int index, Rectangle tile)
            {
                ShortcutItem item = items[index];
                string storedPath = GetStoredPath(item);
                bool missing = String.IsNullOrWhiteSpace(storedPath)
                    || (!File.Exists(storedPath) && !Directory.Exists(storedPath));
                Rectangle iconBounds = new Rectangle(
                    tile.X + (tile.Width - IconSize) / 2,
                    tile.Y + IconTopOffset,
                    IconSize,
                    IconSize);

                graphics.DrawIcon(iconService.GetIcon(storedPath), iconBounds);

                Rectangle nameBounds = new Rectangle(
                    tile.X + 4,
                    iconBounds.Bottom + NameGap,
                    tile.Width - 8,
                    NameHeight);
                Color textColor = missing
                    ? Color.FromArgb(184, 80, 80)
                    : Color.FromArgb(45, 52, 64);
                TextRenderer.DrawText(
                    graphics,
                    item.DisplayName,
                    textFont ?? Font,
                    nameBounds,
                    textColor,
                    TextFormatFlags.HorizontalCenter
                        | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine
                        | TextFormatFlags.EndEllipsis
                        | TextFormatFlags.NoPrefix);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragPending && !dragging && e.Button == MouseButtons.Left)
                {
                    if (dragIndex < 0 || dragIndex >= items.Count)
                    {
                        dragPending = false;
                    }
                    else
                    {
                        Size dragSize = SystemInformation.DragSize;
                        if (Math.Abs(e.X - dragStartPoint.X) >= dragSize.Width
                            || Math.Abs(e.Y - dragStartPoint.Y) >= dragSize.Height)
                        {
                            Point[] positions = ResolvePositions(Columns);
                            Rectangle tile = GetTileBounds(positions[dragIndex]);
                            dragGrabOffset = new Point(
                                dragStartPoint.X - tile.X,
                                dragStartPoint.Y - tile.Y);
                            dragging = true;
                            dragCurrentPoint = e.Location;
                            Capture = true;
                            Cursor = Cursors.SizeAll;
                            toolTip.SetToolTip(this, String.Empty);
                        }
                    }
                }
                else if (dragging && e.Button == MouseButtons.Left)
                {
                    dragCurrentPoint = e.Location;
                    Invalidate();
                    return;
                }

                int index = GetIndexAt(e.Location);
                if (index != hoveredIndex)
                {
                    hoveredIndex = index;
                    toolTip.SetToolTip(
                        this,
                        index >= 0
                            ? items[index].DisplayName
                            : String.Empty);
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (dragging)
                {
                    return; // 拖拽中捕获了鼠标，保持现状即可
                }

                hoveredIndex = -1;
                toolTip.SetToolTip(this, String.Empty);
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    int index = GetIndexAt(e.Location);
                    if (index >= 0)
                    {
                        dragPending = true;
                        dragIndex = index;
                        dragStartPoint = e.Location;
                    }
                }
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                if (dragging || dragPending)
                {
                    return;
                }

                if (e.Button == MouseButtons.Left)
                {
                    int index = GetIndexAt(e.Location);
                    if (index >= 0 && ItemActivated != null)
                    {
                        ItemActivated(index);
                    }
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (dragging && e.Button == MouseButtons.Left)
                {
                    dragCurrentPoint = e.Location;
                    FinishDrag();
                    return;
                }

                dragPending = false;
                if (e.Button != MouseButtons.Right)
                {
                    return;
                }

                int index = GetIndexAt(e.Location);
                if (index >= 0)
                {
                    if (ItemContextRequested != null)
                    {
                        ItemContextRequested(index, PointToScreen(e.Location));
                    }
                }
                else if (BlankContextRequested != null)
                {
                    BlankContextRequested(PointToScreen(e.Location));
                }
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (dragging)
                {
                    // 捕获意外丢失（如拖拽被系统打断）→ 收尾，避免卡在拖拽态
                    FinishDrag();
                }
            }

            private void FinishDrag()
            {
                bool wasDragging = dragging;
                dragging = false;
                dragPending = false;
                Cursor = Cursors.Default;
                if (Capture)
                {
                    Capture = false;
                }

                if (wasDragging && dragIndex >= 0 && dragIndex < items.Count)
                {
                    CommitDrag();
                }

                Invalidate();
            }

            private void CommitDrag()
            {
                int index = dragIndex;
                dragIndex = -1;
                if (index < 0 || index >= items.Count)
                {
                    return;
                }

                if (freeArrange)
                {
                    Point targetCell = GetCellAt(dragCurrentPoint, true);
                    if (targetCell.X < 0 || targetCell.Y < 0)
                    {
                        return;
                    }

                    ShortcutItem dragged = items[index];
                    Point[] positions = ResolvePositions(Columns);
                    int occupant = -1;
                    for (int i = 0; i < positions.Length; i++)
                    {
                        if (i != index
                            && positions[i].X == targetCell.X
                            && positions[i].Y == targetCell.Y)
                        {
                            occupant = i;
                            break;
                        }
                    }

                    if (occupant >= 0)
                    {
                        // 目标格被占 → 两个图标换位；被占者原来没有固定格就交给自动分配
                        ShortcutItem displaced = items[occupant];
                        if (dragged.Col >= 0 && dragged.Row >= 0)
                        {
                            displaced.Col = dragged.Col;
                            displaced.Row = dragged.Row;
                        }
                        else
                        {
                            displaced.Col = -1;
                            displaced.Row = -1;
                        }
                    }

                    dragged.Col = targetCell.X;
                    dragged.Row = targetCell.Y;
                    ResetLayoutCache();
                    RaiseLayoutChanged();
                }
                else
                {
                    // 紧凑模式：拖到哪个图标上，就插入到那个位置（其余依次顺移）
                    int targetIndex = GetIndexAt(dragCurrentPoint);
                    if (targetIndex < 0 || targetIndex == index)
                    {
                        return;
                    }

                    ShortcutItem moved = items[index];
                    items.RemoveAt(index);
                    items.Insert(targetIndex, moved);
                    ResetLayoutCache();
                    RaiseLayoutChanged();
                }
            }

            private void RaiseLayoutChanged()
            {
                Action handler = ItemLayoutChanged;
                if (handler != null)
                {
                    handler();
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    toolTip.Dispose();
                    if (textFont != null)
                    {
                        textFont.Dispose();
                        textFont = null;
                    }
                }

                base.Dispose(disposing);
            }

            private int CalculateColumns(int width)
            {
                return Math.Max(1, (Math.Max(1, width) - PaddingSize * 2) / TileWidth);
            }

            private Rectangle GetTileBounds(Point cell)
            {
                return new Rectangle(
                    PaddingSize + cell.X * TileWidth,
                    PaddingSize + cell.Y * TileHeight,
                    TileWidth,
                    TileHeight);
            }

            private Point GetCellAt(Point point, bool clampToGrid)
            {
                int columns = Columns;
                int cellX = (int)Math.Floor((double)(point.X - PaddingSize) / TileWidth);
                int cellY = (int)Math.Floor((double)(point.Y - PaddingSize) / TileHeight);
                if (clampToGrid)
                {
                    if (cellX < 0)
                    {
                        cellX = 0;
                    }

                    if (cellX >= columns)
                    {
                        cellX = columns - 1;
                    }

                    if (cellY < 0)
                    {
                        cellY = 0;
                    }

                    return new Point(cellX, cellY);
                }

                if (cellX < 0 || cellY < 0 || cellX >= columns)
                {
                    return new Point(-1, -1);
                }

                return new Point(cellX, cellY);
            }

            private int GetIndexAt(Point point)
            {
                if (items.Count == 0)
                {
                    return -1;
                }

                Point cell = GetCellAt(point, false);
                if (cell.X < 0)
                {
                    return -1;
                }

                Point[] positions = ResolvePositions(Columns);
                for (int i = 0; i < positions.Length; i++)
                {
                    if (positions[i].X == cell.X && positions[i].Y == cell.Y)
                    {
                        return i;
                    }
                }

                return -1;
            }

            // 计算每个图标的 (列, 行)。紧凑模式按列表顺序铺；
            // 自由模式优先用存储的 Col/Row，未定位或冲突的交给自动找空位
            private Point[] ResolvePositions(int columns)
            {
                if (resolvedPositions != null
                    && resolvedColumns == columns
                    && resolvedItemCount == items.Count)
                {
                    return resolvedPositions;
                }

                Point[] positions = new Point[items.Count];
                if (!freeArrange)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        positions[i] = new Point(i % columns, i / columns);
                    }
                }
                else
                {
                    Dictionary<long, int> occupied = new Dictionary<long, int>();
                    bool[] placed = new bool[items.Count];
                    int maxRow = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        ShortcutItem item = items[i];
                        if (item.Col < 0 || item.Row < 0)
                        {
                            continue;
                        }

                        int column = Math.Min(item.Col, columns - 1);
                        long key = CellKey(column, item.Row);
                        if (occupied.ContainsKey(key))
                        {
                            continue;
                        }

                        positions[i] = new Point(column, item.Row);
                        occupied[key] = i;
                        placed[i] = true;
                        if (item.Row > maxRow)
                        {
                            maxRow = item.Row;
                        }
                    }

                    for (int i = 0; i < items.Count; i++)
                    {
                        if (placed[i])
                        {
                            continue;
                        }

                        bool done = false;
                        int rowLimit = maxRow + items.Count + 2;
                        for (int row = 0; row <= rowLimit && !done; row++)
                        {
                            for (int column = 0; column < columns; column++)
                            {
                                long key = CellKey(column, row);
                                if (occupied.ContainsKey(key))
                                {
                                    continue;
                                }

                                positions[i] = new Point(column, row);
                                occupied[key] = i;
                                done = true;
                                if (row > maxRow)
                                {
                                    maxRow = row;
                                }

                                break;
                            }
                        }

                        if (!done)
                        {
                            positions[i] = new Point(0, maxRow + 1);
                        }
                    }
                }

                resolvedPositions = positions;
                resolvedColumns = columns;
                resolvedItemCount = items.Count;
                return positions;
            }

            private static long CellKey(int column, int row)
            {
                return (long)row * 0x40000000L + column;
            }

            private void ResetLayoutCache()
            {
                resolvedPositions = null;
                resolvedColumns = -1;
                resolvedItemCount = -1;
            }

            private void CancelDrag()
            {
                dragging = false;
                dragPending = false;
                dragIndex = -1;
            }

            private void UpdateTextFont()
            {
                Font previous = textFont;
                float size = (float)(Font.SizeInPoints * iconScale);
                if (size < 6.5F)
                {
                    size = 6.5F;
                }

                textFont = new Font(Font.FontFamily, size, Font.Style);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }

            private string GetStoredPath(ShortcutItem item)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.ShortcutFileName))
                {
                    return String.Empty;
                }

                return settingsStore.GetStoredPath(item);
            }
        }

        private sealed class BackgroundSurface : Control
        {
            private readonly WinFormsTimer animationTimer;
            private readonly Stopwatch animationClock;
            private readonly List<Bitmap> frames;
            private readonly List<int> frameDelays;
            private BackgroundSettings settings;
            private int currentFrameIndex;
            private int loadVersion;
            private bool disposed;

            public BackgroundSurface(BackgroundSettings settings)
            {
                this.settings = settings ?? BackgroundSettings.CreateDefault();
                frames = new List<Bitmap>();
                frameDelays = new List<int>();
                animationClock = new Stopwatch();
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                        | ControlStyles.OptimizedDoubleBuffer
                        | ControlStyles.UserPaint
                        | ControlStyles.ResizeRedraw
                        | ControlStyles.SupportsTransparentBackColor,
                    true);
                BackColor = Color.FromArgb(245, 247, 250);
                DoubleBuffered = true;

                animationTimer = new WinFormsTimer();
                animationTimer.Interval = 30;
                animationTimer.Tick += delegate { AdvanceAnimation(); };
                RefreshBackground();
            }

            public event Action<string> ImageLoadFailed;

            public void SetSettings(BackgroundSettings value)
            {
                settings = value ?? BackgroundSettings.CreateDefault();
                settings.EnsureValid();
                RefreshBackground();
            }

            public void RefreshBackground()
            {
                if (disposed)
                {
                    return;
                }

                int version = ++loadVersion;
                animationTimer.Stop();
                animationClock.Reset();

                if (!String.Equals(
                        settings.Mode,
                        BackgroundModes.Image,
                        StringComparison.Ordinal)
                    || settings.Image == null
                    || String.IsNullOrWhiteSpace(settings.Image.Path)
                    || !File.Exists(settings.Image.Path))
                {
                    DisposeFrames(frames);
                    frames.Clear();
                    frameDelays.Clear();
                    currentFrameIndex = 0;
                    Invalidate();
                    return;
                }

                string imagePath = settings.Image.Path;
                Size requestedSize = ClientSize;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    List<Bitmap> loadedFrames = null;
                    List<int> loadedDelays = null;
                    string error = null;

                    try
                    {
                        loadedFrames = LoadFrames(imagePath, requestedSize);
                        loadedDelays = ReadFrameDelays(imagePath, loadedFrames.Count);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    if (disposed)
                    {
                        if (loadedFrames != null)
                        {
                            DisposeFrames(loadedFrames);
                        }

                        return;
                    }

                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            if (disposed || version != loadVersion)
                            {
                                if (loadedFrames != null)
                                {
                                    DisposeFrames(loadedFrames);
                                }

                                return;
                            }

                            DisposeFrames(frames);
                            frames.Clear();
                            frameDelays.Clear();
                            currentFrameIndex = 0;

                            if (loadedFrames != null)
                            {
                                frames.AddRange(loadedFrames);
                                frameDelays.AddRange(loadedDelays);
                            }

                            if (!String.IsNullOrWhiteSpace(error) && ImageLoadFailed != null)
                            {
                                ImageLoadFailed(error);
                            }

                            if (frames.Count > 1)
                            {
                                animationClock.Restart();
                                animationTimer.Start();
                            }

                            Invalidate();
                        }));
                    }
                    catch
                    {
                        if (loadedFrames != null)
                        {
                            DisposeFrames(loadedFrames);
                        }
                    }
                });
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (String.Equals(
                    settings.Mode,
                    BackgroundModes.Gradient,
                    StringComparison.Ordinal))
                {
                    Color from = ParseColor(
                        settings.Gradient.From,
                        ColorTranslator.FromHtml(BackgroundSettings.DefaultGradientFrom));
                    Color to = ParseColor(
                        settings.Gradient.To,
                        ColorTranslator.FromHtml(BackgroundSettings.DefaultGradientTo));
                    float angle = 90F;
                    if (String.Equals(
                        settings.Gradient.Direction,
                        BackgroundDirections.BottomToTop,
                        StringComparison.Ordinal))
                    {
                        angle = 270F;
                    }
                    else if (String.Equals(
                        settings.Gradient.Direction,
                        BackgroundDirections.LeftToRight,
                        StringComparison.Ordinal))
                    {
                        angle = 0F;
                    }
                    else if (String.Equals(
                        settings.Gradient.Direction,
                        BackgroundDirections.Diagonal,
                        StringComparison.Ordinal))
                    {
                        angle = 45F;
                    }

                    using (LinearGradientBrush brush = new LinearGradientBrush(
                        ClientRectangle,
                        from,
                        to,
                        angle))
                    {
                        e.Graphics.FillRectangle(brush, ClientRectangle);
                    }

                    return;
                }

                if (String.Equals(
                        settings.Mode,
                        BackgroundModes.Image,
                        StringComparison.Ordinal)
                    && frames.Count > 0)
                {
                    Bitmap image = frames[Math.Min(currentFrameIndex, frames.Count - 1)];
                    DrawCover(e.Graphics, image);
                    return;
                }

                Color fallback = ParseColor(
                    settings.Color,
                    ColorTranslator.FromHtml(BackgroundSettings.DefaultColor));
                using (SolidBrush brush = new SolidBrush(fallback))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    disposed = true;
                    loadVersion++;
                    animationTimer.Stop();
                    animationTimer.Dispose();
                    DisposeFrames(frames);
                    frames.Clear();
                    frameDelays.Clear();
                }

                base.Dispose(disposing);
            }

            private void AdvanceAnimation()
            {
                if (frames.Count <= 1)
                {
                    return;
                }

                int delay = frameDelays.Count > currentFrameIndex
                    ? Math.Max(20, frameDelays[currentFrameIndex])
                    : 100;
                if (animationClock.ElapsedMilliseconds < delay)
                {
                    return;
                }

                animationClock.Restart();
                currentFrameIndex = (currentFrameIndex + 1) % frames.Count;
                Invalidate();
            }

            private void DrawCover(Graphics graphics, Bitmap image)
            {
                if (image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                {
                    return;
                }

                float scale = Math.Max(
                    ClientSize.Width / (float)image.Width,
                    ClientSize.Height / (float)image.Height);
                int width = Math.Max(ClientSize.Width, (int)Math.Ceiling(image.Width * scale));
                int height = Math.Max(ClientSize.Height, (int)Math.Ceiling(image.Height * scale));
                int x = (ClientSize.Width - width) / 2;
                int y = (ClientSize.Height - height) / 2;
                graphics.DrawImage(
                    image,
                    new Rectangle(x, y, width, height),
                    0,
                    0,
                    image.Width,
                    image.Height,
                    GraphicsUnit.Pixel);
            }

            private static List<Bitmap> LoadFrames(string path, Size requestedSize)
            {
                int maxWidth = Math.Max(1024, requestedSize.Width * 2);
                int maxHeight = Math.Max(1024, requestedSize.Height * 2);
                List<Bitmap> result = new List<Bitmap>();

                try
                {
                    WpfBitmapDecoder decoder = WpfBitmapDecoder.Create(
                        new Uri(path, UriKind.Absolute),
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        WpfBitmapCacheOption.OnLoad);
                    for (int i = 0; i < decoder.Frames.Count; i++)
                    {
                        WpfBitmapFrame frame = decoder.Frames[i];
                        WpfBitmapSource source = frame;
                        double scale = Math.Min(
                            1D,
                            Math.Min(
                                maxWidth / (double)Math.Max(1, frame.PixelWidth),
                                maxHeight / (double)Math.Max(1, frame.PixelHeight)));
                        if (scale < 1D)
                        {
                            WpfTransformedBitmap transformed = new WpfTransformedBitmap(
                                frame,
                                new System.Windows.Media.ScaleTransform(scale, scale));
                            transformed.Freeze();
                            source = transformed;
                        }

                        result.Add(ConvertToBitmap(source));
                    }
                }
                catch
                {
                    DisposeFrames(result);
                    throw;
                }

                return result;
            }

            private static Bitmap ConvertToBitmap(WpfBitmapSource source)
            {
                WpfBitmapSource convertedSource = source;
                if (source.Format != WpfPixelFormats.Bgra32
                    && source.Format != WpfPixelFormats.Pbgra32)
                {
                    WpfFormatConvertedBitmap converted = new WpfFormatConvertedBitmap(
                        source,
                        WpfPixelFormats.Bgra32,
                        null,
                        0);
                    converted.Freeze();
                    convertedSource = converted;
                }

                int width = Math.Max(1, convertedSource.PixelWidth);
                int height = Math.Max(1, convertedSource.PixelHeight);
                Bitmap bitmap = new Bitmap(
                    width,
                    height,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    convertedSource.CopyPixels(
                        new WpfInt32Rect(0, 0, width, height),
                        data.Scan0,
                        data.Stride * height,
                        data.Stride);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return bitmap;
            }

            private static List<int> ReadFrameDelays(string path, int frameCount)
            {
                List<int> delays = new List<int>();
                try
                {
                    WpfBitmapDecoder decoder = WpfBitmapDecoder.Create(
                        new Uri(path, UriKind.Absolute),
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        WpfBitmapCacheOption.OnLoad);
                    for (int i = 0; i < frameCount; i++)
                    {
                        int delay = 100;
                        if (i < decoder.Frames.Count)
                        {
                            System.Windows.Media.Imaging.BitmapMetadata metadata =
                                decoder.Frames[i].Metadata
                                as System.Windows.Media.Imaging.BitmapMetadata;
                            if (metadata != null)
                            {
                                object value = metadata.GetQuery("/grctlext/Delay");
                                if (value is ushort)
                                {
                                    delay = Math.Max(20, Convert.ToInt32(value) * 10);
                                }
                            }
                        }

                        delays.Add(delay);
                    }
                }
                catch
                {
                    delays.Clear();
                    for (int i = 0; i < frameCount; i++)
                    {
                        delays.Add(100);
                    }
                }

                return delays;
            }

            private static void DisposeFrames(List<Bitmap> value)
            {
                if (value == null)
                {
                    return;
                }

                for (int i = 0; i < value.Count; i++)
                {
                    if (value[i] != null)
                    {
                        value[i].Dispose();
                    }
                }
            }

        }

        private sealed class TitleBarIconWindow : Form
        {
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private readonly MainForm owner;
            private bool hovered;
            private bool pressed;

            public TitleBarIconWindow(MainForm owner)
            {
                this.owner = owner;
                Owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Size = new Size(32, 32);
                BackColor = Color.FromArgb(71, 82, 101);
                Cursor = Cursors.Hand;
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                        | ControlStyles.OptimizedDoubleBuffer
                        | ControlStyles.UserPaint
                        | ControlStyles.ResizeRedraw,
                    true);
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return parameters;
                }
            }

            public void UpdatePosition()
            {
                if (owner.WindowState == FormWindowState.Minimized || !owner.Visible)
                {
                    Hide();
                    return;
                }

                NativeMethods.Rect windowRect;
                if (!NativeMethods.GetWindowRect(owner.Handle, out windowRect))
                {
                    Hide();
                    return;
                }

                int measuredCaptionHeight =
                    owner.PointToScreen(Point.Empty).Y - windowRect.Top;
                int captionHeight = owner.WindowState == FormWindowState.Maximized
                    ? SystemInformation.CaptionHeight
                    : measuredCaptionHeight > 8
                        ? measuredCaptionHeight
                        : SystemInformation.CaptionHeight;
                int buttonWidth = Math.Max(1, SystemInformation.CaptionButtonSize.Width);
                int gap = Math.Max(6, buttonWidth / 4);
                int left;
                NativeMethods.Rect minimizeRect;
                if (NativeMethods.TryGetCaptionButtonRect(
                    owner.Handle,
                    2, // 最小化按钮
                    out minimizeRect))
                {
                    // 以系统回报的真实最小化按钮为准：齿轮窗口整体放在它左边，留出间距
                    left = minimizeRect.Left - buttonWidth - gap;
                }
                else
                {
                    left = windowRect.Right - buttonWidth * 5 - gap;
                }

                left = Math.Max(windowRect.Left + 6, left);
                int top = Math.Max(
                    Screen.FromHandle(owner.Handle).Bounds.Top,
                    windowRect.Top);

                Bounds = new Rectangle(left, top, buttonWidth, captionHeight);
                ApplyGearRegion();
                if (!Visible)
                {
                    Show();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, Width, Height);
                ComputeGlyphMetrics();

                // 空心描边样式：先用"伪透明"底填充轮廓（Region 裁出齿轮形状），
                // 底色取采样到的标题栏真实像素，换主题不穿帮；
                // 悬停/按下时底色变浅灰作为反馈。描边色 = 标题栏按钮字形色。
                Color fillColor = pressed
                    ? Color.FromArgb(214, 218, 224)
                    : hovered
                        ? Color.FromArgb(226, 229, 234)
                        : SampleCaptionColor();
                using (SolidBrush backBrush = new SolidBrush(fillColor))
                {
                    e.Graphics.FillRectangle(backBrush, bounds);
                }

                // Fluent(Win11 设置)风格实心齿轮：预转换矢量路径整体填充；
                // 悬停/按下时整块槽位变浅灰(与系统标题栏按钮相同的高亮方式), 字形颜色不变
                using (SolidBrush glyphBrush = new SolidBrush(Color.FromArgb(70, 70, 70)))
                using (GraphicsPath gearPath = new GraphicsPath())
                {
                    AppendFluentGear(gearPath, GetGearBox(glyphBoundsCache));
                    e.Graphics.FillPath(glyphBrush, gearPath);
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hovered = false;
                pressed = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    pressed = true;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool clicked = pressed
                    && e.Button == MouseButtons.Left
                    && ClientRectangle.Contains(e.Location);
                pressed = false;
                Invalidate();
                if (clicked)
                {
                    owner.ToggleSettingsPanel();
                }
            }

            private void ApplyGearRegion()
            {
                // 命中区域 = 整个槽位(窗口客户区), 齿形镂空不再吃掉点击
                Region nextRegion = new Region(new Rectangle(0, 0, Width, Height));
                try
                {
                    Region previous = Region;
                    Region = nextRegion;
                    if (previous != null)
                    {
                        previous.Dispose();
                    }
                }
                catch
                {
                    nextRegion.Dispose();
                    throw;
                }
            }

            private Rectangle glyphBoundsCache;

            // ===== Fluent(Win11 设置) 齿轮矢量数据 =====
            // 由 Fluent UI "settings-24-regular" 的 SVG 路径预转换而来(24x24 视图框)。
            // 编码: 0=开始子路径(x,y)  1=直线(x1,y1,x2,y2)
            //       2=三次贝塞尔(起点x,y 控1x,y 控2x,y 终点x,y)  3=闭合子路径
            private static readonly float[] FluentGearGeometry = new float[]
            {
                0F, 12.012F, 2.25F, 2F, 12.012F, 2.25F, 12.746F, 2.258F,
                13.477F, 2.343F, 14.194F, 2.503F, 2F, 14.194F, 2.503F, 14.5067F,
                2.5728F, 14.7405F, 2.8336F, 14.776F, 3.152F, 1F, 14.776F, 3.152F,
                14.946F, 4.679F, 2F, 14.946F, 4.679F, 14.9947F, 5.1156F, 15.2477F,
                5.5031F, 15.6279F, 5.7233F, 2F, 15.6279F, 5.7233F, 16.008F, 5.9434F,
                16.4701F, 5.9701F, 16.873F, 5.795F, 1F, 16.873F, 5.795F, 18.273F,
                5.18F, 2F, 18.273F, 5.18F, 18.5645F, 5.0516F, 18.9054F, 5.1214F,
                19.123F, 5.354F, 2F, 19.123F, 5.354F, 20.1351F, 6.4352F, 20.889F,
                7.7316F, 21.328F, 9.146F, 2F, 21.328F, 9.146F, 21.422F, 9.4509F,
                21.3129F, 9.7818F, 21.056F, 9.971F, 1F, 21.056F, 9.971F, 19.815F,
                10.887F, 2F, 19.815F, 10.887F, 19.4604F, 11.1469F, 19.2509F, 11.5603F,
                19.2509F, 12F, 2F, 19.2509F, 12F, 19.2509F, 12.4397F, 19.4604F,
                12.8531F, 19.815F, 13.113F, 1F, 19.815F, 13.113F, 21.058F, 14.028F,
                2F, 21.058F, 14.028F, 21.3153F, 14.2173F, 21.4245F, 14.5488F, 21.33F,
                14.854F, 2F, 21.33F, 14.854F, 20.8913F, 16.2683F, 20.1377F, 17.5647F,
                19.126F, 18.646F, 2F, 19.126F, 18.646F, 18.9088F, 18.8785F, 18.5685F,
                18.9487F, 18.277F, 18.821F, 1F, 18.277F, 18.821F, 16.871F, 18.204F,
                2F, 16.871F, 18.204F, 16.4686F, 18.0276F, 16.0062F, 18.0533F, 15.6259F,
                18.2733F, 2F, 15.6259F, 18.2733F, 15.2456F, 18.4933F, 14.9927F, 18.8812F,
                14.945F, 19.318F, 1F, 14.945F, 19.318F, 14.775F, 20.844F, 2F,
                14.775F, 20.844F, 14.7402F, 21.1585F, 14.5118F, 21.4174F, 14.204F, 21.491F,
                2F, 14.204F, 21.491F, 12.7556F, 21.8363F, 11.2464F, 21.8363F, 9.798F,
                21.491F, 2F, 9.798F, 21.491F, 9.4898F, 21.4178F, 9.2609F, 21.1588F,
                9.226F, 20.844F, 1F, 9.226F, 20.844F, 9.057F, 19.32F, 2F,
                9.057F, 19.32F, 9.0072F, 18.8846F, 8.7539F, 18.4987F, 8.3743F, 18.2798F,
                2F, 8.3743F, 18.2798F, 7.9947F, 18.0609F, 7.5337F, 18.035F, 7.132F,
                18.21F, 1F, 7.132F, 18.21F, 5.726F, 18.826F, 2F, 5.726F,
                18.826F, 5.4343F, 18.9542F, 5.0934F, 18.884F, 4.876F, 18.651F, 2F,
                4.876F, 18.651F, 3.8641F, 17.5685F, 3.1109F, 16.2706F, 2.673F, 14.855F,
                2F, 2.673F, 14.855F, 2.5785F, 14.5498F, 2.6877F, 14.2183F, 2.945F,
                14.029F, 1F, 2.945F, 14.029F, 4.188F, 13.113F, 2F, 4.188F,
                13.113F, 4.5426F, 12.8531F, 4.7521F, 12.4397F, 4.7521F, 12F, 2F,
                4.7521F, 12F, 4.7521F, 11.5603F, 4.5426F, 11.1469F, 4.188F, 10.887F,
                1F, 4.188F, 10.887F, 2.945F, 9.973F, 2F, 2.945F, 9.973F,
                2.6877F, 9.7837F, 2.5785F, 9.4522F, 2.673F, 9.147F, 2F, 2.673F,
                9.147F, 3.112F, 7.7326F, 3.8659F, 6.4362F, 4.878F, 5.355F, 2F,
                4.878F, 5.355F, 5.0956F, 5.1224F, 5.4365F, 5.0526F, 5.728F, 5.181F,
                1F, 5.728F, 5.181F, 7.128F, 5.796F, 2F, 7.128F, 5.796F,
                7.5316F, 5.9709F, 7.9942F, 5.9441F, 8.3749F, 5.7236F, 2F, 8.3749F,
                5.7236F, 8.7555F, 5.5031F, 9.009F, 5.1151F, 9.058F, 4.678F, 1F,
                9.058F, 4.678F, 9.228F, 3.152F, 2F, 9.228F, 3.152F, 9.2632F,
                2.8329F, 9.4976F, 2.5716F, 9.811F, 2.502F, 2F, 9.811F, 2.502F,
                10.527F, 2.3433F, 11.2607F, 2.2593F, 12.012F, 2.25F, 0F, 12.012F,
                3.75F, 2F, 12.012F, 3.75F, 11.5584F, 3.7548F, 11.1057F, 3.7939F,
                10.658F, 3.867F, 1F, 10.658F, 3.867F, 10.548F, 4.844F, 2F,
                10.548F, 4.844F, 10.4465F, 5.7552F, 9.9182F, 6.564F, 9.1245F, 7.0229F,
                2F, 9.1245F, 7.0229F, 8.3309F, 7.4819F, 7.3664F, 7.5365F, 6.526F,
                7.17F, 1F, 6.526F, 7.17F, 5.627F, 6.776F, 2F, 5.627F,
                6.776F, 5.0555F, 7.47F, 4.6006F, 8.2521F, 4.28F, 9.092F, 1F,
                4.28F, 9.092F, 5.077F, 9.679F, 2F, 5.077F, 9.679F, 5.8162F,
                10.2214F, 6.2529F, 11.0834F, 6.2531F, 12.0002F, 2F, 6.2531F, 12.0002F,
                6.2533F, 12.9171F, 5.8169F, 13.7792F, 5.078F, 14.322F, 1F, 5.078F,
                14.322F, 4.279F, 14.91F, 2F, 4.279F, 14.91F, 4.599F, 15.752F,
                5.055F, 16.536F, 5.627F, 17.232F, 1F, 5.627F, 17.232F, 6.532F,
                16.835F, 2F, 6.532F, 16.835F, 7.3705F, 16.4689F, 8.3331F, 16.5227F,
                9.1256F, 16.98F, 2F, 9.1256F, 16.98F, 9.9181F, 17.4373F, 10.4464F,
                18.2438F, 10.549F, 19.153F, 1F, 10.549F, 19.153F, 10.658F, 20.137F,
                2F, 10.658F, 20.137F, 11.548F, 20.287F, 12.457F, 20.287F, 13.346F,
                20.137F, 1F, 13.346F, 20.137F, 13.456F, 19.153F, 2F, 13.456F,
                19.153F, 13.5565F, 18.2424F, 14.0844F, 17.4339F, 14.8776F, 16.9755F, 2F,
                14.8776F, 16.9755F, 15.6708F, 16.5171F, 16.6348F, 16.4635F, 17.474F, 16.831F,
                1F, 17.474F, 16.831F, 18.378F, 17.227F, 2F, 18.378F, 17.227F,
                18.95F, 16.5325F, 19.4053F, 15.7496F, 19.726F, 14.909F, 1F, 19.726F,
                14.909F, 18.928F, 14.321F, 2F, 18.928F, 14.321F, 18.1888F, 13.7786F,
                17.7521F, 12.9166F, 17.7519F, 11.9998F, 2F, 17.7519F, 11.9998F, 17.7517F,
                11.0829F, 18.1881F, 10.2208F, 18.927F, 9.678F, 1F, 18.927F, 9.678F,
                19.724F, 9.091F, 2F, 19.724F, 9.091F, 19.4032F, 8.2507F, 18.9479F,
                7.4682F, 18.376F, 6.774F, 1F, 18.376F, 6.774F, 17.479F, 7.167F,
                2F, 17.479F, 7.167F, 16.6389F, 7.5348F, 15.6739F, 7.4811F, 14.8798F,
                7.0223F, 2F, 14.8798F, 7.0223F, 14.0857F, 6.5636F, 13.5571F, 5.7545F,
                13.456F, 4.843F, 1F, 13.456F, 4.843F, 13.347F, 3.867F, 2F,
                13.347F, 3.867F, 12.9059F, 3.7949F, 12.46F, 3.7558F, 12.013F, 3.75F,
                0F, 12F, 8.25F, 2F, 12F, 8.25F, 14.0711F, 8.25F,
                15.75F, 9.9289F, 15.75F, 12F, 2F, 15.75F, 12F, 15.75F,
                14.0711F, 14.0711F, 15.75F, 12F, 15.75F, 2F, 12F, 15.75F,
                9.9289F, 15.75F, 8.25F, 14.0711F, 8.25F, 12F, 2F, 8.25F,
                12F, 8.25F, 9.9289F, 9.9289F, 8.25F, 12F, 8.25F, 0F,
                12F, 9.75F, 2F, 12F, 9.75F, 10.7574F, 9.75F, 9.75F,
                10.7574F, 9.75F, 12F, 2F, 9.75F, 12F, 9.75F, 13.2426F,
                10.7574F, 14.25F, 12F, 14.25F, 2F, 12F, 14.25F, 13.2426F,
                14.25F, 14.25F, 13.2426F, 14.25F, 12F, 2F, 14.25F, 12F,
                14.25F, 10.7574F, 13.2426F, 9.75F, 12F, 9.75F
            };
            // 把 24x24 矢量框整框映射到字形区(Fluent 图标在 24 框内自带约 2/24 的内边距,
            // 视觉直径约为字形区的 81%)
            private static RectangleF GetGearBox(Rectangle glyphBounds)
            {
                return new RectangleF(
                    glyphBounds.Left,
                    glyphBounds.Top,
                    glyphBounds.Width,
                    glyphBounds.Height);
            }

            // 解码矢量数据并填入 GraphicsPath(非零环绕规则, 与 SVG 默认一致)
            private static void AppendFluentGear(GraphicsPath path, RectangleF box)
            {
                path.FillMode = FillMode.Winding;
                float scaleX = box.Width / 24F;
                float scaleY = box.Height / 24F;
                float[] data = FluentGearGeometry;
                int i = 0;
                while (i < data.Length)
                {
                    int command = (int)data[i];
                    i++;
                    if (command == 0)
                    {
                        float x = box.X + data[i] * scaleX;
                        float y = box.Y + data[i + 1] * scaleY;
                        i += 2;
                        path.StartFigure();
                        path.AddLine(x, y, x, y);
                    }
                    else if (command == 1)
                    {
                        path.AddLine(
                            box.X + data[i] * scaleX,
                            box.Y + data[i + 1] * scaleY,
                            box.X + data[i + 2] * scaleX,
                            box.Y + data[i + 3] * scaleY);
                        i += 4;
                    }
                    else if (command == 2)
                    {
                        path.AddBezier(
                            box.X + data[i] * scaleX,
                            box.Y + data[i + 1] * scaleY,
                            box.X + data[i + 2] * scaleX,
                            box.Y + data[i + 3] * scaleY,
                            box.X + data[i + 4] * scaleX,
                            box.Y + data[i + 5] * scaleY,
                            box.X + data[i + 6] * scaleX,
                            box.Y + data[i + 7] * scaleY);
                        i += 8;
                    }
                    else if (command == 3)
                    {
                        path.CloseFigure();
                    }
                }
            }

            private void ComputeGlyphMetrics()
            {
                int glyphSize = Math.Min(44, Math.Max(24, Math.Min(Height - 2, Width - 8)));
                glyphBoundsCache = new Rectangle(
                    (Width - glyphSize) / 2,
                    (Height - glyphSize) / 2,
                    glyphSize,
                    glyphSize);
            }

            // 采样本窗口左侧标题栏空白处的真实像素颜色，作为齿轮内部"伪透明"底色
            private Color SampleCaptionColor()
            {
                try
                {
                    NativeMethods.Rect windowRect;
                    if (NativeMethods.GetWindowRect(owner.Handle, out windowRect))
                    {
                        int sampleX = Left - 8;
                        int sampleY = Top + Height / 2;
                        if (sampleX < windowRect.Left + 4)
                        {
                            sampleX = windowRect.Left + 4;
                        }

                        IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
                        if (dc != IntPtr.Zero)
                        {
                            try
                            {
                                int colorRef = NativeMethods.GetPixel(dc, sampleX, sampleY);
                                if (colorRef != -1)
                                {
                                    return Color.FromArgb(
                                        colorRef & 0xFF,
                                        (colorRef >> 8) & 0xFF,
                                        (colorRef >> 16) & 0xFF);
                                }
                            }
                            finally
                            {
                                NativeMethods.ReleaseDC(IntPtr.Zero, dc);
                            }
                        }
                    }
                }
                catch
                {
                }

                return Color.FromArgb(243, 243, 243);
            }
        }

        // 排列按钮悬浮窗：位于齿轮左边，黑色“一上一下”双箭头线条，
        // 点击打开图标排列面板（摆放方式 + 统一缩放）。定位锚定齿轮窗口左沿。
        private sealed class ArrangeIconWindow : Form
        {
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private readonly MainForm owner;
            private bool hovered;
            private bool pressed;
            private Rectangle leftArrowBoundsCache;
            private Rectangle rightArrowBoundsCache;

            public ArrangeIconWindow(MainForm owner)
            {
                this.owner = owner;
                Owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Size = new Size(32, 32);
                BackColor = Color.FromArgb(71, 82, 101);
                Cursor = Cursors.Hand;
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                        | ControlStyles.OptimizedDoubleBuffer
                        | ControlStyles.UserPaint
                        | ControlStyles.ResizeRedraw,
                    true);
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return parameters;
                }
            }

            public void UpdatePosition()
            {
                if (owner.WindowState == FormWindowState.Minimized
                    || !owner.Visible
                    || owner.titleBarIconWindow == null
                    || !owner.titleBarIconWindow.Visible)
                {
                    Hide();
                    return;
                }

                // 调用方保证齿轮窗口先就位（真实标题栏按钮坐标），以它左沿为锚
                Rectangle gearBounds = owner.titleBarIconWindow.Bounds;
                int gap = Math.Max(6, gearBounds.Width / 4);
                int left = gearBounds.Left - gearBounds.Width - gap;
                if (left < 0)
                {
                    Hide();
                    return;
                }

                Bounds = new Rectangle(left, gearBounds.Top, gearBounds.Width, gearBounds.Height);
                ApplyArrowRegion();
                if (!Visible)
                {
                    Show();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, Width, Height);

                // 与齿轮一致：伪透明底采样标题栏真实像素，悬停/按下浅灰反馈
                Color fillColor = pressed
                    ? Color.FromArgb(214, 218, 224)
                    : hovered
                        ? Color.FromArgb(226, 229, 234)
                        : MainForm.SampleCaptionPixelColor(Left - 8, Top + Height / 2);
                using (SolidBrush backBrush = new SolidBrush(fillColor))
                {
                    e.Graphics.FillRectangle(backBrush, bounds);
                }

                int centerX = Width / 2;
                int centerY = Height / 2;
                int spread = Math.Max(3, (int)Math.Round(Height * 0.15F));
                int halfLength = Math.Max(5, (int)Math.Round(Height * 0.25F));
                int headSize = Math.Max(3, (int)Math.Round(Height * 0.13F));

                // 用户指定：黑色线条，一上一下
                using (Pen pen = new Pen(Color.FromArgb(25, 25, 25), 2.5F))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    DrawVerticalArrow(e.Graphics, pen, centerX - spread, centerY, halfLength, headSize, true);
                    DrawVerticalArrow(e.Graphics, pen, centerX + spread, centerY, halfLength, headSize, false);
                }
            }

            private static void DrawVerticalArrow(
                Graphics graphics,
                Pen pen,
                int centerX,
                int centerY,
                int halfLength,
                int headSize,
                bool pointingUp)
            {
                int tipY = pointingUp ? centerY - halfLength : centerY + halfLength;
                int tailY = pointingUp ? centerY + halfLength : centerY - halfLength;
                int headDy = pointingUp ? headSize : -headSize;
                graphics.DrawLine(pen, centerX, tailY, centerX, tipY);
                graphics.DrawLine(pen, centerX, tipY, centerX - headSize, tipY + headDy);
                graphics.DrawLine(pen, centerX, tipY, centerX + headSize, tipY + headDy);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hovered = false;
                pressed = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    pressed = true;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool clicked = pressed
                    && e.Button == MouseButtons.Left
                    && ClientRectangle.Contains(e.Location);
                pressed = false;
                Invalidate();
                if (clicked)
                {
                    owner.ToggleArrangePanel();
                }
            }

            private void ApplyArrowRegion()
            {
                ComputeArrowMetrics();
                Region nextRegion = new Region(leftArrowBoundsCache);
                try
                {
                    nextRegion.Union(rightArrowBoundsCache);
                    Region previous = Region;
                    Region = nextRegion;
                    if (previous != null)
                    {
                        previous.Dispose();
                    }
                }
                catch
                {
                    nextRegion.Dispose();
                    throw;
                }
            }

            private void ComputeArrowMetrics()
            {
                int spread = Math.Max(3, (int)Math.Round(Height * 0.15F));
                int halfLength = Math.Max(5, (int)Math.Round(Height * 0.25F));
                int headSize = Math.Max(3, (int)Math.Round(Height * 0.13F));
                int centerX = Width / 2;
                int centerY = Height / 2;
                leftArrowBoundsCache = Rectangle.FromLTRB(
                    centerX - spread - headSize - 2,
                    centerY - halfLength - 2,
                    centerX - spread + headSize + 2,
                    centerY + halfLength + 2);
                rightArrowBoundsCache = Rectangle.FromLTRB(
                    centerX + spread - headSize - 2,
                    centerY - halfLength - 2,
                    centerX + spread + headSize + 2,
                    centerY + halfLength + 2);
            }
        }

        // 书包按钮悬浮窗：位于排列按钮（↑↓）左边，黑色线条书包（主体+提手+前袋），
        // 点击打开收纳容量面板。定位锚定排列窗口左沿，链条：书包 · ↑↓ · 齿轮 · 系统按钮。
        private sealed class BagIconWindow : Form
        {
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private readonly MainForm owner;
            private bool hovered;
            private bool pressed;
            private Rectangle glyphBoundsCache;

            // ===== Fluent 图标矢量数据(FluentBackpackGeometry) =====
            // 由 SVG 路径预转换而来(24x24 视图框)。
            // 编码: 0=开始子路径(x,y)  1=直线(x1,y1,x2,y2)
            //       2=三次贝塞尔(起点x,y 控1x,y 控2x,y 终点x,y)  3=闭合子路径
            private static readonly float[] FluentBackpackGeometry = new float[]
            {
                0F, 12F, 2F, 2F, 12F, 2F, 10.0552F, 1.9999F,
                8.4331F, 3.4866F, 8.264F, 5.424F, 2F, 8.264F, 5.424F, 5.6405F,
                6.8095F, 3.9993F, 9.5331F, 4F, 12.5F, 1F, 4F, 12.5F,
                4F, 14F, 1F, 4F, 14F, 20F, 14F, 1F,
                20F, 14F, 20F, 12.5F, 2F, 20F, 12.5F, 20.0007F,
                9.5331F, 18.3595F, 6.8095F, 15.736F, 5.424F, 2F, 15.736F, 5.424F,
                15.5669F, 3.4866F, 13.9448F, 1.9999F, 12F, 2F, 0F, 20F,
                15.714F, 1F, 20F, 15.714F, 9.5F, 15.714F, 1F, 9.5F,
                15.714F, 9.5F, 17.25F, 2F, 9.5F, 17.25F, 9.5F, 17.6642F,
                9.1642F, 18F, 8.75F, 18F, 2F, 8.75F, 18F, 8.3358F,
                18F, 8F, 17.6642F, 8F, 17.25F, 1F, 8F, 17.25F,
                8F, 15.714F, 1F, 8F, 15.714F, 4F, 15.714F, 1F,
                4F, 15.714F, 4F, 18.75F, 2F, 4F, 18.75F, 4F,
                20.5449F, 5.4551F, 22F, 7.25F, 22F, 1F, 7.25F, 22F,
                16.75F, 22F, 2F, 16.75F, 22F, 18.5449F, 22F, 20F,
                20.5449F, 20F, 18.75F, 3F, 0F, 12F, 4.5F, 2F,
                12F, 4.5F, 11.302F, 4.5F, 10.626F, 4.59F, 9.98F, 4.757F,
                2F, 9.98F, 4.757F, 10.3581F, 3.9864F, 11.1416F, 3.498F, 12F,
                3.498F, 2F, 12F, 3.498F, 12.8584F, 3.498F, 13.6419F, 3.9864F,
                14.02F, 4.757F, 2F, 14.02F, 4.757F, 13.3604F, 4.5856F, 12.6815F,
                4.4993F, 12F, 4.5F, 0F, 8F, 10.417F, 2F, 8F,
                10.417F, 8F, 9.0821F, 9.0821F, 8F, 10.417F, 8F, 1F,
                10.417F, 8F, 13.583F, 8F, 2F, 13.583F, 8F, 14.9179F,
                8F, 16F, 9.0821F, 16F, 10.417F, 2F, 16F, 10.417F,
                16F, 11.29F, 15.291F, 12F, 14.417F, 12F, 1F, 14.417F,
                12F, 9.583F, 12F, 2F, 9.583F, 12F, 9.1632F, 12F,
                8.7605F, 11.8332F, 8.4636F, 11.5364F, 2F, 8.4636F, 11.5364F, 8.1668F,
                11.2395F, 8F, 10.8368F, 8F, 10.417F, 0F, 10.417F, 9.5F,
                2F, 10.417F, 9.5F, 9.9106F, 9.5F, 9.5F, 9.9106F, 9.5F,
                10.417F, 2F, 9.5F, 10.417F, 9.5F, 10.463F, 9.537F, 10.5F,
                9.583F, 10.5F, 1F, 9.583F, 10.5F, 14.417F, 10.5F, 2F,
                14.417F, 10.5F, 14.439F, 10.5F, 14.4601F, 10.4913F, 14.4757F, 10.4757F,
                2F, 14.4757F, 10.4757F, 14.4913F, 10.4601F, 14.5F, 10.439F, 14.5F,
                10.417F, 2F, 14.5F, 10.417F, 14.5F, 9.9106F, 14.0894F, 9.5F,
                13.583F, 9.5F, 3F
            };
            // 把 24x24 矢量框整框映射到字形区(与齿轮 v2 同款映射)
            private static RectangleF GetBackpackBox(Rectangle glyphBounds)
            {
                return new RectangleF(
                    glyphBounds.Left,
                    glyphBounds.Top,
                    glyphBounds.Width,
                    glyphBounds.Height);
            }

            // 解码矢量数据并填入 GraphicsPath(非零环绕规则, 与 SVG 默认一致)
            private static void AppendFluentBackpack(GraphicsPath path, RectangleF box)
            {
                path.FillMode = FillMode.Winding;
                float scaleX = box.Width / 24F;
                float scaleY = box.Height / 24F;
                float[] data = FluentBackpackGeometry;
                int i = 0;
                while (i < data.Length)
                {
                    int command = (int)data[i];
                    i++;
                    if (command == 0)
                    {
                        float x = box.X + data[i] * scaleX;
                        float y = box.Y + data[i + 1] * scaleY;
                        i += 2;
                        path.StartFigure();
                        path.AddLine(x, y, x, y);
                    }
                    else if (command == 1)
                    {
                        path.AddLine(
                            box.X + data[i] * scaleX,
                            box.Y + data[i + 1] * scaleY,
                            box.X + data[i + 2] * scaleX,
                            box.Y + data[i + 3] * scaleY);
                        i += 4;
                    }
                    else if (command == 2)
                    {
                        path.AddBezier(
                            box.X + data[i] * scaleX,
                            box.Y + data[i + 1] * scaleY,
                            box.X + data[i + 2] * scaleX,
                            box.Y + data[i + 3] * scaleY,
                            box.X + data[i + 4] * scaleX,
                            box.Y + data[i + 5] * scaleY,
                            box.X + data[i + 6] * scaleX,
                            box.Y + data[i + 7] * scaleY);
                        i += 8;
                    }
                    else if (command == 3)
                    {
                        path.CloseFigure();
                    }
                }
            }

            public BagIconWindow(MainForm owner)
            {
                this.owner = owner;
                Owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Size = new Size(32, 32);
                BackColor = Color.FromArgb(71, 82, 101);
                Cursor = Cursors.Hand;
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                        | ControlStyles.OptimizedDoubleBuffer
                        | ControlStyles.UserPaint
                        | ControlStyles.ResizeRedraw,
                    true);
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return parameters;
                }
            }

            public void UpdatePosition()
            {
                if (owner.WindowState == FormWindowState.Minimized
                    || !owner.Visible
                    || owner.arrangeIconWindow == null
                    || !owner.arrangeIconWindow.Visible)
                {
                    Hide();
                    return;
                }

                // 调用方保证排列按钮先就位（齿轮 → ↑↓ → 书包），以它左沿为锚
                Rectangle arrangeBounds = owner.arrangeIconWindow.Bounds;
                int gap = Math.Max(6, arrangeBounds.Width / 4);
                int left = arrangeBounds.Left - arrangeBounds.Width - gap;
                if (left < 0)
                {
                    Hide();
                    return;
                }

                Bounds = new Rectangle(left, arrangeBounds.Top, arrangeBounds.Width, arrangeBounds.Height);
                ApplyBagRegion();
                if (!Visible)
                {
                    Show();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, Width, Height);

                // 与齿轮/排列按钮一致：伪透明底采样标题栏真实像素，悬停/按下浅灰反馈
                Color fillColor = pressed
                    ? Color.FromArgb(214, 218, 224)
                    : hovered
                        ? Color.FromArgb(226, 229, 234)
                        : MainForm.SampleCaptionPixelColor(Left - 8, Top + Height / 2);
                using (SolidBrush backBrush = new SolidBrush(fillColor))
                {
                    e.Graphics.FillRectangle(backBrush, bounds);
                }

                ComputeBagMetrics();

                // Fluent(Win11)风格实心背包：预转换矢量路径整体填充；
                // 颜色与齿轮一致(70,70,70), 标题栏字形族视觉统一
                using (SolidBrush glyphBrush = new SolidBrush(Color.FromArgb(70, 70, 70)))
                using (GraphicsPath backpackPath = new GraphicsPath())
                {
                    AppendFluentBackpack(backpackPath, GetBackpackBox(glyphBoundsCache));
                    e.Graphics.FillPath(glyphBrush, backpackPath);
                }
            }

            private void ComputeBagMetrics()
            {
                // 与齿轮 v2 相同: 字形尽量吃满槽位, 高度留 2px, 宽度留 8px
                int glyphSize = Math.Min(44, Math.Max(24, Math.Min(Height - 2, Width - 8)));
                glyphBoundsCache = new Rectangle(
                    (Width - glyphSize) / 2,
                    (Height - glyphSize) / 2,
                    glyphSize,
                    glyphSize);
            }

            private void ApplyBagRegion()
            {
                // 命中区域 = 整个槽位(窗口客户区), 与齿轮 v2 一致
                Region nextRegion = new Region(new Rectangle(0, 0, Width, Height));
                try
                {
                    Region previous = Region;
                    Region = nextRegion;
                    if (previous != null)
                    {
                        previous.Dispose();
                    }
                }
                catch
                {
                    nextRegion.Dispose();
                    throw;
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hovered = false;
                pressed = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    pressed = true;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool clicked = pressed
                    && e.Button == MouseButtons.Left
                    && ClientRectangle.Contains(e.Location);
                pressed = false;
                Invalidate();
                if (clicked)
                {
                    owner.ToggleCapacityPanel();
                }
            }
        }

        private sealed class SettingsOutsideClickFilter : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_RBUTTONDOWN = 0x0204;
            private const int WM_MBUTTONDOWN = 0x0207;
            private readonly MainForm owner;

            public SettingsOutsideClickFilter(MainForm owner)
            {
                this.owner = owner;
            }

            public bool PreFilterMessage(ref Message message)
            {
                if (message.Msg != WM_LBUTTONDOWN
                    && message.Msg != WM_RBUTTONDOWN
                    && message.Msg != WM_MBUTTONDOWN)
                {
                    return false;
                }

                if (owner.ShouldCloseSettingsPanelForClick(Control.MousePosition))
                {
                    try
                    {
                        owner.BeginInvoke(new MethodInvoker(owner.HideFloatingPanels));
                    }
                    catch
                    {
                    }
                }

                return false;
            }
        }

        private sealed class DirectionOption
        {
            public DirectionOption(string text, string value)
            {
                Text = text;
                Value = value;
            }

            public string Text { get; private set; }
            public string Value { get; private set; }

            public override string ToString()
            {
                return Text;
            }
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(
                bounds.Right - diameter,
                bounds.Bottom - diameter,
                diameter,
                diameter,
                0,
                90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class TextInputDialog : Form
        {
            private readonly TextBox textBox;

            public TextInputDialog(
                string title,
                string labelText,
                string initialValue,
                string fallbackValue)
            {
                this.fallbackValue = fallbackValue;
                Text = title;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                ClientSize = new Size(360, 126);
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

                Label label = new Label();
                label.Text = labelText;
                label.AutoSize = true;
                label.Location = new Point(16, 16);
                Controls.Add(label);

                textBox = new TextBox();
                textBox.Location = new Point(18, 42);
                textBox.Width = ClientSize.Width - 36;
                textBox.Text = initialValue ?? String.Empty;
                textBox.SelectAll();
                Controls.Add(textBox);

                Button okButton = new Button();
                okButton.Text = "确定";
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(ClientSize.Width - 176, 82);
                okButton.Size = new Size(72, 28);
                Controls.Add(okButton);

                Button cancelButton = new Button();
                cancelButton.Text = "取消";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(ClientSize.Width - 94, 82);
                cancelButton.Size = new Size(72, 28);
                Controls.Add(cancelButton);

                AcceptButton = okButton;
                CancelButton = cancelButton;
            }

            public string Value
            {
                get
                {
                    string value = textBox.Text.Trim();
                    return String.IsNullOrWhiteSpace(value) ? fallbackValue : value;
                }
            }

            private readonly string fallbackValue;
        }

        private sealed class PendingSourceMove
        {
            public string SourcePath { get; set; }
            public int RemainingAttempts { get; set; }
        }

    }
}
