using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Clipmon.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _generatedIcon;
    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? CaptureNowRequested;
    public event EventHandler? PauseResumeRequested;
    public event EventHandler? QuitRequested;

    public TrayIconService()
    {
        _generatedIcon = BuildClipboardIcon();

        _icon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "Clipmon",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _icon.MouseClick += OnMouseClick;
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateMonitoringState(bool isMonitoring)
    {
        if (_icon.ContextMenuStrip is null) return;
        if (_icon.ContextMenuStrip.Items["pauseResume"] is ToolStripMenuItem item)
        {
            item.Text = isMonitoring ? "Pause monitoring" : "Resume monitoring";
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Clipmon", null,
            (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty)) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Capture clipboard now", null,
            (_, _) => CaptureNowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem("Pause monitoring", null,
            (_, _) => PauseResumeRequested?.Invoke(this, EventArgs.Empty)) { Name = "pauseResume" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null,
            (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty)));
        return menu;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Procedurally generated clipboard glyph so we don't need to ship a binary asset.
    /// Renders a gradient rounded square with a clean white clipboard outline.
    /// </summary>
    private static Icon BuildClipboardIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Background: rounded square with diagonal gradient (matches app accent)
            var bgRect = new RectangleF(2, 2, 28, 28);
            using var bgPath = RoundedRect(bgRect, 7);
            using var bgBrush = new LinearGradientBrush(
                bgRect,
                Color.FromArgb(0x5B, 0x8D, 0xEF),
                Color.FromArgb(0x7C, 0x66, 0xE8),
                LinearGradientMode.ForwardDiagonal);
            g.FillPath(bgBrush, bgPath);

            // Clipboard outline
            using var outlinePen = new Pen(Color.White, 1.7f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            var bodyRect = new RectangleF(9, 11, 14, 15);
            using var bodyPath = RoundedRect(bodyRect, 2.4f);
            g.DrawPath(outlinePen, bodyPath);

            // Top tab
            var tabRect = new RectangleF(12.5f, 8, 7, 4.5f);
            using var tabPath = RoundedRect(tabRect, 1.4f);
            g.DrawPath(outlinePen, tabPath);

            // Connector dot
            using var dotBrush = new SolidBrush(Color.White);
            g.FillEllipse(dotBrush, 14.5f, 9.6f, 3, 1.4f);

            // Text lines
            using var linePen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(linePen, 12.5f, 17, 19.5f, 17);
            g.DrawLine(linePen, 12.5f, 20, 19.5f, 20);
            g.DrawLine(linePen, 12.5f, 23, 17.5f, 23);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            using var stream = new MemoryStream();
            temp.Save(stream);
            stream.Position = 0;
            return new Icon(stream);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Visible = false;
        _icon.Dispose();
        _generatedIcon.Dispose();
    }
}
