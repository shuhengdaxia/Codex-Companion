using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed class ThemeGalleryItem
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Author { get; set; }
    public string Summary { get; set; }
    public string DetailUrl { get; set; }
    public string PreviewImagePath { get; set; }
    public string PreviewUrl { get; set; }
    public byte[] PreviewImageBytes { get; set; }
    public string PreviewState { get; set; }
    public string PreviewError { get; set; }
    public string Accent { get; set; }
    public string Background { get; set; }
    public string Foreground { get; set; }
    public bool Installable { get; set; }
    public string Availability { get; set; }
    public string AvailabilityDetail { get; set; }
    public override string ToString() { return String.IsNullOrWhiteSpace(Name) ? Id : Name; }
}

public sealed class ThemeApplyResult
{
    public bool Success { get; set; }
    public string ThemeId { get; set; }
    public string Message { get; set; }
}

public sealed class ThemePreviewResult
{
    public bool Success { get; set; }
    public byte[] Bytes { get; set; }
    public string State { get; set; }
    public string Error { get; set; }
}

public interface IThemeGalleryService
{
    Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken);
    Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken);
    string GetCurrentThemeId();
    Task<ThemeApplyResult> ApplyAsync(string themeId);
    Task<ThemeApplyResult> LaunchAsync(string themeId);
    Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken);
    Task<ThemeApplyResult> RestoreAsync();
}

public sealed class ThemeGalleryForm : Form
{
    private const int PageSize = 12;
    private readonly AppController controller;
    private readonly IThemeGalleryService service;
    private readonly bool previewOnly;
    private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> contentReadySource = new TaskCompletionSource<bool>();
    private readonly SemaphoreSlim previewGate = new SemaphoreSlim(4, 4);
    private readonly List<ThemeGalleryItem> allItems = new List<ThemeGalleryItem>();
    private readonly HashSet<ThemeCard> previewLoading = new HashSet<ThemeCard>();
    private bool previewRefreshPending;
    private bool resizingCards;
    private readonly Color pageBack = Color.FromArgb(246, 248, 251);
    private readonly Color panelBack = DesktopPalette.Surface;
    private readonly Color textColor = Color.FromArgb(35, 48, 68);
    private readonly Color mutedColor = Color.FromArgb(104, 117, 138);
    private readonly Color borderColor = Color.FromArgb(224, 230, 239);
    private readonly Color accent = Color.FromArgb(25, 104, 223);

    private TextBox searchBox;
    private ComboBox availabilityComboBox;
    private FlowLayoutPanel cardsPanel;
    private Label selectionLabel;
    private System.Windows.Forms.Timer filterRefreshTimer;
    private Label statusLabel;
    private Button restoreButton;
    private Button closeButton;
    private Button previousPageButton;
    private Button nextPageButton;
    private Label pageLabel;
    private TabControl appearanceTabs;
    private ComboBox themeComboBox;
    private ComboBox skinComboBox;
    private Panel themePreviewPanel;
    private Label appearanceStatusLabel;
    private Button appearanceApplyButton;
    private Button appearanceCloseButton;
    private ThemeGalleryItem firstPreviewItem;
    private ThemeCard selectedCard;
    private int currentPage = 1;
    private int pageCount = 1;
    private int filteredCount;
    private bool disposed;
    private bool actionBusy;
    private bool appearanceBusy;
    private CancellationTokenSource restartCancellation;

    public string AppliedThemeId { get; private set; }
    public bool AppearanceUpdated { get; private set; }
    public Task ContentReady { get { return contentReadySource.Task; } }

    public ThemeGalleryForm(IThemeGalleryService service, bool previewOnly)
        : this(null, service, previewOnly)
    {
    }

    public ThemeGalleryForm(AppController controller, IThemeGalleryService service, bool previewOnly)
    {
        this.controller = controller;
        this.service = service;
        this.previewOnly = previewOnly;
        InitializeForm();
        BuildLayout();
        BindAppearanceChoices();
        LoadAppearanceSettings();
        FormClosing += ThemeGalleryFormClosing;
        LoadThemes();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            lifetimeCancellation.Cancel();
            if (restartCancellation != null)
            {
                restartCancellation.Cancel();
                restartCancellation.Dispose();
                restartCancellation = null;
            }
            if (filterRefreshTimer != null)
            {
                filterRefreshTimer.Stop();
                filterRefreshTimer.Dispose();
                filterRefreshTimer = null;
            }
            lifetimeCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    public void SavePreview(string path)
    {
        using (Bitmap bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path);
        }
    }

    public void SelectGalleryTab()
    {
        if (appearanceTabs != null) appearanceTabs.SelectedIndex = 1;
    }

