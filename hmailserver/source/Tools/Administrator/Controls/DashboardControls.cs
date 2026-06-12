// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Custom dashboard controls: arc gauge, stat card and sparkline.
// All controls are pure GDI+ and double-buffered; no external dependencies.
// Fully theme-aware (light/dark via Utilities.Theme) with soft glow accents
// and eased value animations for a modern, fluid feel.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using hMailServer.Administrator.Utilities;

namespace hMailServer.Administrator.Controls
{
   /// <summary>
   /// A 270-degree arc gauge with green/amber/red zones, glow accent and
   /// animated needle value.
   /// </summary>
   public class ArcGauge : Control
   {
      private double value_;
      private double shownValue_;
      private double maximum_ = 100;
      private string unitText_ = "";
      private readonly Timer animator_;

      public ArcGauge()
      {
         SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
         Font = new Font("Segoe UI", 9F);

         animator_ = new Timer { Interval = 33 };
         animator_.Tick += AnimatorTick;

         Theme.Changed += ThemeChanged;
         Disposed += delegate { Theme.Changed -= ThemeChanged; animator_.Dispose(); };
      }

      private void ThemeChanged(object sender, EventArgs e)
      {
         Invalidate();
      }

      private void AnimatorTick(object sender, EventArgs e)
      {
         double delta = value_ - shownValue_;
         if (Math.Abs(delta) < 0.5)
         {
            shownValue_ = value_;
            animator_.Stop();
         }
         else
         {
            shownValue_ += delta * 0.25; // ease-out
         }
         Invalidate();
      }

      public double Value
      {
         get { return value_; }
         set
         {
            value_ = Math.Max(0, value);
            if (!animator_.Enabled && Math.Abs(value_ - shownValue_) >= 0.5)
               animator_.Start();
            Invalidate();
         }
      }

      public double Maximum
      {
         get { return maximum_; }
         set { maximum_ = Math.Max(1, value); Invalidate(); }
      }

      public string UnitText
      {
         get { return unitText_; }
         set { unitText_ = value ?? ""; Invalidate(); }
      }

