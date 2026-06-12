// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Central theme engine for hMailServer Administrator.
//
// Provides light and dark palettes, persists the user's choice in the
// registry, recursively restyles any control tree, switches window title
// bars to dark mode via DWM, enables native dark scrollbars/headers via
// uxtheme, and installs a thread-local CBT hook so every Form the
// application opens (including modal dialogs) is themed automatically.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace hMailServer.Administrator.Utilities
{
   public enum ThemeMode
   {
      Light,
      Dark
   }

   /// <summary>Color tokens for one theme variant.</summary>
   public class ThemePalette
   {
      public Color Background;     // window / pane background
      public Color Surface;        // cards, inputs-on-background, menu bar
      public Color SurfaceAlt;     // hover states, subtle fills
      public Color Border;         // hairline borders
      public Color Text;           // primary text
      public Color TextMuted;      // secondary text
      public Color Accent;         // brand / focus color
      public Color AccentText;     // text on accent
      public Color Success;
      public Color Warning;
      public Color Danger;
      public Color Purple;
      public Color Input;          // text box background
      public Color Track;          // gauge/sparkline track lines
   }

   public static class Theme
   {
      private const string RegistryKeyPath = @"Software\hMailServer\Administrator";
      private const string RegistryValueName = "Theme";

      public static readonly ThemePalette Light = new ThemePalette
      {
         Background = Color.FromArgb(246, 248, 250),
         Surface = Color.White,
         SurfaceAlt = Color.FromArgb(234, 238, 242),
         Border = Color.FromArgb(208, 215, 222),
         Text = Color.FromArgb(36, 41, 47),
         TextMuted = Color.FromArgb(87, 96, 106),
         Accent = Color.FromArgb(9, 105, 218),
         AccentText = Color.White,
         Success = Color.FromArgb(46, 160, 67),
         Warning = Color.FromArgb(227, 160, 8),
         Danger = Color.FromArgb(207, 34, 46),
         Purple = Color.FromArgb(130, 80, 223),
         Input = Color.White,
         Track = Color.FromArgb(232, 236, 241)
      };

      public static readonly ThemePalette Dark = new ThemePalette
      {
         Background = Color.FromArgb(13, 17, 23),
         Surface = Color.FromArgb(22, 27, 34),
         SurfaceAlt = Color.FromArgb(33, 38, 45),
         Border = Color.FromArgb(48, 54, 61),
         Text = Color.FromArgb(230, 237, 243),
         TextMuted = Color.FromArgb(139, 148, 158),
         Accent = Color.FromArgb(47, 129, 247),
         AccentText = Color.White,
         Success = Color.FromArgb(63, 185, 80),
         Warning = Color.FromArgb(210, 153, 34),
         Danger = Color.FromArgb(248, 81, 73),
         Purple = Color.FromArgb(163, 113, 247),
         Input = Color.FromArgb(13, 17, 23),
         Track = Color.FromArgb(33, 38, 45)
      };

      public static ThemeMode Mode { get; private set; } = ThemeMode.Light;

      /// <summary>The active palette.</summary>
      public static ThemePalette C
      {
         get { return Mode == ThemeMode.Dark ? Dark : Light; }
      }

      public static bool IsDark
      {
         get { return Mode == ThemeMode.Dark; }
      }

      /// <summary>Raised after the mode changes and all open forms restyled.</summary>
      public static event EventHandler Changed;

      /// <summary>
      /// Loads the saved preference. When none exists, follows the Windows
      /// "app mode" (light/dark) system preference.
      /// </summary>
      public static void LoadPreference()
      {
         try
         {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
            {
               object value = key == null ? null : key.GetValue(RegistryValueName);
               if (value is string)
               {
                  Mode = string.Equals((string) value, "Dark", StringComparison.OrdinalIgnoreCase)
                     ? ThemeMode.Dark
                     : ThemeMode.Light;
                  return;
               }
            }

            using (RegistryKey personalize = Registry.CurrentUser.OpenSubKey(
               @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
               object appsLight = personalize == null ? null : personalize.GetValue("AppsUseLightTheme");
               if (appsLight is int && (int) appsLight == 0)
                  Mode = ThemeMode.Dark;
            }
         }
         catch (Exception)
         {
            Mode = ThemeMode.Light;
         }
      }

      public static void SavePreference()
      {
         try
         {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath))
            {
               if (key != null)
                  key.SetValue(RegistryValueName, Mode == ThemeMode.Dark ? "Dark" : "Light");
            }
         }
         catch (Exception)
         {
            // Theme persistence is best-effort.
         }
      }

      /// <summary>Switches mode, restyles every open form and persists the choice.</summary>
      public static void SetMode(ThemeMode mode)
      {
         if (Mode == mode)
            return;

         Mode = mode;
         SavePreference();
         UpdateMenuRenderer();

         foreach (Form form in System.Windows.Forms.Application.OpenForms)
            Apply(form);

         EventHandler handler = Changed;
         if (handler != null)
            handler(null, EventArgs.Empty);
      }

      /// <summary>Recursively themes a control tree (including the form chrome).</summary>
      public static void Apply(Control root)
      {
         if (root == null || root.IsDisposed)
            return;

         Form form = root as Form;
         if (form != null)
            ApplyTitleBar(form);

         ApplyRecursive(root);

         root.Invalidate(true);
      }

      private static void ApplyRecursive(Control control)
      {
         ApplyToControl(control);

         foreach (Control child in control.Controls)
            ApplyRecursive(child);
      }

      private static void ApplyToControl(Control control)
      {
         ThemePalette p = C;

         if (control is Form || control is UserControl)
         {
            control.BackColor = p.Background;
            control.ForeColor = p.Text;
         }
         else if (control is Button)
         {
            Button button = (Button) control;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = p.Border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = p.SurfaceAlt;
            button.FlatAppearance.MouseDownBackColor = p.Border;
            button.BackColor = p.Surface;
            button.ForeColor = p.Text;
            button.UseVisualStyleBackColor = false;
         }
         else if (control is TextBoxBase) // TextBox, RichTextBox, MaskedTextBox
         {
            TextBoxBase box = (TextBoxBase) control;
            box.BackColor = p.Input;
            box.ForeColor = p.Text;
            TextBox plain = box as TextBox;
            if (plain != null && plain.BorderStyle == BorderStyle.Fixed3D)
               plain.BorderStyle = BorderStyle.FixedSingle;
            RichTextBox rich = box as RichTextBox;
            if (rich != null && rich.BorderStyle == BorderStyle.Fixed3D)
               rich.BorderStyle = BorderStyle.FixedSingle;
            SetNativeScrollbarTheme(box);
         }
         else if (control is ComboBox)
         {
            ComboBox combo = (ComboBox) control;
            combo.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
            combo.BackColor = p.Input;
            combo.ForeColor = p.Text;
         }
         else if (control is ListView)
         {
            ListView list = (ListView) control;
            list.BackColor = p.Input;
            list.ForeColor = p.Text;
            if (list.BorderStyle == BorderStyle.Fixed3D)
               list.BorderStyle = BorderStyle.FixedSingle;
            SetNativeScrollbarTheme(list);
         }
         else if (control is TreeView)
         {
            TreeView tree = (TreeView) control;
            tree.BackColor = p.Input;
            tree.ForeColor = p.Text;
            if (tree.BorderStyle == BorderStyle.Fixed3D)
               tree.BorderStyle = BorderStyle.FixedSingle;
            tree.LineColor = p.Border;
            SetNativeScrollbarTheme(tree);
         }
         else if (control is ListBox)
         {
            ListBox listBox = (ListBox) control;
            listBox.BackColor = p.Input;
            listBox.ForeColor = p.Text;
            if (listBox.BorderStyle == BorderStyle.Fixed3D)
               listBox.BorderStyle = BorderStyle.FixedSingle;
            SetNativeScrollbarTheme(listBox);
         }
         else if (control is NumericUpDown)
         {
            control.BackColor = p.Input;
            control.ForeColor = p.Text;
         }
         else if (control is DataGridView)
         {
            DataGridView grid = (DataGridView) control;
            grid.BackgroundColor = p.Background;
            grid.GridColor = p.Border;
            grid.DefaultCellStyle.BackColor = p.Input;
            grid.DefaultCellStyle.ForeColor = p.Text;
            grid.DefaultCellStyle.SelectionBackColor = p.Accent;
            grid.DefaultCellStyle.SelectionForeColor = p.AccentText;
            grid.ColumnHeadersDefaultCellStyle.BackColor = p.Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Text;
            grid.EnableHeadersVisualStyles = !IsDark;
            SetNativeScrollbarTheme(grid);
         }
         else if (control is GroupBox)
         {
            GroupBox group = (GroupBox) control;
            group.ForeColor = p.Text;
            group.Paint -= GroupBoxPaint;
            group.Paint += GroupBoxPaint;
         }
         else if (control is TabControl)
         {
            TabControl tabs = (TabControl) control;
            if (IsDark)
            {
               tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
               tabs.DrawItem -= TabControlDrawItem;
               tabs.DrawItem += TabControlDrawItem;
            }
            else
            {
               tabs.DrawItem -= TabControlDrawItem;
               tabs.DrawMode = TabDrawMode.Normal;
            }
         }
         else if (control is TabPage)
         {
            control.BackColor = p.Background;
            control.ForeColor = p.Text;
         }
         else if (control is LinkLabel)
         {
            LinkLabel link = (LinkLabel) control;
            link.LinkColor = p.Accent;
            link.ActiveLinkColor = p.Accent;
            link.VisitedLinkColor = p.Accent;
            link.ForeColor = p.Text;
         }
         else if (control is Label || control is CheckBox || control is RadioButton)
         {
            control.ForeColor = p.Text;
         }
         else if (control is MenuStrip || control is StatusStrip || control is ToolStrip)
         {
            control.BackColor = p.Surface;
            control.ForeColor = p.Text;
         }
         else if (control is Panel || control is SplitContainer || control is SplitterPanel ||
                  control is TableLayoutPanel || control is FlowLayoutPanel)
         {
            control.BackColor = p.Background;
            control.ForeColor = p.Text;
         }
      }

      private static void GroupBoxPaint(object sender, PaintEventArgs e)
      {
         GroupBox group = (GroupBox) sender;
         ThemePalette p = C;
         Graphics g = e.Graphics;

         g.Clear(group.BackColor == Color.Transparent || group.Parent == null
            ? p.Background
            : group.Parent.BackColor);

         int textOffset = 8;
         Size textSize = TextRenderer.MeasureText(group.Text, group.Font);
         int top = group.Font.Height / 2;

         Rectangle border = new Rectangle(0, top, group.Width - 1, group.Height - top - 1);
         using (Pen pen = new Pen(p.Border))
            g.DrawRectangle(pen, border);

         if (!string.IsNullOrEmpty(group.Text))
         {
            Rectangle textRect = new Rectangle(textOffset - 2, 0, textSize.Width + 4, textSize.Height);
            using (SolidBrush fill = new SolidBrush(group.Parent == null ? p.Background : group.Parent.BackColor))
               g.FillRectangle(fill, textRect);
            TextRenderer.DrawText(g, group.Text, group.Font, new Point(textOffset, 0), p.Text);
         }
      }

      private static void TabControlDrawItem(object sender, DrawItemEventArgs e)
      {
         TabControl tabs = (TabControl) sender;
         ThemePalette p = C;
         bool selected = e.Index == tabs.SelectedIndex;

         using (SolidBrush back = new SolidBrush(selected ? p.Background : p.Surface))
            e.Graphics.FillRectangle(back, e.Bounds);

         if (selected)
            using (Pen accent = new Pen(p.Accent, 2f))
               e.Graphics.DrawLine(accent, e.Bounds.Left + 2, e.Bounds.Bottom - 1,
                  e.Bounds.Right - 2, e.Bounds.Bottom - 1);

         TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
            selected ? p.Text : p.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      }

      #region Menu rendering

      private sealed class ThemeColorTable : ProfessionalColorTable
      {
         public override Color MenuBorder { get { return C.Border; } }
         public override Color MenuItemBorder { get { return C.Accent; } }
         public override Color MenuItemSelected { get { return C.SurfaceAlt; } }
         public override Color MenuItemSelectedGradientBegin { get { return C.SurfaceAlt; } }
         public override Color MenuItemSelectedGradientEnd { get { return C.SurfaceAlt; } }
         public override Color MenuItemPressedGradientBegin { get { return C.Surface; } }
         public override Color MenuItemPressedGradientEnd { get { return C.Surface; } }
         public override Color MenuStripGradientBegin { get { return C.Surface; } }
         public override Color MenuStripGradientEnd { get { return C.Surface; } }
         public override Color ToolStripDropDownBackground { get { return C.Surface; } }
         public override Color ImageMarginGradientBegin { get { return C.Surface; } }
         public override Color ImageMarginGradientMiddle { get { return C.Surface; } }
         public override Color ImageMarginGradientEnd { get { return C.Surface; } }
         public override Color SeparatorDark { get { return C.Border; } }
         public override Color SeparatorLight { get { return C.Border; } }
         public override Color StatusStripGradientBegin { get { return C.Surface; } }
         public override Color StatusStripGradientEnd { get { return C.Surface; } }
      }

      private sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
      {
         public ThemeMenuRenderer() : base(new ThemeColorTable())
         {
            RoundedEdges = false;
         }

         protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
         {
            e.TextColor = e.Item.Enabled ? C.Text : C.TextMuted;
            base.OnRenderItemText(e);
         }

         protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
         {
            e.ArrowColor = C.Text;
            base.OnRenderArrow(e);
         }

         protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
         {
            // Draw a simple themed check mark instead of the default glyph.
            Rectangle r = e.ImageRectangle;
            using (Pen pen = new Pen(C.Accent, 2f))
            {
               e.Graphics.DrawLines(pen, new[]
               {
                  new Point(r.Left + 3, r.Top + r.Height / 2),
                  new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4),
                  new Point(r.Right - 3, r.Top + 3)
               });
            }
         }
      }

      private static void UpdateMenuRenderer()
      {
         ToolStripManager.Renderer = new ThemeMenuRenderer();
      }

      #endregion

      #region Native interop (dark title bar, dark scrollbars, auto-theming hook)

      [DllImport("dwmapi.dll", PreserveSig = true)]
      private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

      [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
      private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

      private const int DwmwaUseImmersiveDarkMode = 20;

      private static void ApplyTitleBar(Form form)
      {
         if (!form.IsHandleCreated)
         {
            form.HandleCreated -= FormHandleCreated;
            form.HandleCreated += FormHandleCreated;
            return;
         }

         int useDark = IsDark ? 1 : 0;
         try
         {
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
         }
         catch (Exception)
         {
            // Older Windows builds do not support the attribute.
         }
      }

      private static void FormHandleCreated(object sender, EventArgs e)
      {
         ApplyTitleBar((Form) sender);
      }

      private static void SetNativeScrollbarTheme(Control control)
      {
         if (!control.IsHandleCreated)
            return;
         try
         {
            SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
         }
         catch (Exception)
         {
         }
      }

      // --- CBT hook: themes every new top-level Form automatically ---

      private delegate IntPtr CbtProc(int nCode, IntPtr wParam, IntPtr lParam);

      [DllImport("user32.dll", SetLastError = true)]
      private static extern IntPtr SetWindowsHookEx(int idHook, CbtProc lpfn, IntPtr hMod, uint dwThreadId);

      [DllImport("user32.dll")]
      private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

      [DllImport("kernel32.dll")]
      private static extern uint GetCurrentThreadId();

      private const int WhCbt = 5;
      private const int HcbtActivate = 5;

      private static IntPtr hook_ = IntPtr.Zero;
      private static CbtProc hookProc_; // kept alive for the GC
      private static readonly HashSet<Form> themedForms_ = new HashSet<Form>();

      /// <summary>
      /// Installs a thread-scoped hook so any Form shown on the UI thread is
      /// themed automatically (covers every modal dialog without per-dialog code).
      /// </summary>
      public static void InstallAutoThemer()
      {
         if (hook_ != IntPtr.Zero)
            return;

         UpdateMenuRenderer();

         hookProc_ = CbtHookProc;
         hook_ = SetWindowsHookEx(WhCbt, hookProc_, IntPtr.Zero, GetCurrentThreadId());
      }

      private static IntPtr CbtHookProc(int nCode, IntPtr wParam, IntPtr lParam)
      {
         if (nCode == HcbtActivate)
         {
            try
            {
               Control control = Control.FromHandle(wParam);
               Form form = control as Form;
               if (form != null && !themedForms_.Contains(form))
               {
                  themedForms_.Add(form);
                  form.Disposed += delegate { themedForms_.Remove(form); };
                  Apply(form);
               }
            }
            catch (Exception)
            {
               // Never let theming break window activation.
            }
         }

         return CallNextHookEx(hook_, nCode, wParam, lParam);
      }

      #endregion
   }
}
