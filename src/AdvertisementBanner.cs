using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

internal sealed class AdvertisementBanner : DesktopSurface
{
    private Advertisement[] items = new Advertisement[0];
    private int index;
    private readonly Label textLabel;
    private readonly Label outgoingLabel;
    private readonly Panel textViewport;
    private readonly TableLayoutPanel paging;
    private readonly Label countLabel;
    private readonly Button previousButton;
    private readonly Button nextButton;
    private readonly Button detailsButton;
    private readonly Timer rotationTimer = new Timer { Interval = 6000 };
    private readonly Timer animationTimer = new Timer { Interval = 16 };
    private readonly Stopwatch animationClock = new Stopwatch();
    private int slideDirection;
    private bool showingDetails;
    internal Advertisement Current { get { return items.Length == 0 ? null : items[index]; } }

    internal AdvertisementBanner()
    {
        DoubleBuffered = true;
        BackColor = DesktopPalette.Surface;
        BorderColor = Color.FromArgb(224, 230, 239);
        CornerRadius = 8;
        Padding = new Padding(12, 8, 12, 8);
        Margin = new Padding(0, 0, 0, 8);
        Dock = DockStyle.Fill;
        Visible = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(layout);
        paging = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
        paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28F));
        paging.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28F));
        previousButton = Button("‹", "上一条广告");
        nextButton = Button("›", "下一条广告");
        previousButton.Click += delegate { MoveSelection(-1); };
        nextButton.Click += delegate { MoveSelection(1); };
        countLabel = new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = DesktopPalette.Muted };
        paging.Controls.Add(previousButton, 0, 0);
        paging.Controls.Add(countLabel, 1, 0);
        paging.Controls.Add(nextButton, 2, 0);
        layout.Controls.Add(paging, 1, 0);

        textViewport = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
        textLabel = CreateTextLabel("广告内容");
        outgoingLabel = CreateTextLabel("上一条广告内容");
        outgoingLabel.Visible = false;
        textViewport.Controls.Add(textLabel);
        textViewport.Controls.Add(outgoingLabel);
        textViewport.Resize += delegate { FinishTransition(); };
        MouseEventHandler scroll = delegate(object sender, MouseEventArgs e) {
            if (items.Length < 2 || e.Delta == 0) return;
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
            MoveSelection(e.Delta > 0 ? -1 : 1);
        };
        MouseWheel += scroll;
        textViewport.MouseWheel += scroll;
        textLabel.MouseWheel += scroll;
        outgoingLabel.MouseWheel += scroll;
        detailsButton = Button("查看详情 →", "查看当前广告详情");
        detailsButton.Margin = Padding.Empty;
        detailsButton.Click += delegate { ShowDetails(); };
        layout.Controls.Add(textViewport, 0, 0);
        layout.Controls.Add(detailsButton, 2, 0);
        animationTimer.Tick += delegate { AnimateTransition(); };
        rotationTimer.Tick += delegate {
            if (!showingDetails && !ContainsFocus && !ClientRectangle.Contains(PointToClient(Cursor.Position)))
                MoveSelection(1);
        };
    }

    private static Label CreateTextLabel(string name)
    {
        return new Label { AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = name, Margin = Padding.Empty, ForeColor = Color.FromArgb(104, 117, 138),
            UseCompatibleTextRendering = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel) };
    }

    private static Button Button(string text, string name)
    {
        var button = new DesktopButton { Text = text, AccessibleName = name, Dock = DockStyle.Fill, Margin = Padding.Empty,
            Borderless = true, CornerRadius = 6, Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel),
            Cursor = Cursors.Hand, BackColor = DesktopPalette.Surface, ForeColor = Color.FromArgb(25, 104, 223),
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = DesktopPalette.Border;
        return button;
    }

    internal void SetItems(Advertisement[] value)
    {
        Advertisement previous = Current;
        FinishTransition();
        items = value;
        index = 0;
        if (previous != null)
            for (int i = 0; i < items.Length; i++)
                if (items[i].Content == previous.Content && items[i].LinkUrl == previous.LinkUrl) { index = i; break; }
        UpdateCurrent();
        RestartRotation();
    }

    internal void MoveSelection(int offset)
    {
        if (items.Length < 2 || offset % items.Length == 0) return;
        FinishTransition();
        Advertisement previous = Current;
        index = (index + offset % items.Length + items.Length) % items.Length;
        UpdateCurrent();
        RestartRotation();
        if (!Visible || textViewport.ClientSize.Width == 0) return;
        slideDirection = offset < 0 ? -1 : 1;
        outgoingLabel.Text = previous.Content;
        outgoingLabel.Bounds = textViewport.ClientRectangle;
        outgoingLabel.Visible = true;
        textLabel.SetBounds(slideDirection * textViewport.ClientSize.Width, 0, textViewport.ClientSize.Width, textViewport.ClientSize.Height);
        detailsButton.Enabled = false;
        animationClock.Restart();
        animationTimer.Start();
    }

    private void AnimateTransition()
    {
        double progress = Math.Min(1, animationClock.Elapsed.TotalMilliseconds / 260);
        int distance = (int)(textViewport.ClientSize.Width * (1 - Math.Pow(1 - progress, 3)));
        outgoingLabel.Left = -slideDirection * distance;
        textLabel.Left = slideDirection * (textViewport.ClientSize.Width - distance);
        if (progress >= 1) FinishTransition();
    }

    private void FinishTransition()
    {
        animationTimer.Stop();
        animationClock.Reset();
        if (outgoingLabel != null) outgoingLabel.Visible = false;
        if (textLabel != null) textLabel.Bounds = textViewport.ClientRectangle;
        if (detailsButton != null) detailsButton.Enabled = items.Length > 0;
    }

    private void RestartRotation()
    {
        rotationTimer.Stop();
        if (Visible && items.Length > 1) rotationTimer.Start();
    }

    private void UpdateCurrent()
    {
        textLabel.Text = Current == null ? "" : Current.Content;
        countLabel.Text = items.Length == 0 ? "" : (index + 1) + "/" + items.Length;
        paging.Visible = items.Length > 1;
        previousButton.Enabled = nextButton.Enabled = items.Length > 1;
        detailsButton.Enabled = items.Length > 0;
        Visible = items.Length > 0;
    }

    private void ShowDetails()
    {
        Advertisement current = Current;
        if (current == null) return;
        using (var dialog = new Form { Text = "推广详情", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 330), MinimumSize = new Size(420, 280), Font = Font, BackColor = DesktopPalette.Surface,
            ShowInTaskbar = false, MaximizeBox = false, MinimizeBox = false, Padding = new Padding(20) })
        {
            var content = new TextBox { Text = current.Content, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                BackColor = DesktopPalette.Surface, ForeColor = DesktopPalette.Text };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft };
            var close = new DesktopButton { Text = "关闭", Width = 88, Height = 34, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(close);
            if (current.LinkUrl != null)
            {
                var open = new DesktopButton { Text = "访问链接 ↗", Width = 116, Height = 34 };
                open.Click += delegate
                {
                    try { Process.Start(new ProcessStartInfo(current.LinkUrl) { UseShellExecute = true }); }
                    catch (Exception) { MessageBox.Show(dialog, "无法打开广告链接，请检查默认浏览器。", "打开链接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                actions.Controls.Add(open);
                var destination = new Label { Text = new Uri(current.LinkUrl).Host, Dock = DockStyle.Bottom,
                    Height = 32, ForeColor = DesktopPalette.Muted, AutoEllipsis = true, UseMnemonic = false };
                dialog.Controls.Add(content);
                dialog.Controls.Add(destination);
            }
            else dialog.Controls.Add(content);
            dialog.Controls.Add(actions);
            dialog.CancelButton = close;
            dialog.HandleCreated += delegate { DesktopPalette.LightTitleBar(dialog); };
            showingDetails = true;
            try { dialog.ShowDialog(FindForm()); }
            finally { showingDetails = false; RestartRotation(); }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (items.Length > 1 && (keyData == Keys.Left || keyData == Keys.Right))
        {
            MoveSelection(keyData == Keys.Left ? -1 : 1);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) FinishTransition();
        RestartRotation();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            rotationTimer.Dispose();
            animationTimer.Dispose();
            animationClock.Stop();
        }
        base.Dispose(disposing);
    }

}
