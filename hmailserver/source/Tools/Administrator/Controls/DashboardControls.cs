// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Custom dashboard controls: arc gauge, stat card and sparkline.
// All controls are pure GDI+ and double-buffered; no external dependencies.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace hMailServer.Administrator.Controls
{
   /// <summary>
   /// A 270-degree arc gauge with green/amber/red zones.
   /// </summary>
   public class ArcGauge : Control
   {
      private double value_;
      private double maximum_ = 100;
      private string unitText_ = "";

      public ArcGauge()
      {
         SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
         ForeColor = Color.FromArgb(50, 58, 69);
         BackColor = Color.White;
         Font = new Font("Segoe UI", 9F);
      }

      public double Value
      {
         get { return value_; }
         set { value_ = Math.Max(0, value); Invalidate(); }
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

         double ratio = Math.Min(1.0, value_ / maximum_);

         // Track
         using (Pen track = new Pen(Color.FromArgb(232, 236, 241), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, arcRect, startAngle, sweepTotal);

         // Value arc, colored by load
         Color arcColor = ratio < 0.60 ? Color.FromArgb(46, 160, 67)
                        : ratio < 0.85 ? Color.FromArgb(227, 160, 8)
                        : Color.FromArgb(207, 34, 46);

         if (ratio > 0.001)
         {
            using (Pen pen = new Pen(arcColor, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
               g.DrawArc(pen, arcRect, startAngle, (float)(sweepTotal * ratio));
         }

         // Centre value
         string valueText = ((long)value_).ToString("N0");
         using (Font big = new Font("Segoe UI", Math.Max(10f, side * 0.16f), FontStyle.Bold))
         using (Font small = new Font("Segoe UI", Math.Max(7f, side * 0.07f)))
         using (Brush fg = new SolidBrush(ForeColor))
         using (Brush dim = new SolidBrush(Color.FromArgb(130, 140, 152)))
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
            using (Brush fg = new SolidBrush(Color.FromArgb(80, 90, 102)))
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
   /// A flat statistics card: large number, caption and a colored accent bar.
   /// </summary>
   public class StatCard : Control
   {
      private string value_ = "0";
      private Color accent_ = Color.FromArgb(9, 105, 218);

      public StatCard()
      {
         SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
         BackColor = Color.White;
         Size = new Size(190, 84);
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

      protected override void OnPaint(PaintEventArgs e)
      {
         base.OnPaint(e);
         Graphics g = e.Graphics;
         g.SmoothingMode = SmoothingMode.AntiAlias;
         g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

         Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
         using (GraphicsPath path = RoundedRect(r, 6))
         {
            using (Brush bg = new SolidBrush(Color.White))
               g.FillPath(bg, path);
            using (Pen border = new Pen(Color.FromArgb(216, 222, 228)))
               g.DrawPath(border, path);
         }

         using (Brush accent = new SolidBrush(accent_))
            g.FillRectangle(accent, 0, 8, 4, Height - 16);

         using (Font valueFont = new Font("Segoe UI", 17F, FontStyle.Bold))
         using (Font capFont = new Font("Segoe UI", 9F))
         using (Brush fg = new SolidBrush(Color.FromArgb(36, 41, 47)))
         using (Brush dim = new SolidBrush(Color.FromArgb(110, 119, 129)))
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
   /// Rolling line chart used for message throughput history.
   /// </summary>
   public class Sparkline : Control
   {
      private readonly List<double> points_ = new List<double>();
      private int capacity_ = 120;

      public Sparkline()
      {
         SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
         BackColor = Color.White;
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
         Graphics g = e.Graphics;
         g.SmoothingMode = SmoothingMode.AntiAlias;
         g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

         Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
         using (GraphicsPath path = StatCard.RoundedRect(r, 6))
         {
            using (Brush bg = new SolidBrush(Color.White))
               g.FillPath(bg, path);
            using (Pen border = new Pen(Color.FromArgb(216, 222, 228)))
               g.DrawPath(border, path);
         }

         const int padLeft = 12, padRight = 12, padTop = 30, padBottom = 14;
         Rectangle plot = new Rectangle(padLeft, padTop, Width - padLeft - padRight, Height - padTop - padBottom);
         if (plot.Width < 20 || plot.Height < 10)
            return;

         using (Font capFont = new Font("Segoe UI Semibold", 9.5f))
         using (Brush fg = new SolidBrush(Color.FromArgb(80, 90, 102)))
            g.DrawString(Text, capFont, fg, 12, 8);

         double max = 1;
         foreach (double p in points_)
            if (p > max) max = p;

         // Grid lines
         using (Pen grid = new Pen(Color.FromArgb(238, 241, 245)))
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
            using (Brush dim = new SolidBrush(Color.FromArgb(140, 148, 158)))
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

         // Area fill
         PointF[] area = new PointF[pts.Length + 2];
         Array.Copy(pts, area, pts.Length);
         area[pts.Length] = new PointF(pts[pts.Length - 1].X, plot.Bottom);
         area[pts.Length + 1] = new PointF(pts[0].X, plot.Bottom);
         using (Brush fill = new SolidBrush(Color.FromArgb(28, 9, 105, 218)))
            g.FillPolygon(fill, area);

         using (Pen line = new Pen(Color.FromArgb(9, 105, 218), 2f) { LineJoin = LineJoin.Round })
            g.DrawLines(line, pts);

         // Max label
         using (Font dimFont = new Font("Segoe UI", 8F))
         using (Brush dim = new SolidBrush(Color.FromArgb(140, 148, 158)))
            g.DrawString(((long)max).ToString("N0") + " peak", dimFont, dim, plot.Right - 64, 10);
      }

      protected override void OnTextChanged(EventArgs e)
      {
         base.OnTextChanged(e);
         Invalidate();
      }
   }
}