    private void InitializeForm()
    {
        Text = previewOnly ? "Codex 外观预览" : "Codex 外观设置";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 620);
        ClientSize = new Size(1040, 700);
        BackColor = pageBack;
        ForeColor = textColor;
        Font = new Font("Segoe UI", 18F, FontStyle.Regular, GraphicsUnit.Pixel);
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DesktopPalette.LightTitleBar(this);
    }

    private void BuildLayout()
    {
        appearanceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Font,
            Padding = new Point(18, 8),
            AccessibleName = "Codex 外观设置"
        };
        TabPage appearancePage = new TabPage("外观偏好") { BackColor = pageBack, Padding = new Padding(8) };
        TabPage galleryPage = new TabPage("主题图库") { BackColor = pageBack, Padding = new Padding(8) };
        appearancePage.Controls.Add(BuildAppearanceLayout());
        galleryPage.Controls.Add(BuildGalleryLayout());
        appearanceTabs.TabPages.Add(appearancePage);
        appearanceTabs.TabPages.Add(galleryPage);
        Controls.Add(appearanceTabs);
    }

    private Control BuildGalleryLayout()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(8);
        root.BackColor = pageBack;
        root.ColumnCount = 1;
        root.RowCount = 3;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.Controls.Add(BuildFilters(), 0, 0);
        cardsPanel = new FlowLayoutPanel();
        cardsPanel.Dock = DockStyle.Fill;
        cardsPanel.AutoScroll = true;
        cardsPanel.WrapContents = true;
        cardsPanel.FlowDirection = FlowDirection.LeftToRight;
        cardsPanel.Padding = new Padding(8);
        cardsPanel.BackColor = pageBack;
        cardsPanel.Resize += delegate { ResizeCards(); EnsureVisiblePreviews(); };
        cardsPanel.Scroll += delegate { ScheduleVisiblePreviews(); };
        cardsPanel.MouseWheel += delegate { ScheduleVisiblePreviews(); };
        root.Controls.Add(cardsPanel, 0, 1);
        root.Controls.Add(BuildActions(), 0, 2);
        return root;
    }

    private Control BuildAppearanceLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = pageBack,
            Padding = new Padding(20, 16, 20, 12),
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = pageBack };
        var title = new Label
        {
            Text = "外观偏好",
            Dock = DockStyle.Top,
            Height = 36,
            ForeColor = textColor,
            Font = new Font(Font.FontFamily, 24F, FontStyle.Bold, GraphicsUnit.Pixel),
            UseCompatibleTextRendering = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var caption = new Label
        {
            Text = "主题和皮肤独立保存，不影响系统连接配置。",
            Font = new Font(Font.FontFamily, 16.5F, FontStyle.Regular, GraphicsUnit.Pixel),
            UseCompatibleTextRendering = true,
            Dock = DockStyle.Fill,
            ForeColor = mutedColor,
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(caption);
        header.Controls.Add(title);
        root.Controls.Add(header, 0, 0);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int index = 0; index < 2; index++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        themeComboBox = CreateAppearanceComboBox("Codex 主题");
        themeComboBox.SelectedIndexChanged += delegate { if (themePreviewPanel != null) themePreviewPanel.Invalidate(); };
        skinComboBox = CreateAppearanceComboBox("Codex 皮肤");
        AddAppearanceRow(grid, 0, "主题", themeComboBox);
        AddAppearanceRow(grid, 1, "皮肤", skinComboBox);
        root.Controls.Add(grid, 0, 1);

        themePreviewPanel = new DesktopSurface
        {
            Dock = DockStyle.Fill,
            BackColor = DesktopPalette.Input,
            BorderColor = borderColor,
            CornerRadius = 10,
            Margin = new Padding(0, 10, 0, 8),
            AccessibleName = "当前主题配色示意"
        };
        themePreviewPanel.Paint += PaintThemePreview;
        root.Controls.Add(themePreviewPanel, 0, 2);

        appearanceStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = mutedColor,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            AccessibleName = "外观操作状态",
            Font = new Font(Font.FontFamily, 16.5F, FontStyle.Regular, GraphicsUnit.Pixel),
            UseCompatibleTextRendering = true
        };
        root.Controls.Add(appearanceStatusLabel, 0, 3);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        appearanceApplyButton = CreateButton("保存并重启 Codex", 196, true);
        appearanceApplyButton.AccessibleName = "保存外观并重启 Codex";
        appearanceApplyButton.Click += ApplyAppearance;
        appearanceCloseButton = CreateButton("关闭", 76, false);
        appearanceCloseButton.DialogResult = DialogResult.Cancel;
        appearanceCloseButton.AccessibleName = "关闭外观设置";
        appearanceApplyButton.Dock = DockStyle.Fill;
        appearanceCloseButton.Dock = DockStyle.Fill;
        actions.Controls.Add(appearanceApplyButton, 1, 0);
        actions.Controls.Add(appearanceCloseButton, 3, 0);
        root.Controls.Add(actions, 0, 4);
        return root;
    }

    private ComboBox CreateAppearanceComboBox(string accessibleName)
    {
        return new DesktopComboBox
        {
            AccessibleName = accessibleName,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Regular, GraphicsUnit.Pixel),
            ItemHeight = 36
        };
    }

    private void AddAppearanceRow(TableLayoutPanel grid, int row, string text, Control input)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = mutedColor,
            UseCompatibleTextRendering = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = Padding.Empty;
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(input, 1, row);
    }

    private Control BuildFilters()
    {
        Panel filters = new Panel();
        filters.Dock = DockStyle.Fill;
        Panel controls = new Panel();
        controls.Dock = DockStyle.Top;
        controls.Height = 42;
        Panel searchPanel = new Panel();
        searchPanel.Dock = DockStyle.Fill;
        searchPanel.Padding = new Padding(0, 0, 12, 0);
        Label searchLabel = new Label();
        searchLabel.Dock = DockStyle.Left;
        searchLabel.Width = 94;
        searchLabel.UseCompatibleTextRendering = true;
        searchLabel.Text = "搜索主题";
        searchLabel.ForeColor = mutedColor;
        searchLabel.TextAlign = ContentAlignment.MiddleLeft;
        searchBox = new TextBox { BackColor = DesktopPalette.Input, ForeColor = DesktopPalette.Text, BorderStyle = BorderStyle.None, AccessibleName = "搜索主题" };
        searchBox.Dock = DockStyle.Fill;
        searchBox.TextChanged += delegate { ScheduleCardRefresh(); };
        var searchFrame = new DesktopSurface { Dock = DockStyle.Fill, CornerRadius = 9, BorderColor = borderColor, Padding = new Padding(12, 8, 12, 6) };
        searchFrame.Controls.Add(searchBox);
        searchBox.Enter += delegate { searchFrame.BorderColor = accent; searchFrame.Invalidate(); };
        searchBox.Leave += delegate { searchFrame.BorderColor = borderColor; searchFrame.Invalidate(); };
        searchPanel.Controls.Add(searchFrame);
        searchPanel.Controls.Add(searchLabel);
        availabilityComboBox = new DesktopComboBox { AccessibleName = "主题可用性筛选", Font = Font, ItemHeight = 36 };
        availabilityComboBox.Dock = DockStyle.Right;
        availabilityComboBox.Width = 180;
        availabilityComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        availabilityComboBox.Items.Add("全部主题");
        availabilityComboBox.Items.Add("可直接应用");
        availabilityComboBox.Items.Add("网站参考");
        availabilityComboBox.SelectedIndex = 0;
        availabilityComboBox.SelectedIndexChanged += delegate { ScheduleCardRefresh(); };
        controls.Controls.Add(searchPanel);
        controls.Controls.Add(availabilityComboBox);
        filterRefreshTimer = new System.Windows.Forms.Timer { Interval = 180 };
        filterRefreshTimer.Tick += delegate
        {
            filterRefreshTimer.Stop();
            RefreshCards();
        };
        selectionLabel = new Label();
        selectionLabel.Font = new Font(Font.FontFamily, 16.5F, FontStyle.Regular, GraphicsUnit.Pixel);
        selectionLabel.UseCompatibleTextRendering = true;
        selectionLabel.Dock = DockStyle.Bottom;
        selectionLabel.Height = Math.Max(44, selectionLabel.Font.Height * 2 + 8);
        selectionLabel.AutoEllipsis = false;
        selectionLabel.ForeColor = mutedColor;
        selectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        selectionLabel.Text = "单击选择，双击预览。应用主题请点“应用并重启”。";
        filters.Controls.Add(selectionLabel);
        filters.Controls.Add(controls);
        return filters;
    }

    private Control BuildActions()
    {
        var actions = new TableLayoutPanel { ColumnCount = 6, RowCount = 1, Margin = Padding.Empty };
        actions.Dock = DockStyle.Fill;
        actions.Padding = new Padding(0, 8, 0, 0);
        foreach (int width in new[] { 188, 98, 98 })
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        closeButton = CreateButton("关闭", 76, false);
        closeButton.DialogResult = DialogResult.Cancel;
        restoreButton = CreateButton("恢复原生主题", 164, false);
        restoreButton.Click += RestoreTheme;
        nextPageButton = CreateButton("下一页", 106, false);
        nextPageButton.Click += NextPage;
        previousPageButton = CreateButton("上一页", 106, false);
        previousPageButton.Click += PreviousPage;
        pageLabel = new Label { Width = 136, Height = Math.Max(32, Font.Height + 14), TextAlign = ContentAlignment.MiddleCenter, ForeColor = mutedColor, AutoEllipsis = false };
        statusLabel = new Label();
        pageLabel.Font = statusLabel.Font = new Font(Font.FontFamily, 16.5F, FontStyle.Regular, GraphicsUnit.Pixel);
        pageLabel.UseCompatibleTextRendering = statusLabel.UseCompatibleTextRendering = true;
        statusLabel.Width = 270;
        statusLabel.Height = Math.Max(32, statusLabel.Font.Height + 14);
        statusLabel.AutoEllipsis = true;
        statusLabel.ForeColor = mutedColor;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        Control[] controls = { pageLabel, previousPageButton, nextPageButton, statusLabel, restoreButton, closeButton };
        for (int i = 0; i < controls.Length; i++)
        {
            controls[i].Dock = DockStyle.Fill;
            controls[i].Margin = new Padding(4, 0, 4, 0);
            actions.Controls.Add(controls[i], i, 0);
        }
        AcceptButton = closeButton;
        CancelButton = closeButton;
        return actions;
    }

    private Button CreateButton(string text, int width, bool primary)
    {
        Button button = new DesktopButton
        {
            Text = text,
            Width = width,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Regular, GraphicsUnit.Pixel),
            CornerRadius = 9, BorderColor = borderColor, AccentColor = accent, ForeColor = textColor
        };
        button.Height = Math.Max(28, button.Font.Height + 10);
        if (primary)
        {
            button.BackColor = accent;
            button.ForeColor = Color.White;
            ((DesktopButton)button).Primary = true;
            button.UseVisualStyleBackColor = false;
        }
        return button;
    }

    private void BindAppearanceChoices()
    {
        BindChoiceCombo(themeComboBox, ThemeCatalog.Themes);
        BindChoiceCombo(skinComboBox, new Choice[] {
            new Choice("native", "Codex 原生界面", null, null, null, null, null),
            new Choice("qq", "QQ 风格 · 蓝银面板", null, null, null, null, null)
        });
    }

    private void BindChoiceCombo(ComboBox comboBox, object[] values)
    {
        comboBox.DisplayMember = "Label";
        comboBox.ValueMember = "Id";
        comboBox.DataSource = values;
    }

    private void LoadAppearanceSettings()
    {
        if (previewOnly || controller == null)
        {
            SelectValue(themeComboBox, "keep");
            SelectValue(skinComboBox, "native");
            appearanceApplyButton.Enabled = false;
            SetAppearanceStatus(previewOnly ? "预览模式：外观保存与应用已禁用。" : "外观配置服务未连接。");
            return;
        }
        try
        {
            AppearanceSettings settings = controller.LoadAppearanceSettings();
            SelectValue(themeComboBox, EmptyTo(settings == null ? null : settings.Theme, "keep"));
            SelectValue(skinComboBox, EmptyTo(settings == null ? null : settings.Skin, "native"));
            appearanceApplyButton.Enabled = true;
            SetAppearanceStatus("已加载本机外观设置。");
        }
        catch (Exception error)
        {
            appearanceApplyButton.Enabled = false;
            SetAppearanceStatus("读取外观设置失败：" + error.Message);
        }
    }

    private async void ApplyAppearance(object sender, EventArgs e)
    {
        if (appearanceBusy || actionBusy || previewOnly || controller == null) return;
        var settings = new AppearanceSettings
        {
            Theme = SelectedValueOrText(themeComboBox, "keep"),
            Skin = SelectedValueOrText(skinComboBox, "native")
        };
        SetAppearanceBusy(true, "正在退出 Codex、保存外观并重新启动；如出现官方退出确认，请确认...");
        try
        {
            ConfigurationLaunchResult result = await ConfigurationLaunchWorkflow.RunAsync(
                () => controller.ApplyAppearanceAsync(settings), () => controller.OpenCodex());
            AppearanceUpdated = true;
            SetAppearanceStatus(result.Message);
        }
        catch (Exception error)
        {
            SetAppearanceStatus("保存外观失败：" + error.Message);
        }
        finally
        {
            SetAppearanceBusy(false, null);
        }
    }

    private void SetAppearanceBusy(bool busy, string message)
    {
        if (disposed) return;
        appearanceBusy = busy;
        appearanceTabs.Enabled = !busy;
        themeComboBox.Enabled = !busy;
        skinComboBox.Enabled = !busy;
        appearanceApplyButton.Enabled = !busy && !previewOnly && controller != null;
        appearanceCloseButton.Enabled = !busy;
        closeButton.Enabled = !busy && !actionBusy;
        restoreButton.Enabled = !busy && !actionBusy && !previewOnly && service != null;
        foreach (ThemeCard card in cardsPanel.Controls)
            card.SetActionsEnabled(!busy && !actionBusy && !previewOnly && service != null && card.Item.Installable);
        UpdatePageControls();
        if (!String.IsNullOrWhiteSpace(message)) SetAppearanceStatus(message);
    }

    private void SetAppearanceStatus(string text)
    {
        if (appearanceStatusLabel != null && !disposed)
            appearanceStatusLabel.Text = String.IsNullOrWhiteSpace(text) ? "" : text;
    }

    private void SelectValue(ComboBox comboBox, string value)
    {
        comboBox.SelectedValue = value;
        if (comboBox.SelectedValue == null || comboBox.SelectedValue.ToString() != value) comboBox.Text = value;
    }

    private string SelectedValueOrText(ComboBox comboBox, string fallback)
    {
        if (comboBox.SelectedValue != null) return comboBox.SelectedValue.ToString();
        return String.IsNullOrWhiteSpace(comboBox.Text) ? fallback : comboBox.Text.Trim();
    }

    private string EmptyTo(string value, string fallback)
    {
        return String.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void PaintThemePreview(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Choice choice = themeComboBox.SelectedItem as Choice;
        Color background = ParseColor(choice == null ? null : choice.Background, panelBack);
        Color themeAccent = ParseColor(choice == null ? null : choice.Accent, accent);
        Color foreground = ParseColor(choice == null ? null : choice.Foreground, textColor);
        float top = Math.Max(18F, (themePreviewPanel.Height - 76F) / 2F);
        var miniature = new RectangleF(22, top, 96, 72);
        using (var path = DesktopPalette.Round(miniature, 8))
        using (var brush = new SolidBrush(background))
        using (Pen pen = new Pen(borderColor))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
        using (var brush = new SolidBrush(themeAccent))
        {
            e.Graphics.FillEllipse(brush, 32, top + 11, 7, 7);
            using (var path = DesktopPalette.Round(new RectangleF(54, top + 52, 50, 13), 4))
                e.Graphics.FillPath(brush, path);
        }
        using (var pen = new Pen(Color.FromArgb(125, foreground), 2))
        {
            e.Graphics.DrawLine(pen, 32, top + 30, 42, top + 30);
            e.Graphics.DrawLine(pen, 32, top + 40, 42, top + 40);
            e.Graphics.DrawLine(pen, 54, top + 30, 104, top + 30);
            e.Graphics.DrawLine(pen, 54, top + 40, 88, top + 40);
        }
        var caption = new Rectangle(140, (int)top, Math.Max(0, themePreviewPanel.Width - 162), 28);
        DesktopPalette.DrawText(e.Graphics, "配色示意 · " + (choice == null ? "跟随当前主题" : choice.Label),
            Font, caption, mutedColor, StringAlignment.Near);
    }

    private Color ParseColor(string value, Color fallback)
    {
        if (String.IsNullOrWhiteSpace(value)) return fallback;
        try { return ColorTranslator.FromHtml(value); }
        catch { return fallback; }
    }

    private async void LoadThemes()
    {
        allItems.Clear();
        try
        {
            if (service == null)
            {
                SetStatus("主题服务未连接。");
                contentReadySource.TrySetException(new InvalidOperationException("主题服务未连接。"));
                return;
            }
            SetStatus("正在读取全部主题...");
            IList<ThemeGalleryItem> items = await service.LoadThemesAsync(lifetimeCancellation.Token);
            if (disposed) return;
            if (items != null) allItems.AddRange(items);
            if (allItems.Count == 0)
            {
                SetStatus("暂无可显示的主题。");
                contentReadySource.TrySetException(new InvalidDataException("主题目录为空。"));
                return;
            }
            firstPreviewItem = allItems[0];
            RefreshCards();
            SetStatus(CatalogStatus());
        }
        catch (OperationCanceledException) { contentReadySource.TrySetCanceled(); }
        catch (Exception ex)
        {
            SetStatus("读取主题图库失败：" + ex.Message);
            contentReadySource.TrySetException(ex);
        }
    }

    private void RefreshCards()
    {
        if (cardsPanel == null || disposed) return;
        selectedCard = null;
        if (selectionLabel != null) selectionLabel.Text = "单击选择，双击预览。应用主题请点“应用并重启”。";
        List<ThemeGalleryItem> filteredItems = new List<ThemeGalleryItem>();
        string search = searchBox == null ? "" : searchBox.Text.Trim();
        int mode = availabilityComboBox == null ? 0 : availabilityComboBox.SelectedIndex;
        foreach (ThemeGalleryItem item in allItems)
        {
            if (mode == 1 && !item.Installable) continue;
            if (mode == 2 && item.Installable) continue;
            string haystack = (item.Name ?? "") + " " + (item.Author ?? "") + " " + (item.Summary ?? "");
            if (search.Length > 0 && haystack.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            filteredItems.Add(item);
        }
        filteredCount = filteredItems.Count;
        pageCount = Math.Max(1, (filteredCount + PageSize - 1) / PageSize);
        if (currentPage > pageCount) currentPage = pageCount;
        int first = Math.Min(filteredCount, (currentPage - 1) * PageSize);
        int last = Math.Min(filteredCount, first + PageSize);
        cardsPanel.SuspendLayout();
        try
        {
            while (cardsPanel.Controls.Count > 0) cardsPanel.Controls[0].Dispose();
            for (int index = first; index < last; index++) cardsPanel.Controls.Add(CreateCard(filteredItems[index]));
        }
        finally { cardsPanel.ResumeLayout(true); }
        ResizeCards();
        EnsureVisiblePreviews();
        UpdatePageControls();
        if (cardsPanel.Controls.Count == 0) SetStatus("没有匹配的主题。");
    }

    private void ScheduleCardRefresh()
    {
        currentPage = 1;
        if (filterRefreshTimer == null || disposed)
        {
            RefreshCards();
            return;
        }
        filterRefreshTimer.Stop();
        filterRefreshTimer.Start();
    }

    private void PreviousPage(object sender, EventArgs e)
    {
        if (actionBusy || appearanceBusy || currentPage <= 1) return;
        currentPage--;
        RefreshCards();
    }

    private void NextPage(object sender, EventArgs e)
    {
        if (actionBusy || appearanceBusy || currentPage >= pageCount) return;
        currentPage++;
        RefreshCards();
    }

    private void UpdatePageControls()
    {
        if (pageLabel == null) return;
        pageLabel.Text = "第 " + currentPage + "/" + pageCount + " 页（" + filteredCount + "项）";
        previousPageButton.Enabled = !actionBusy && !appearanceBusy && currentPage > 1;
        nextPageButton.Enabled = !actionBusy && !appearanceBusy && currentPage < pageCount;
    }

    private string CatalogStatus()
    {
        int installable = 0;
        foreach (ThemeGalleryItem item in allItems)
            if (item != null && item.Installable) installable++;
        return "可应用 " + installable + " / 共 " + allItems.Count;
    }

    private ThemeCard CreateCard(ThemeGalleryItem item)
    {
        ThemeCard card = new ThemeCard(item, panelBack, textColor, mutedColor, borderColor);
        card.RestartClicked += RestartCardTheme;
        card.DetailsClicked += OpenDetails;
        card.SelectClicked += SelectCard;
        card.PreviewClicked += OpenImagePreview;
        UpdateCardState(card);
        return card;
    }

    private void SelectCard(ThemeCard card)
    {
        if (card == null || disposed) return;
        if (selectedCard != null && !selectedCard.IsDisposed) selectedCard.SetSelected(false);
        selectedCard = card;
        selectedCard.SetSelected(true);
        if (selectionLabel != null)
            selectionLabel.Text = "已选择：" + (card.Item.Name ?? card.Item.Id) +
                (card.Item.Installable ? "。应用主题请点“应用并重启”。" : "。该主题仅预览或暂不可用。");
    }

    private void ResizeCards()
    {
        if (cardsPanel == null || resizingCards) return;
        resizingCards = true;
        try
        {
            int available = Math.Max(300, cardsPanel.ClientSize.Width - cardsPanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            int columns = Math.Max(1, available / 380);
            int cardMargin = cardsPanel.Controls.Count > 0 ? ((ThemeCard)cardsPanel.Controls[0]).Margin.Horizontal : 10;
            int width = Math.Max(238, (available - cardMargin * columns) / columns);
            foreach (ThemeCard card in cardsPanel.Controls)
            {
                card.Width = width;
                card.LayoutCard();
            }
            cardsPanel.PerformLayout();
        }
        finally { resizingCards = false; }
        UpdateScrollExtent();
    }

    private void UpdateScrollExtent()
    {
        if (cardsPanel == null || resizingCards) return;
        int contentBottom = cardsPanel.Padding.Top;
        int scrollOffset = -cardsPanel.AutoScrollPosition.Y;
        foreach (ThemeCard card in cardsPanel.Controls)
            contentBottom = Math.Max(contentBottom, card.Bottom + scrollOffset + card.Margin.Bottom);
        int height = contentBottom + cardsPanel.Padding.Bottom + 8;
        if (cardsPanel.AutoScrollMinSize.Height == height) return;
        resizingCards = true;
        try { cardsPanel.AutoScrollMinSize = new Size(0, height); }
        finally { resizingCards = false; }
    }

    private void EnsureVisiblePreviews()
    {
        if (cardsPanel == null || service == null || disposed) return;
        Rectangle viewport = cardsPanel.ClientRectangle;
        foreach (ThemeCard card in cardsPanel.Controls)
            if (card.Bounds.IntersectsWith(viewport)) StartPreview(card);
    }

    private void ScheduleVisiblePreviews()
    {
        if (cardsPanel == null || disposed || previewRefreshPending) return;
        previewRefreshPending = true;
        try
        {
            if (cardsPanel.IsHandleCreated)
            {
                cardsPanel.BeginInvoke((MethodInvoker)delegate
                {
                    previewRefreshPending = false;
                    EnsureVisiblePreviews();
                });
            }
            else
            {
                previewRefreshPending = false;
                EnsureVisiblePreviews();
            }
        }
        catch (InvalidOperationException)
        {
            previewRefreshPending = false;
            if (!disposed) EnsureVisiblePreviews();
        }
    }

    private async void StartPreview(ThemeCard card)
    {
        ThemeGalleryItem item = card.Item;
        if (item == null || card.PreviewImage != null || previewLoading.Contains(card)) return;
        previewLoading.Add(card);
        card.SetPreviewStatus("加载预览…");
        bool gateHeld = false;
        try
        {
            await previewGate.WaitAsync(lifetimeCancellation.Token);
            gateHeld = true;
            byte[] previewBytes = card.PreviewBytes;
            if ((previewBytes == null || previewBytes.Length == 0) && item.PreviewImageBytes != null)
            {
                previewBytes = item.PreviewImageBytes;
                item.PreviewImageBytes = null;
            }
            if (previewBytes == null || previewBytes.Length == 0)
            {
                ThemePreviewResult result = await service.LoadPreviewAsync(item.Id, lifetimeCancellation.Token);
                if (disposed || card.IsDisposed) return;
                if (result != null && result.Success && result.Bytes != null)
                {
                    previewBytes = result.Bytes;
                    item.PreviewState = result.State;
                    item.PreviewError = result.Error;
                }
                else
                {
                    string error = result == null ? "预览返回为空。" : result.Error;
                    card.SetPreviewStatus(String.IsNullOrWhiteSpace(error) ? "暂无预览图" : error);
                    if (item == firstPreviewItem) contentReadySource.TrySetException(new InvalidOperationException(error ?? "首项预览不可用。"));
                    return;
                }
            }
            bool previewApplied = await card.SetPreviewAsync(previewBytes);
            if (previewApplied)
            {
                if (item == firstPreviewItem) contentReadySource.TrySetResult(true);
            }
        }
        catch (OperationCanceledException) { if (item == firstPreviewItem) contentReadySource.TrySetCanceled(); }
        catch (Exception ex)
        {
            if (!disposed && !card.IsDisposed) card.SetPreviewStatus("预览失败：" + ex.Message);
            if (item == firstPreviewItem) contentReadySource.TrySetException(ex);
        }
        finally
        {
            if (gateHeld) previewGate.Release();
            previewLoading.Remove(card);
        }
    }

    private void UpdateCardState(ThemeCard card)
    {
        if (card != null) card.SetActionsEnabled(!actionBusy && !appearanceBusy && !previewOnly && service != null && card.Item.Installable);
    }

    private async void RestartCardTheme(ThemeCard card)
    {
        if (card == null || actionBusy || appearanceBusy || previewOnly || service == null || !card.Item.Installable) return;
        SelectCard(card);
        if (MessageBox.Show(this, "应用主题并重启 Codex 会中断当前任务。主题包会先完成下载和校验，确认后才关闭 Codex。是否继续？",
            "确认重启 Codex", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        restartCancellation = new CancellationTokenSource();
        SetActionBusy(true, "正在验证主题并准备重启 Codex...");
        try
        {
            ThemeApplyResult result = await service.RestartAsync(card.Item.Id, restartCancellation.Token);
            if (disposed) return;
            if (result == null || !result.Success)
            {
                ShowThemeFailure("重启 Codex 失败", result == null ? "主题服务未返回结果。" : result.Message);
                return;
            }
            AppliedThemeId = result.ThemeId ?? card.Item.Id;
            SetStatus(String.IsNullOrWhiteSpace(result.Message) ? "主题已应用，Codex 已重启。" : result.Message);
        }
        catch (OperationCanceledException) { SetStatus("已取消重启，Codex 未被关闭。"); }
        catch (Exception ex) { ShowThemeFailure("重启 Codex 失败", ex.Message); }
        finally
        {
            if (restartCancellation != null) { restartCancellation.Dispose(); restartCancellation = null; }
            SetActionBusy(false, null);
        }
    }

    private async void RestoreTheme(object sender, EventArgs e)
    {
        if (actionBusy || appearanceBusy || previewOnly || service == null) return;
        SetActionBusy(true, "正在恢复原生主题...");
        try
        {
            ThemeApplyResult result = await service.RestoreAsync();
            if (disposed) return;
            if (result == null || !result.Success)
            {
                ShowThemeFailure("主题恢复失败", result == null ? "主题服务未返回结果。" : result.Message);
                return;
            }
            SetStatus(String.IsNullOrWhiteSpace(result.Message) ? "已恢复原生主题。" : result.Message);
        }
        catch (Exception ex) { ShowThemeFailure("主题恢复失败", ex.Message); }
        finally { SetActionBusy(false, null); }
    }

    private void OpenDetails(ThemeCard card)
    {
        if (card == null || String.IsNullOrWhiteSpace(card.Item.DetailUrl)) return;
        SelectCard(card);
        try { Process.Start(card.Item.DetailUrl); }
        catch (Exception ex) { SetStatus("打开主题详情失败：" + ex.Message); }
    }

    private void OpenImagePreview(ThemeCard card)
    {
        if (card == null) return;
        SelectCard(card);
        byte[] bytes = card.PreviewBytes;
        if (bytes == null || bytes.Length == 0) return;
        using (Form form = new Form())
        using (PictureBox picture = new PictureBox())
        {
            form.Text = card.Item.Name ?? "主题预览";
            form.StartPosition = FormStartPosition.CenterParent;
            form.BackColor = DesktopPalette.Page;
            form.HandleCreated += delegate { DesktopPalette.LightTitleBar(form); };
            form.ClientSize = new Size(1000, 700);
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image source = Image.FromStream(stream)) picture.Image = new Bitmap(source);
            form.Controls.Add(picture);
            form.ShowDialog(this);
        }
    }

    private void SetActionBusy(bool busy, string message)
    {
        if (disposed) return;
        actionBusy = busy;
        closeButton.Enabled = !busy;
        restoreButton.Enabled = !busy && !previewOnly && service != null;
        themeComboBox.Enabled = !busy;
        skinComboBox.Enabled = !busy;
        appearanceApplyButton.Enabled = !busy && !previewOnly && controller != null;
        appearanceCloseButton.Enabled = !busy;
        foreach (ThemeCard card in cardsPanel.Controls) card.SetActionsEnabled(!busy && !appearanceBusy && !previewOnly && service != null && card.Item.Installable);
        UpdatePageControls();
        if (!String.IsNullOrWhiteSpace(message)) SetStatus(message);
    }

    private void SetStatus(string text)
    {
        if (statusLabel != null && !disposed) statusLabel.Text = String.IsNullOrWhiteSpace(text) ? "" : text;
    }

    private void ShowThemeFailure(string title, string detail)
    {
        SetStatus(title + "。");
        if (disposed) return;
        MessageBox.Show(this, String.IsNullOrWhiteSpace(detail) ? title + "。" : detail,
            title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ThemeGalleryFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!actionBusy && !appearanceBusy) return;
        e.Cancel = true;
        if (restartCancellation != null) restartCancellation.Cancel();
        if (appearanceBusy) SetAppearanceStatus("外观保存进行中，请等待完成后再关闭窗口。");
        else SetStatus("主题操作进行中，请等待完成后再关闭窗口。");
    }

    private sealed class ThemeCard : DesktopSurface
    {
        public readonly ThemeGalleryItem Item;
        private readonly PictureBox picture;
        private readonly Label title;
        private readonly Label meta;
        private readonly Label summary;
        private readonly Label status;
        private readonly Button restart;
        private readonly Button details;
        private readonly Color normalBack;
        private readonly Color normalText;
        private readonly Color normalBorder;
        private bool selected;
        private string lastStatus;
        private Image currentImage;
        private byte[] previewBytes;
        public byte[] PreviewBytes { get { return previewBytes; } }
        public event Action<ThemeCard> RestartClicked;
        public event Action<ThemeCard> DetailsClicked;
        public event Action<ThemeCard> SelectClicked;
        public event Action<ThemeCard> PreviewClicked;
        public Image PreviewImage { get { return currentImage; } }

        public ThemeCard(ThemeGalleryItem item, Color panelBack, Color textColor, Color mutedColor, Color borderColor)
        {
            Item = item;
            Font = new Font("Segoe UI", 18F, FontStyle.Regular, GraphicsUnit.Pixel);
            normalBack = panelBack;
            normalText = textColor;
            normalBorder = borderColor;
            BackColor = panelBack;
            BorderStyle = BorderStyle.None;
            BorderColor = borderColor;
            CornerRadius = 12;
            Margin = new Padding(5);
            Height = 330;
            picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = DesktopPalette.Input, Cursor = Cursors.Hand };
            picture.Click += delegate { if (SelectClicked != null) SelectClicked(this); };
            picture.DoubleClick += delegate { if (PreviewClicked != null) PreviewClicked(this); };
            title = NewLabel(21F, FontStyle.Bold, textColor);
            title.Cursor = Cursors.Hand;
            title.Click += delegate { if (SelectClicked != null) SelectClicked(this); };
            meta = NewLabel(16.5F, FontStyle.Regular, mutedColor);
            summary = NewLabel(16.5F, FontStyle.Regular, textColor);
            summary.AutoEllipsis = true;
            status = NewLabel(16.5F, FontStyle.Regular, mutedColor);
            restart = NewButton("应用并重启"); restart.Click += delegate { if (RestartClicked != null) RestartClicked(this); };
            details = NewButton("详情"); details.Click += delegate { if (DetailsClicked != null) DetailsClicked(this); };
            Controls.Add(picture); Controls.Add(title); Controls.Add(meta); Controls.Add(summary); Controls.Add(status);
            Controls.Add(restart); Controls.Add(details);
            UpdateText();
            LayoutCard();
        }

        private Label NewLabel(float size, FontStyle style, Color color)
        {
            return new Label { ForeColor = color, Font = new Font(Font.FontFamily, size, style, GraphicsUnit.Pixel), AutoEllipsis = true, UseCompatibleTextRendering = true };
        }

        private Button NewButton(string text)
        {
            return new DesktopButton
            {
                Text = text,
                Height = 28,
                Width = 74,
                Margin = new Padding(2),
                Font = new Font(Font.FontFamily, 18F, FontStyle.Regular, GraphicsUnit.Pixel),
                CornerRadius = 9, BorderColor = Color.FromArgb(224, 230, 239),
                AccentColor = Color.FromArgb(25, 104, 223), ForeColor = Color.FromArgb(35, 48, 68)
            };
        }

        public void LayoutCard()
        {
            int width = Math.Max(238, Width);
            picture.SetBounds(12, 12, width - 24, Math.Max(140, (width - 24) * 9 / 16));
            int titleHeight = Math.Max(24, title.Font.Height + 6);
            int bodyHeight = Math.Max(20, meta.Font.Height + 6);
            int detailHeight = Math.Max(22, summary.Font.Height + 7);
            int statusHeight = Math.Max(22, status.Font.Height + 7);
            title.SetBounds(12, picture.Bottom + 10, width - 24, titleHeight);
            meta.SetBounds(12, title.Bottom, width - 24, bodyHeight);
            summary.SetBounds(12, meta.Bottom, width - 24, detailHeight);
            status.SetBounds(12, summary.Bottom, width - 24, statusHeight);
            int buttonHeight = Math.Max(38, restart.Font.Height + 12);
            Height = status.Bottom + buttonHeight + 20;
            int buttonWidth = Math.Max(90, (width - 32) / 2);
            int buttonRow = Height - buttonHeight - 12;
            restart.SetBounds(12, buttonRow, buttonWidth, buttonHeight);
            details.SetBounds(20 + buttonWidth, buttonRow, buttonWidth, buttonHeight);
        }

        public void SetActionsEnabled(bool enabled)
        {
            restart.Enabled = enabled;
            details.Enabled = !String.IsNullOrWhiteSpace(Item.DetailUrl);
        }

        public void SetSelected(bool value)
        {
            selected = value;
            BackColor = selected ? DesktopPalette.Selected : normalBack;
            BorderColor = selected ? Color.FromArgb(25, 104, 223) : normalBorder;
            Invalidate();
            title.ForeColor = selected ? Color.FromArgb(25, 104, 223) : normalText;
            status.Text = selected ? "已选择 · " + (lastStatus ?? "") : (lastStatus ?? "");
        }

        private string AvailabilityText()
        {
            if (Item.Installable) return "可应用 · 应用时校验";
            return String.IsNullOrWhiteSpace(Item.AvailabilityDetail) ? "仅预览或暂不可用" :
                "仅预览或暂不可用：" + Item.AvailabilityDetail;
        }

        private void SetStatusText(string message)
        {
            lastStatus = String.IsNullOrWhiteSpace(message) ? "" : message;
            status.Text = selected ? "已选择 · " + lastStatus : lastStatus;
        }

        public void SetPreviewStatus(string message)
        {
            picture.Image = null;
            if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
            SetColorPlaceholder();
            SetStatusText(message);
        }

        private void SetColorPlaceholder()
        {
            Bitmap placeholder = new Bitmap(Math.Max(1, picture.Width), Math.Max(1, picture.Height));
            Color background = ParseColor(Item.Background, picture.BackColor);
            Color accent = ParseColor(Item.Accent, DesktopPalette.Accent);
            using (Graphics graphics = Graphics.FromImage(placeholder))
            {
                graphics.Clear(background);
                using (Brush brush = new SolidBrush(accent))
                    graphics.FillRectangle(brush, 0, Math.Max(0, placeholder.Height - 22), placeholder.Width, 22);
            }
            currentImage = placeholder;
            picture.Image = currentImage;
        }

        private Color ParseColor(string value, Color fallback)
        {
            try { return String.IsNullOrWhiteSpace(value) ? fallback : ColorTranslator.FromHtml(value); }
            catch { return fallback; }
        }

        public async Task<bool> SetPreviewAsync(byte[] bytes)
        {
            if (IsDisposed || Disposing) return false;
            if (bytes == null || bytes.Length == 0)
            {
                SetPreviewStatus("暂无预览图");
                return false;
            }
            int width = Math.Max(1, picture.Width);
            int height = Math.Max(1, picture.Height);
            Color background = picture.BackColor;
            Bitmap thumbnail = null;
            try
            {
                thumbnail = await Task.Run(delegate
                {
                    Bitmap rendered = new Bitmap(width, height);
                    try
                    {
                        using (MemoryStream stream = new MemoryStream(bytes))
                        using (Image source = Image.FromStream(stream))
                        using (Graphics graphics = Graphics.FromImage(rendered))
                        {
                            graphics.Clear(background);
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.SmoothingMode = SmoothingMode.HighQuality;
                            float scale = Math.Min((float)width / source.Width, (float)height / source.Height);
                            int drawWidth = Math.Max(1, (int)(source.Width * scale));
                            int drawHeight = Math.Max(1, (int)(source.Height * scale));
                            int x = (width - drawWidth) / 2;
                            int y = (height - drawHeight) / 2;
                            graphics.DrawImage(source, new Rectangle(x, y, drawWidth, drawHeight));
                        }
                        return rendered;
                    }
                    catch
                    {
                        rendered.Dispose();
                        throw;
                    }
                });
                if (IsDisposed || Disposing) return false;
                picture.Image = null;
                if (currentImage != null) currentImage.Dispose();
                currentImage = thumbnail;
                thumbnail = null;
                picture.Image = currentImage;
                previewBytes = bytes;
                SetStatusText(AvailabilityText());
                return true;
            }
            catch
            {
                if (!IsDisposed && !Disposing) SetPreviewStatus("预览格式不可用");
                return false;
            }
            finally { if (thumbnail != null) thumbnail.Dispose(); }
        }

        private void UpdateText()
        {
            title.Text = Item.Name ?? Item.Id;
            meta.Text = String.IsNullOrWhiteSpace(Item.Author) ? "" : "作者：" + Item.Author;
            summary.Text = Item.Summary ?? "";
            SetStatusText(AvailabilityText());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                picture.Image = null;
                if (currentImage != null) { currentImage.Dispose(); currentImage = null; }
                previewBytes = null;
            }
            base.Dispose(disposing);
        }
    }
}