      protected override void OnPaint(PaintEventArgs e)
      {
         base.OnPaint(e);
         ThemePalette p = Theme.C;
         BackColor = p.Surface;

         Graphics g = e.Graphics;
         g.SmoothingMode = SmoothingMode.AntiAlias;
         g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

         int side = Math.Min(Width, Height - 18);
         if (side < 40)
            return;

         float thickness = Math.Max(8f, side * 0.11f);
         RectangleF arcRect = new RectangleF(
            (Width - side) / 2f + thickness / 2f,
            thickness / 2f,
            side - thickness,
            side - thickness);

         const float startAngle = 135f;
         const float sweepTotal = 270f;

         double ratio = Math.Min(1.0, shownValue_ / maximum_);

         // Track
         using (Pen track = new Pen(p.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, arcRect, startAngle, sweepTotal);

         // Value arc, colored by load, with a soft glow pass underneath
         Color arcColor = ratio < 0.60 ? p.Success
                        : ratio < 0.85 ? p.Warning
                        : p.Danger;

         if (ratio > 0.001)
         {
            float sweep = (float)(sweepTotal * ratio);
            using (Pen glow = new Pen(Color.FromArgb(46, arcColor), thickness + 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
               g.DrawArc(glow, arcRect, startAngle, sweep);
            using (Pen pen = new Pen(arcColor, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
               g.DrawArc(pen, arcRect, startAngle, sweep);
         }

         // Centre value
         string valueText = ((long)Math.Round(shownValue_)).ToString("N0");
         using (Font big = new Font("Segoe UI", Math.Max(10f, side * 0.16f), FontStyle.Bold))
         using (Font small = new Font("Segoe UI", Math.Max(7f, side * 0.07f)))
         using (Brush fg = new SolidBrush(p.Text))
         using (Brush dim = new SolidBrush(p.TextMuted))
         {
            SizeF vs = g.MeasureString(valueText, big);
            float cx = Width / 2f, cy = arcRect.Top + arcRect.Height / 2f;
            g.DrawString(valueText, big, fg, cx - vs.Width / 2f, cy - vs.Height / 2f - 4);

            string maxText = "of " + ((long)maximum_).ToString("N0") + (unitText_.Length > 0 ? " " + unitText_ : "");
            SizeF ms = g.MeasureString(maxText, small);
            g.DrawString(maxText, small, dim, cx - ms.Width / 2f, cy + vs.Height / 2f - 8);
         }

         // Caption under the gauge
         if (!string.IsNullOrEmpty(Text))
         {
            using (Font cap = new Font("Segoe UI Semibold", 9.5f))
            using (Brush fg = new SolidBrush(p.TextMuted))
            {
               SizeF cs = g.MeasureString(Text, cap);
               g.DrawString(Text, cap, fg, (Width - cs.Width) / 2f, Height - cs.Height - 2);
            }
         }
      }

      protected override void OnTextChanged(EventArgs e)
      {
         base.OnTextChanged(e);
         Invalidate();
      }
   }

   /// <summary>
   /// A flat statistics card: large number, caption, colored accent bar,
   /// hover highlight and rounded corners.
   /// </summary>
   public class StatCard : Control
   {
      private string value_ = "0";
      private Color accent_;
      private bool hovered_;

      public StatCard()
      {
         SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
         Size = new Size(190, 84);
         accent_ = Theme.C.Accent;

         Theme.Changed += ThemeChanged;
         Disposed += delegate { Theme.Changed -= ThemeChanged; };
      }

      private void ThemeChanged(object sender, EventArgs e)
      {
         Invalidate();
      }

      public string ValueText
      {
         get { return value_; }
         set { value_ = value ?? "0"; Invalidate(); }
      }

      public Color AccentColor
      {
         get { return accent_; }
         set { accent_ = value; Invalidate(); }
      }

      protected override void OnMouseEnter(EventArgs e)
      {
         base.OnMouseEnter(e);
         hovered_ = true;
         Invalidate();
      }

      protected override void OnMouseLeave(EventArgs e)
      {
         base.OnMouseLeave(e);
         hovered_ = false;
         Invalidate();
      }

      protected override void OnPaint(PaintEventArgs e)
      {
         base.OnPaint(e);
         ThemePalette p = Theme.C;
         BackColor = Parent != null ? Parent.BackColor : p.Background;

         Graphics g = e.Graphics;
         g.SmoothingMode = SmoothingMode.AntiAlias;
         g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

         Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
         using (GraphicsPath path = RoundedRect(r, 8))
         {
            using (Brush bg = new SolidBrush(hovered_ ? p.SurfaceAlt : p.Surface))
               g.FillPath(bg, path);
            using (Pen border = new Pen(hovered_ ? accent_ : p.Border))
               g.DrawPath(border, path);
         }

         // Accent bar with glow
         using (Brush glow = new SolidBrush(Color.FromArgb(60, accent_)))
            g.FillRectangle(glow, 0, 6, 7, Height - 12);
         using (Brush accent = new SolidBrush(accent_))
            g.FillRectangle(accent, 0, 8, 4, Height - 16);

         using (Font valueFont = new Font("Segoe UI", 17F, FontStyle.Bold))
         using (Font capFont = new Font("Segoe UI", 9F))
         using (Brush fg = new SolidBrush(p.Text))
         using (Brush dim = new SolidBrush(p.TextMuted))
         {
            g.DrawString(value_, valueFont, fg, 14, 10);
            g.DrawString(Text, capFont, dim, 15, Height - 28);
         }
      }

      protected override void OnTextChanged(EventArgs e)
      {
         base.OnTextChanged(e);
         Invalidate();
      }

      internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
      {
         GraphicsPath path = new GraphicsPath();
         int d = radius * 2;
         path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
         path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
         path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
         path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
         path.CloseFigure();
         return path;
      }
   }

   /// <summary>
   /// Rolling line chart used for message throughput history, with gradient
   /// area fill and glow line.
   /// </summary>
   public class Sparkline : Control
   {
      private readonly List<double> points_ = new List<double>();
      private int capacity_ = 120;

      public Sparkline()
      {
         SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

         Theme.Changed += ThemeChanged;
         Disposed += delegate { Theme.Changed -= ThemeChanged; };
      }

      private void ThemeChanged(object sender, EventArgs e)
      {
         Invalidate();
      }

      public int Capacity
      {
         get { return capacity_; }
         set { capacity_ = Math.Max(10, value); }
      }

      public void AddPoint(double value)
      {
         points_.Add(Math.Max(0, value));
         while (points_.Count > capacity_)
            points_.RemoveAt(0);
         Invalidate();
      }

      public void Reset()
      {
         points_.Clear();
         Invalidate();
      }

      protected override void OnPaint(PaintEventArgs e)
      {
         base.OnPaint(e);
         ThemePalette p = Theme.C;
         BackColor = Parent != null ? Parent.BackColor : p.Background;

         Graphics g = e.Graphics;
         g.SmoothingMode = SmoothingMode.AntiAlias;
         g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

         Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
         using (GraphicsPath path = StatCard.RoundedRect(r, 8))
         {
            using (Brush bg = new SolidBrush(p.Surface))
               g.FillPath(bg, path);
            using (Pen border = new Pen(p.Border))
               g.DrawPath(border, path);
         }

         const int padLeft = 12, padRight = 12, padTop = 30, padBottom = 14;
         Rectangle plot = new Rectangle(padLeft, padTop, Width - padLeft - padRight, Height - padTop - padBottom);
         if (plot.Width < 20 || plot.Height < 10)
            return;

         using (Font capFont = new Font("Segoe UI Semibold", 9.5f))
         using (Brush fg = new SolidBrush(p.TextMuted))
            g.DrawString(Text, capFont, fg, 12, 8);

         double max = 1;
         foreach (double pt in points_)
            if (pt > max) max = pt;

         // Grid lines
         using (Pen grid = new Pen(p.Track))
         {
            for (int i = 1; i <= 3; i++)
            {
               int y = plot.Top + plot.Height * i / 4;
               g.DrawLine(grid, plot.Left, y, plot.Right, y);
            }
         }

         if (points_.Count < 2)
         {
            using (Font dimFont = new Font("Segoe UI", 9F))
            using (Brush dim = new SolidBrush(p.TextMuted))
               g.DrawString("Collecting data...", dimFont, dim, plot.Left + 4, plot.Top + plot.Height / 2 - 8);
            return;
         }

         PointF[] pts = new PointF[points_.Count];
         for (int i = 0; i < points_.Count; i++)
         {
            float x = plot.Left + (float)plot.Width * i / (capacity_ - 1);
            float y = plot.Bottom - (float)(plot.Height * (points_[i] / max));
            pts[i] = new PointF(x, y);
         }

         // Gradient area fill under the line
         PointF[] area = new PointF[pts.Length + 2];
         Array.Copy(pts, area, pts.Length);
         area[pts.Length] = new PointF(pts[pts.Length - 1].X, plot.Bottom);
         area[pts.Length + 1] = new PointF(pts[0].X, plot.Bottom);
         using (LinearGradientBrush fill = new LinearGradientBrush(
            new Point(0, plot.Top), new Point(0, plot.Bottom),
            Color.FromArgb(64, p.Accent), Color.FromArgb(0, p.Accent)))
            g.FillPolygon(fill, area);

         // Glow pass + crisp line
         using (Pen glowPen = new Pen(Color.FromArgb(56, p.Accent), 5f) { LineJoin = LineJoin.Round })
            g.DrawLines(glowPen, pts);
         using (Pen line = new Pen(p.Accent, 2f) { LineJoin = LineJoin.Round })
            g.DrawLines(line, pts);

         // Max label
         using (Font dimFont = new Font("Segoe UI", 8F))
         using (Brush dim = new SolidBrush(p.TextMuted))
            g.DrawString(((long)max).ToString("N0") + " peak", dimFont, dim, plot.Right - 64, 10);
      }

      protected override void OnTextChanged(EventArgs e)
      {
         base.OnTextChanged(e);
         Invalidate();
      }
   }
}
