// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// VS Code-style command palette (Ctrl+K): fuzzy search across every page
// in the navigation tree and jump straight to it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using hMailServer.Administrator.Utilities;

namespace hMailServer.Administrator.Dialogs
{
   public class formCommandPalette : Form
   {
      private class Entry
      {
         public string Path;          // "Settings  >  Protocols  >  SMTP"
         public TreeNode Node;
         public int Score;
      }

      private readonly TextBox searchBox_;
      private readonly ListBox resultsList_;
      private readonly List<Entry> allEntries_ = new List<Entry>();
      private readonly List<Entry> matches_ = new List<Entry>();

      public TreeNode SelectedNode { get; private set; }

      public formCommandPalette(TreeView tree)
      {
         FormBorderStyle = FormBorderStyle.None;
         StartPosition = FormStartPosition.Manual;
         ShowInTaskbar = false;
         KeyPreview = true;
         Size = new Size(560, 380);
         BackColor = Theme.C.Surface;
         Padding = new Padding(1);

         searchBox_ = new TextBox
         {
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 13F),
            BackColor = Theme.C.Surface,
            ForeColor = Theme.C.Text,
            Location = new Point(16, 14),
            Width = Width - 32
         };
         searchBox_.TextChanged += delegate { Filter(); };
         Controls.Add(searchBox_);

         resultsList_ = new ListBox
         {
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 34,
            Font = new Font("Segoe UI", 10F),
            BackColor = Theme.C.Surface,
            ForeColor = Theme.C.Text,
            Location = new Point(8, 48),
            Size = new Size(Width - 16, Height - 58),
            IntegralHeight = false,
            TabStop = false
         };
         resultsList_.DrawItem += DrawResultItem;
         resultsList_.MouseMove += (s, e) =>
         {
            int index = resultsList_.IndexFromPoint(e.Location);
            if (index >= 0 && index != resultsList_.SelectedIndex)
               resultsList_.SelectedIndex = index;
         };
         resultsList_.Click += delegate { Accept(); };
         Controls.Add(resultsList_);

         KeyDown += OnKey;
         searchBox_.KeyDown += OnKey;
         Deactivate += delegate { DialogResult = DialogResult.Cancel; Close(); };
         Paint += (s, e) =>
         {
            using (Pen border = new Pen(Theme.C.Accent))
               e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            using (Pen line = new Pen(Theme.C.Border))
               e.Graphics.DrawLine(line, 8, 44, Width - 8, 44);
         };

         IndexTree(tree.Nodes, "");
         Filter();
      }

      protected override CreateParams CreateParams
      {
         get
         {
            CreateParams cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
         }
      }

      private void IndexTree(TreeNodeCollection nodes, string prefix)
      {
         foreach (TreeNode node in nodes)
         {
            string path = prefix.Length == 0 ? node.Text : prefix + "  \u203A  " + node.Text;
            allEntries_.Add(new Entry { Path = path, Node = node });
            IndexTree(node.Nodes, path);
         }
      }

      private void Filter()
      {
         string query = searchBox_.Text.Trim();
         matches_.Clear();

         foreach (Entry entry in allEntries_)
         {
            entry.Score = FuzzyScore(entry.Path, query);
            if (entry.Score >= 0)
               matches_.Add(entry);
         }

         matches_.Sort((a, b) => b.Score.CompareTo(a.Score));

         resultsList_.BeginUpdate();
         resultsList_.Items.Clear();
         foreach (Entry entry in matches_)
            resultsList_.Items.Add(entry);
         resultsList_.EndUpdate();

         if (resultsList_.Items.Count > 0)
            resultsList_.SelectedIndex = 0;
      }

      /// <summary>
      /// Case-insensitive subsequence match. Higher score for consecutive
      /// runs, word starts and shorter paths. Returns -1 when not a match.
      /// </summary>
      private static int FuzzyScore(string text, string query)
      {
         if (query.Length == 0)
            return 0;

         int score = 0, textIndex = 0, run = 0;
         foreach (char qc in query)
         {
            char q = char.ToLowerInvariant(qc);
            bool found = false;
            while (textIndex < text.Length)
            {
               char t = char.ToLowerInvariant(text[textIndex]);
               if (t == q)
               {
                  bool wordStart = textIndex == 0 || !char.IsLetterOrDigit(text[textIndex - 1]);
                  run++;
                  score += 2 + (wordStart ? 6 : 0) + run * 2;
                  textIndex++;
                  found = true;
                  break;
               }
               run = 0;
               textIndex++;
            }
            if (!found)
               return -1;
         }

         return score - text.Length / 8;
      }

      private void DrawResultItem(object sender, DrawItemEventArgs e)
      {
         if (e.Index < 0 || e.Index >= matches_.Count)
            return;

         ThemePalette p = Theme.C;
         bool selected = (e.State & DrawItemState.Selected) != 0;
         Entry entry = matches_[e.Index];

         using (SolidBrush back = new SolidBrush(selected ? p.SurfaceAlt : p.Surface))
            e.Graphics.FillRectangle(back, e.Bounds);

         if (selected)
            using (SolidBrush bar = new SolidBrush(p.Accent))
               e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top + 4, 3, e.Bounds.Height - 8);

         // Render the leaf bold, the path muted.
         int sep = entry.Path.LastIndexOf('\u203A');
         string parent = sep > 0 ? entry.Path.Substring(0, sep + 1).Trim() : "";
         string leaf = sep > 0 ? entry.Path.Substring(sep + 1).Trim() : entry.Path;

         int x = e.Bounds.Left + 14;
         using (Font leafFont = new Font("Segoe UI Semibold", 10F))
         {
            TextRenderer.DrawText(e.Graphics, leaf, leafFont,
               new Point(x, e.Bounds.Top + 7), p.Text);
            x += TextRenderer.MeasureText(leaf, leafFont).Width + 10;
         }

         if (parent.Length > 0)
            TextRenderer.DrawText(e.Graphics, parent, resultsList_.Font,
               new Point(x, e.Bounds.Top + 9), p.TextMuted);
      }

      private void OnKey(object sender, KeyEventArgs e)
      {
         if (e.KeyCode == Keys.Escape)
         {
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
         }
         else if (e.KeyCode == Keys.Enter)
         {
            Accept();
            e.Handled = true;
            e.SuppressKeyPress = true;
         }
         else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
         {
            int count = resultsList_.Items.Count;
            if (count > 0)
            {
               int index = resultsList_.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
               resultsList_.SelectedIndex = Math.Max(0, Math.Min(count - 1, index));
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
         }
      }

      private void Accept()
      {
         if (resultsList_.SelectedIndex >= 0 && resultsList_.SelectedIndex < matches_.Count)
         {
            SelectedNode = matches_[resultsList_.SelectedIndex].Node;
            DialogResult = DialogResult.OK;
            Close();
         }
      }

      /// <summary>Shows the palette anchored near the top of the owner window.</summary>
      public static TreeNode Prompt(Form owner, TreeView tree)
      {
         using (formCommandPalette palette = new formCommandPalette(tree))
         {
            palette.Location = new Point(
               owner.Left + (owner.Width - palette.Width) / 2,
               owner.Top + 96);
            palette.ShowDialog(owner);
            return palette.SelectedNode;
         }
      }
   }
}
