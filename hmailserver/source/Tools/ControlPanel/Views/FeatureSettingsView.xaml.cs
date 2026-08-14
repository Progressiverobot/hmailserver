using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

// System.Windows.Documents (imported above for Run/Inlines) declares a Typography
// of its own, so the unqualified name is ambiguous. Aliased to the Control Panel's
// type scale rather than dropping the import - the same fix ExternalSetupView and
// SslCertificatesView already carry.
using Typography = hMailServer.ControlPanel.Services.Typography;
using hMailServer.ControlPanel.Services;
using System.Linq;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Data-driven settings pages for the hMailServer.INI feature switches:
   /// transport security (DANE, DNSSEC, MTA-STS, ARC, TLS-RPT), automatic
   /// certificates (ACME), integrations (REST API, Prometheus metrics,
   /// ManageSieve), authentication (OAuth2, password storage), the DNS
   /// resolver and the public web services listener.
   /// </summary>
   public partial class FeatureSettingsView : UserControl, IPageLifecycle
   {
      public enum Section
      {
         Security,
         Automation,
         Integration,
         Hardening,
         Authentication,
         Dns,
         WebServices
      }

      private abstract class Setting
      {
         public string Key;
         public string Label;

         /// <summary>
         /// A caption printed under the editor and attached to it as accessible
         /// help text - for the statements in <see cref="SettingClaims"/> about
         /// what the server actually does with the value. Named to match
         /// ServerSettingsView, so the two settings views read the same way.
         /// </summary>
         public string Blurb;

         /// <summary>
         /// What a screen reader should call this editor. Assigned by
         /// <see cref="AssignAccessibleNames"/> before the editors are built,
         /// because the answer depends on the whole page - see
         /// <see cref="AccessibleNames"/>.
         /// </summary>
         public string AccessibleName;

         public abstract FrameworkElement CreateEditor(IniFeatureStore store);
         public abstract void Save(IniFeatureStore store);

         /// <summary>
         /// The value currently sitting in the editor, as the INI would spell it
         /// ("1"/"0" for booleans), or null before the editor has been built.
         /// The computed warnings read this so they react while the administrator
         /// is still typing, instead of only after a save and a page reload.
         /// </summary>
         public virtual string LiveValue => null;

         /// <summary>
         /// Calls <paramref name="handler"/> whenever the editor's value changes.
         /// No-op for editors that never take part in a computed warning.
         /// </summary>
         public virtual void OnEditorChanged(Action handler)
         {
         }

         protected static void SetAid(FrameworkElement element, string id)
         {
            if (element != null && !string.IsNullOrEmpty(id))
               System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
         }

         /// <summary>
         /// The AutomationId plus the accessible name.
         ///
         /// An AutomationId is for test automation and is never spoken. Every
         /// editor on this page is labelled by a separate TextBlock above it and
         /// WPF does not connect the two, so before this each of the 83 INI
         /// settings here was announced as an unnamed "edit" - including the
         /// password pepper, whose label carries the warning that changing it
         /// invalidates every stored password.
         /// </summary>
         protected void Describe(FrameworkElement element, string id)
         {
            SetAid(element, id);

            if (element != null && !string.IsNullOrEmpty(AccessibleName))
               System.Windows.Automation.AutomationProperties.SetName(element, AccessibleName);
         }

         /// <summary>
         /// Prints <see cref="Blurb"/> under the control and attaches it to the
         /// control as accessible help text. Both, for the reason given on
         /// ServerSettingsView's Annotate: a caption sitting loose in the panel is
         /// reached only after the editor, and a note saying the server does less
         /// than the control suggests has to be heard with it.
         /// </summary>
         protected void Annotate(FrameworkElement editor, Panel panel)
         {
            if (string.IsNullOrEmpty(Blurb))
               return;

            if (editor != null)
               System.Windows.Automation.AutomationProperties.SetHelpText(editor, Blurb);

            panel?.Children.Add(new TextBlock
            {
               Text = Blurb,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 4, 0, 0)
            });
         }
      }

      private class BoolSetting : Setting
      {
         public bool Default;
         private CheckBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();

            box_ = new CheckBox
            {
               Content = Label,
               IsChecked = store.ReadBool(Key, Default),
               FontSize = 13.5
            };
            SetAid(box_, Key);

            // A checkbox names itself from its Content, so it needs an override
            // only when the resolved name differs - i.e. when the same wording
            // appears on another card and has been qualified with the card title.
            if (!string.IsNullOrEmpty(AccessibleName) &&
                !string.Equals(AccessibleName, Label, StringComparison.Ordinal))
            {
               System.Windows.Automation.AutomationProperties.SetName(box_, AccessibleName);
            }

            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override void Save(IniFeatureStore store)
            => store.WriteBool(Key, box_.IsChecked == true);

         public override string LiveValue
            => box_ == null ? null : (box_.IsChecked == true ? "1" : "0");

         public override void OnEditorChanged(Action handler)
         {
            if (box_ == null || handler == null)
               return;
            box_.Checked += (s, e) => handler();
            box_.Unchecked += (s, e) => handler();
         }
      }

      private class TextSetting : Setting
      {
         public string Default = "";
         public string Placeholder = "";
         private Wpf.Ui.Controls.TextBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });
            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = store.Read(Key, Default),
               PlaceholderText = Placeholder,
               FontSize = 13,
               MaxWidth = 520,
               MinWidth = 320,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            Describe(box_, Key);
            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override void Save(IniFeatureStore store)
            => store.Write(Key, box_.Text.Trim());

         public override string LiveValue => box_?.Text;

         public override void OnEditorChanged(Action handler)
         {
            if (box_ != null && handler != null)
               box_.TextChanged += (s, e) => handler();
         }
      }

      /// <summary>
      /// A path field: a text box plus a "..." button that opens a file or folder
      /// picker (<see cref="PickFolder"/>) and writes the chosen path back into the box.
      /// </summary>
      private class PathSetting : Setting
      {
         public readonly string Default = "";
         public string Placeholder = "";
         public bool PickFolder;
         public string FileFilter = "All files (*.*)|*.*";
         private Wpf.Ui.Controls.TextBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });

            var row = new Grid { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = store.Read(Key, Default),
               PlaceholderText = Placeholder,
               FontSize = 13,
               HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Describe(box_, Key);
            Grid.SetColumn(box_, 0);
            row.Children.Add(box_);

            var browse = new Wpf.Ui.Controls.Button
            {
               Content = "\u2026",
               MinWidth = 40,
               Margin = new Thickness(8, 0, 0, 0),
               VerticalAlignment = VerticalAlignment.Bottom,
               ToolTip = PickFolder ? "Browse for a folder" : "Browse for a file"
            };
            SetAid(browse, Key + "Browse");
            // The content is a single ellipsis, so without this the certificate and
            // private-key browse buttons on the REST API card are announced as two
            // identical "\u2026" and there is no way to tell which one is which.
            System.Windows.Automation.AutomationProperties.SetName(browse,
               (PickFolder ? "Browse for a folder for " : "Browse for a file for ")
               + (AccessibleName ?? Label ?? "this setting"));
            browse.Click += (s, e) =>
            {
               string picked = PickFolder
                  ? PathPicker.PickFolder(box_.Text)
                  : PathPicker.PickFile(box_.Text, FileFilter);
               if (picked != null)
                  box_.Text = picked;
            };
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);

            panel.Children.Add(row);
            Annotate(box_, panel);
            return panel;
         }

         public override void Save(IniFeatureStore store)
            => store.Write(Key, box_.Text.Trim());

         public override string LiveValue => box_?.Text;

         public override void OnEditorChanged(Action handler)
         {
            if (box_ != null && handler != null)
               box_.TextChanged += (s, e) => handler();
         }
      }

      private class ChoiceSetting : Setting
      {
         public int Default;
         public (int Value, string Label)[] Options;
         private ComboBox combo_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });

            combo_ = new ComboBox { FontSize = 13, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            if (!int.TryParse(store.Read(Key, Default.ToString()), out int current))
               current = Default;
            foreach ((int value, string label) in Options)
            {
               var item = new ComboBoxItem { Content = label, Tag = value };
               combo_.Items.Add(item);
               if (value == current)
                  combo_.SelectedItem = item;
            }
            if (combo_.SelectedItem == null && combo_.Items.Count > 0)
               combo_.SelectedIndex = 0;

            Describe(combo_, Key);
            panel.Children.Add(combo_);
            Annotate(combo_, panel);
            return panel;
         }

         public override void Save(IniFeatureStore store)
         {
            int value = combo_.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : Default;
            store.Write(Key, value.ToString());
         }

         public override string LiveValue
            => combo_?.SelectedItem is ComboBoxItem cbi ? ((int) cbi.Tag).ToString() : null;

         public override void OnEditorChanged(Action handler)
         {
            if (combo_ != null && handler != null)
               combo_.SelectionChanged += (s, e) => handler();
         }
      }

      /// <summary>
      /// A write-only secret field. The current value is never shown (it may be a
      /// DPAPI-protected blob); a placeholder indicates whether one is already set,
      /// and the value is only written when the admin types a new one — so leaving
      /// the field blank keeps the existing secret untouched.
      /// </summary>
      private class SecretSetting : Setting
      {
         /// <summary>
         /// Placeholder text shown when no secret is configured yet. It was called
         /// Note; renamed because the base class now carries the note that says what
         /// the server does with the value, and two members of that name would have
         /// been one silently shadowing the other.
         /// </summary>
         public string Hint = "";

         /// <summary>
         /// Adds a "Generate" button that fills the box with a strong random value
         /// from <see cref="PasswordGenerator"/>. For the server-wide secrets (SRS,
         /// BATV, the metrics bearer token) whose only requirement is randomness:
         /// without the button, "enter a secret" quietly invites a weak one.
         /// </summary>
         public bool OfferGenerate;

         private Wpf.Ui.Controls.PasswordBox box_;
         private bool hasStored_;

         /// <summary>
         /// True when a secret is effectively configured: one is stored in the INI,
         /// or the administrator has typed one into the editor. What the computed
         /// warnings ask, since a blank box keeps the stored value.
         /// </summary>
         public bool IsConfigured(IniFeatureStore store)
         {
            if (box_ != null && !string.IsNullOrEmpty(box_.Password))
               return true;
            if (box_ != null)
               return hasStored_;
            return !string.IsNullOrEmpty(store.Read(Key, "").Trim());
         }

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            hasStored_ = !string.IsNullOrEmpty(store.Read(Key, "").Trim());
            string placeholder = hasStored_
               ? "A secret is configured — leave blank to keep it"
               : (string.IsNullOrEmpty(Hint) ? "Enter a secret" : Hint);

            box_ = new Wpf.Ui.Controls.PasswordBox
            {
               PlaceholderText = placeholder,
               FontSize = 13,
               HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Describe(box_, Key);

            // A PasswordBox's placeholder is not part of its accessible name, and
            // whether a secret is already stored is the one thing this control
            // conveys that its label does not - leaving the field blank keeps the
            // existing value, so a listener who cannot see the placeholder has no
            // way to know whether there is one.
            System.Windows.Automation.AutomationProperties.SetHelpText(box_,
               string.IsNullOrEmpty(Blurb) ? placeholder : placeholder + " " + Blurb);

            if (OfferGenerate)
            {
               var row = new Grid { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
               row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
               row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

               Grid.SetColumn(box_, 0);
               row.Children.Add(box_);

               var generate = new Wpf.Ui.Controls.Button
               {
                  Content = "Generate",
                  Margin = new Thickness(8, 0, 0, 0),
                  VerticalAlignment = VerticalAlignment.Bottom,
                  ToolTip = "Fill in a strong random secret"
               };
               SetAid(generate, Key + "Generate");
               // The visible content is the same word on every secret that offers
               // it, so the accessible name says which secret this one fills.
               System.Windows.Automation.AutomationProperties.SetName(generate,
                  "Generate a random value for " + (AccessibleName ?? Label ?? "this secret"));
               generate.Click += (s, e) => box_.Password = PasswordGenerator.Generate(32);
               Grid.SetColumn(generate, 1);
               row.Children.Add(generate);

               panel.Children.Add(row);
            }
            else
            {
               box_.MaxWidth = 520;
               box_.MinWidth = 320;
               box_.HorizontalAlignment = HorizontalAlignment.Left;
               panel.Children.Add(box_);
            }

            // Annotate would overwrite the help text set just above, so only the
            // printed caption is wanted here.
            if (!string.IsNullOrEmpty(Blurb))
            {
               panel.Children.Add(new TextBlock
               {
                  Text = Blurb,
                  FontSize = Typography.Caption,
                  TextWrapping = TextWrapping.Wrap,
                  Opacity = 0.65,
                  Margin = new Thickness(0, 4, 0, 0)
               });
            }

            return panel;
         }

         public override void Save(IniFeatureStore store)
         {
            string entered = box_.Password;
            if (!string.IsNullOrEmpty(entered))
               store.Write(Key, entered);
            // Blank = keep the existing secret.
         }

         public override string LiveValue => box_?.Password;

         public override void OnEditorChanged(Action handler)
         {
            // Wpf.Ui's PasswordBox derives from TextBox, so TextChanged fires as
            // the (masked) text changes - including when Generate fills it in.
            if (box_ != null && handler != null)
               box_.TextChanged += (s, e) => handler();
         }
      }

      /// <summary>
      /// Direct reads and writes of hMailServer.INI sections other than [Settings],
      /// which is all <see cref="IniFeatureStore"/> speaks. Used by the sending-limit
      /// editors ([SendingLimits] / [SendingLimitsOverrides]) and by the computed
      /// warnings that need [Directories] DataFolder to locate the ACME certificate.
      /// </summary>
      private static class IniDirect
      {
         [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
         private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder result, int size, string filePath);

         [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
         private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

         [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
         private static extern int GetPrivateProfileSection(string section, char[] result, int size, string filePath);

         [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
         private static extern bool WritePrivateProfileSection(string section, string value, string filePath);

         public static string ReadValue(string iniPath, string section, string key, string defaultValue)
         {
            if (string.IsNullOrEmpty(iniPath))
               return defaultValue;
            var buffer = new StringBuilder(4096);
            GetPrivateProfileString(section, key, defaultValue, buffer, buffer.Capacity, iniPath);
            return buffer.ToString();
         }

         public static void WriteValue(string iniPath, string section, string key, string value)
         {
            if (string.IsNullOrEmpty(iniPath))
               throw new InvalidOperationException("hMailServer.INI was not found on this machine.");
            WritePrivateProfileString(section, key, value, iniPath);
         }

         /// <summary>The key=value lines of one section, in file order.</summary>
         public static List<string> ReadSectionLines(string iniPath, string section)
         {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(iniPath))
               return lines;

            // 32767 characters is the documented ceiling for this API.
            var buffer = new char[32767];
            int copied = GetPrivateProfileSection(section, buffer, buffer.Length, iniPath);

            int start = 0;
            for (int i = 0; i < copied; i++)
            {
               if (buffer[i] != '\0')
                  continue;
               if (i > start)
                  lines.Add(new string(buffer, start, i - start));
               start = i + 1;
            }

            return lines;
         }

         /// <summary>Replaces one section's lines wholesale (empty list clears it).</summary>
         public static void WriteSectionLines(string iniPath, string section, IReadOnlyList<string> lines)
         {
            if (string.IsNullOrEmpty(iniPath))
               throw new InvalidOperationException("hMailServer.INI was not found on this machine.");

            // The API wants "line\0line\0\0"; the marshaller appends one
            // terminator, so an explicit trailing '\0' completes the pair. An
            // empty section is a lone '\0', which the marshaller turns into the
            // required double terminator.
            string joined = lines.Count == 0 ? "\0" : string.Join("\0", lines) + "\0";
            WritePrivateProfileSection(section, joined, iniPath);
         }
      }

      /// <summary>
      /// A text setting stored in an hMailServer.INI section other than [Settings].
      /// Same editor as <see cref="TextSetting"/>; only the section differs.
      /// </summary>
      private class SectionTextSetting : Setting
      {
         public string Section;
         public string Default = "";
         public string Placeholder = "";
         private Wpf.Ui.Controls.TextBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });
            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = IniDirect.ReadValue(store.IniPath, Section, Key, Default),
               PlaceholderText = Placeholder,
               FontSize = 13,
               MaxWidth = 520,
               MinWidth = 320,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            Describe(box_, Key);
            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override void Save(IniFeatureStore store)
            => IniDirect.WriteValue(store.IniPath, Section, Key, box_.Text.Trim());

         public override string LiveValue => box_?.Text;

         public override void OnEditorChanged(Action handler)
         {
            if (box_ != null && handler != null)
               box_.TextChanged += (s, e) => handler();
         }
      }

      /// <summary>
      /// Edits a whole INI section as lines, one entry per line. Built for
      /// [SendingLimitsOverrides], whose entries are per-address values rather than
      /// fixed keys. Reads the section back on every build (so it never misreports
      /// its own state) and only rewrites the section when the text was actually
      /// changed, so merely opening and saving the page cannot disturb the file.
      /// </summary>
      private class SectionLinesSetting : Setting
      {
         public string Section;
         public string Placeholder = "";
         private Wpf.Ui.Controls.TextBox box_;
         private string loaded_;

         private static string Normalize(IEnumerable<string> lines)
            => string.Join("\n", lines.Select(l => l.Trim()).Where(l => l.Length > 0));

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });

            loaded_ = Normalize(IniDirect.ReadSectionLines(store.IniPath, Section));

            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = loaded_,
               PlaceholderText = Placeholder,
               FontSize = 13,
               MaxWidth = 520,
               MinWidth = 320,
               MinHeight = 88,
               AcceptsReturn = true,
               TextWrapping = TextWrapping.NoWrap,
               VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            Describe(box_, Key);
            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override void Save(IniFeatureStore store)
         {
            string current = Normalize(box_.Text.Split('\n'));
            if (current == loaded_)
               return;

            IniDirect.WriteSectionLines(store.IniPath, Section,
               current.Length == 0 ? new string[0] : current.Split('\n'));
            loaded_ = current;
         }
      }

      /// <summary>
      /// A row that names something edited on another page, and goes there.
      ///
      /// Two uses, both the same shape. A setting that has MOVED is a link an
      /// administrator has already followed once and a note in somebody's runbook,
      /// and neither survives a silent relocation: the page opens, the setting is
      /// not on it, and the reasonable conclusion is that the feature was removed.
      /// And a setting that has always been elsewhere - the API keys, beside the
      /// listener they authenticate - is worth naming where it will be looked for
      /// rather than only where it lives.
      ///
      /// Deliberately not persisted, and deliberately carries no Key, so that the
      /// settings-index generator cannot index it: the Ctrl+K palette must send
      /// somebody searching for "session tickets" to the page that owns the
      /// setting, not to a signpost pointing at it.
      /// </summary>
      private class ElsewhereSetting : Setting
      {
         private readonly string page_;

         /// <param name="page">Nav key of the page that owns the setting.</param>
         /// <param name="caption">What is over there, in the administrator's words.</param>
         public ElsewhereSetting(string page, string caption)
         {
            page_ = page;
            Label = caption;
         }

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            // LocationOf gives the full "group > page" trail and returns "" - not
            // null - for a key it does not know, so an empty result has to fall
            // back to the title rather than be printed as a blank destination.
            string destination = NavigationMap.LocationOf(page_);
            if (string.IsNullOrEmpty(destination))
               destination = NavigationMap.TitleOf(page_);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

            var text = new TextBlock
            {
               Text = Label + "  →  " + destination,
               FontSize = 13,
               TextWrapping = TextWrapping.Wrap,
               VerticalAlignment = VerticalAlignment.Center,
               MaxWidth = 420
            };
            row.Children.Add(text);

            var button = new Wpf.Ui.Controls.Button
            {
               Content = "Open…",
               Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
               FontSize = Typography.Caption,
               Padding = new Thickness(8, 3, 8, 3),
               Margin = new Thickness(10, 0, 0, 0),
               VerticalAlignment = VerticalAlignment.Center,
               Cursor = System.Windows.Input.Cursors.Hand,
               ToolTip = "Open " + destination
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "Open " + destination + ", which now has " + Label);
            SetAid(button, "elsewhere-" + page_);
            button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page_);
            row.Children.Add(button);

            return row;
         }

         /// <summary>Nothing to save: this row edits nothing.</summary>
         public override void Save(IniFeatureStore store)
         {
         }
      }

      /// <summary>What one computed warning has decided to say, or null for nothing.</summary>
      private class WarningState
      {
         public StatusLevel Level;
         public string Text;
      }

      /// <summary>
      /// A computed warning printed inside a card, under the settings it is about.
      /// <see cref="Compute"/> runs against the live editors (falling back to the
      /// INI for values on other pages) and returns null when there is nothing to
      /// say. Each carries a colour, a shape AND a word via StatusSemantics, so the
      /// meaning survives greyscale, colour blindness and High Contrast.
      /// </summary>
      private class WarningDef
      {
         /// <summary>AutomationId of the warning row, so tests can find it.</summary>
         public string Aid;

         public Func<WarningState> Compute;
      }

      private class CardDef
      {
         public string Title;
         public string Blurb;
         public List<Setting> Settings = new();
         public List<WarningDef> Warnings = new();
      }

      private readonly Section section_;
      private readonly IniFeatureStore store_ = new();
      private List<CardDef> cards_;

      /// <summary>One built warning row: the definition and the WPF pieces it drives.</summary>
      private class WarningRow
      {
         public WarningDef Def;
         public Grid Row;
         public System.Windows.Shapes.Path Mark;
         public TextBlock Text;
      }

      private readonly List<WarningRow> warningRows_ = new();

      // ---- inputs for the computed warnings -----------------------------------
      //
      // Each reads the live editor when the setting is on this page (so a warning
      // reacts while the administrator types) and falls back to the INI file for
      // settings that live on another page. The fallback default must match the
      // server's own default in IniFileSettings.cpp, or the warning would reason
      // about a configuration the server does not run.

      private Setting FindSetting_(string key)
      {
         if (cards_ == null)
            return null;

         foreach (CardDef card in cards_)
         foreach (Setting setting in card.Settings)
         {
            if (string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))
               return setting;
         }

         return null;
      }

      private string LiveText_(string key, string fallbackDefault)
      {
         Setting setting = FindSetting_(key);
         string live = setting?.LiveValue;
         return live ?? store_.Read(key, fallbackDefault);
      }

      private bool LiveBool_(string key, bool fallbackDefault)
      {
         Setting setting = FindSetting_(key);
         string live = setting?.LiveValue;
         return live != null ? live == "1" : store_.ReadBool(key, fallbackDefault);
      }

      private int LiveInt_(string key, int fallbackDefault)
      {
         return int.TryParse(LiveText_(key, fallbackDefault.ToString()).Trim(), out int value)
            ? value
            : fallbackDefault;
      }

      /// <summary>
      /// Whether a write-only secret is effectively set: typed into the editor, or
      /// already stored in the INI (a blank box keeps the stored value).
      /// </summary>
      private bool SecretConfigured_(string key)
      {
         if (FindSetting_(key) is SecretSetting secret)
            return secret.IsConfigured(store_);

         return !string.IsNullOrEmpty(store_.Read(key, "").Trim());
      }

      /// <summary>
      /// The service account line: what Windows is running the service as, against
      /// what the INI asks for.
      ///
      /// This is the "show the state, not the switch" rule applied to the one
      /// setting on the page whose effect is not the server's to deliver.
      /// ServiceAccountName is consumed by ServiceManager::RegisterService and
      /// never read again, so an administrator who types an account here, saves,
      /// and restarts the service gets no change and no error - the service simply
      /// keeps logging on as whoever it logged on as before. Only the Service
      /// Control Manager knows the truth, so the truth is what is reported, in
      /// every case including the ones where it cannot be determined.
      /// </summary>
      private WarningState ComputeServiceAccountState_()
      {
         // Reading the local SCM only describes this machine, and the settings
         // being edited belong to whichever host the session is connected to.
         if (!ServerSession.IsLocalSession)
         {
            return new WarningState
            {
               Level = StatusLevel.Information,
               Text = "The Control Panel is connected to another host, so the account ITS service runs as cannot be read "
                      + "from here. Open the Control Panel on the server itself to see it."
            };
         }

         WindowsServiceInfo service = serviceInfo_ ??= WindowsServiceInfo.Query();
         string configured = LiveText_("ServiceAccountName", "").Trim();

         if (!service.Exists)
         {
            return new WarningState
            {
               Level = StatusLevel.Information,
               Text = service.Error != null
                  ? "The Service Control Manager could not be queried, so the account the service runs as is unknown: "
                    + service.Error
                  : "Windows does not report an hMailServer service on this machine, so there is nothing for this "
                    + "setting to apply to yet. It is read when the service is registered."
            };
         }

         string running = WindowsServiceInfo.DescribeAccount(service.StartName);
         string register = "\"" + WindowsServiceInfo.ExecutableFrom(service.PathName) + "\" /Register";
         string howToApply = "To apply it, open an elevated Command Prompt and run:  " + register
                             + "   - then restart the hMailServer service. Registering an already-registered service "
                             + "reconfigures it in place; it does not create a second one and it does not touch your mail.";

         if (configured.Length == 0)
         {
            // Nothing requested. Whether that is fine depends entirely on what the
            // service is already running as, so the answer is about the SCM value.
            //
            // Note what is NOT said here: that an empty value means LocalSystem. It
            // means that only on the CreateService path, and this branch is only
            // reached when the service already exists - so ReconfigureService_ is
            // what would run, and there an empty value means "leave the account
            // alone". Saying "empty = LocalSystem" would invite somebody to clear
            // the box to undo a least-privilege account and get no change at all.
            bool localSystem = string.Equals(
               WindowsServiceInfo.Canonical(service.StartName, Environment.MachineName), "localsystem",
               StringComparison.OrdinalIgnoreCase);

            return new WarningState
            {
               Level = localSystem ? StatusLevel.Information : StatusLevel.Good,
               Text = localSystem
                  ? "The service is running as " + running + ". That is the default and it works, but every part of "
                    + "hMailServer that faces the network runs with it. Naming an account above - NT SERVICE\\hMailServer "
                    + "needs no password - and then re-registering the service is what changes it."
                  : "The service is running as " + service.StartName + ", which is not LocalSystem, so it is already "
                    + "contained. Nothing is requested above, and on an already-registered service an empty value "
                    + "means \"leave the account as it is\" - so re-registering would not move it back to LocalSystem. "
                    + "To do that, set the box to LocalSystem explicitly."
            };
         }

         if (WindowsServiceInfo.SameAccount(configured, service.StartName, Environment.MachineName))
         {
            return new WarningState
            {
               Level = StatusLevel.Good,
               Text = "The service is running as " + service.StartName + ", which is the account requested above. "
                      + "Nothing further is needed."
            };
         }

         return new WarningState
         {
            Level = StatusLevel.Warning,
            Text = "Not applied yet. The service is running as " + running + ", while this page asks for "
                   + configured + ". This setting is read only when the service is registered, so saving it here "
                   + "changes nothing on its own - not even after a restart. " + howToApply
         };
      }

      /// <summary>
      /// Queried once per page instance. The SCM lookup goes through WMI, which is
      /// slow enough that running it on every keystroke - which is what a computed
      /// warning does - would be felt while typing.
      /// </summary>
      private WindowsServiceInfo serviceInfo_;

      /// <summary>[Directories] DataFolder, or "" when it cannot be read.</summary>
      private string DataFolder_()
         => IniDirect.ReadValue(store_.IniPath, "Directories", "DataFolder", "").Trim();

      /// <summary>
      /// Where the server looks for an issued ACME certificate: the configured
      /// output folder, else Data\ACME - the exact fallback in
      /// AcmeClient::GetCertificateDirectory. "" when neither can be determined.
      /// </summary>
      private string AcmeCertificateFolder_()
      {
         string configured = store_.Read("AcmeCertificateDirectory", "").Trim();
         if (configured.Length > 0)
            return configured;

         string dataFolder = DataFolder_();
         return dataFolder.Length > 0 ? dataFolder + "\\ACME" : "";
      }

      /// <summary>True when both halves of the issued ACME pair exist on disk.</summary>
      private bool AcmeCertificateExists_()
      {
         string folder = AcmeCertificateFolder_();
         return folder.Length > 0
            && System.IO.File.Exists(folder + "\\fullchain.pem")
            && System.IO.File.Exists(folder + "\\privkey.pem");
      }

      /// <summary>
      /// The state of the certificate ACME has actually issued.
      ///
      /// Every other thing on the ACME card is a request - a switch, an e-mail
      /// address, a list of host names - and none of them says whether an issued
      /// certificate exists, when it expires, or whether the renewal that is meant
      /// to be automatic has in fact happened. An expired automatic certificate is
      /// silent by construction: nothing on the page changes, and the first symptom
      /// is clients refusing to connect. So the switch is joined by the one fact
      /// that answers the question, read from the file on disk.
      /// </summary>
      private WarningState ComputeAcmeCertificateState_()
      {
         if (!LiveBool_("AcmeEnabled", false))
            return null;

         if (!ServerSession.IsLocalSession)
         {
            return new WarningState
            {
               Level = StatusLevel.Information,
               Text = "The issued certificate is a file on the server's disk, and the Control Panel is connected to "
                      + "another host, so its expiry cannot be read from here."
            };
         }

         string folder = AcmeCertificateFolder_();

         if (folder.Length == 0)
         {
            return new WarningState
            {
               Level = StatusLevel.Information,
               Text = "Where the issued certificate would be written could not be determined, because neither the "
                      + "output folder above nor [Directories] DataFolder in hMailServer.INI could be read."
            };
         }

         if (!AcmeCertificateExists_())
         {
            return new WarningState
            {
               Level = StatusLevel.Warning,
               Text = "ACME is switched on but no certificate has been issued yet - fullchain.pem and privkey.pem "
                      + "are not both in " + folder + ". Issue happens on the server's own schedule, so this is "
                      + "normal for a few minutes after enabling it; if it persists, the CA could not reach this "
                      + "server, and the External setup page checks exactly that."
            };
         }

         // Cached against the folder it was read for. A computed warning re-runs on
         // every keystroke in every editor on the page, and Inspect opens and parses
         // two PEM files - doing that per character typed into the contact address
         // would be felt. Keying it on the folder means a change to the output
         // folder still re-reads, which is the only edit on this card that can
         // change the answer.
         if (acmeHealth_ == null || acmeHealthFolder_ != folder)
         {
            acmeHealth_ = CertificateInspector.Inspect(
               folder + "\\fullchain.pem", folder + "\\privkey.pem", filesReadableHere: true);
            acmeHealthFolder_ = folder;
         }

         CertificateHealth health = acmeHealth_;

         if (health.ExpiresOn == null || health.DaysRemaining == null)
         {
            return new WarningState
            {
               Level = StatusLevel.Information,
               Text = "A certificate has been issued into " + folder + ", but its expiry could not be read: "
                      + (health.CertificateFile?.Detail ?? "the file could not be parsed as a certificate.")
            };
         }

         int days = health.DaysRemaining.Value;
         string expires = health.ExpiresOn.Value.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

         if (days < 0)
         {
            return new WarningState
            {
               Level = StatusLevel.Critical,
               Text = "The issued certificate EXPIRED on " + expires + ", " + (-days) + " days ago, and renewal has "
                      + "not replaced it. Clients are refusing this server's TLS. Check the error log for the last "
                      + "renewal attempt: the usual cause is that the CA can no longer reach this server on the "
                      + "http-01 challenge port."
            };
         }

         if (days <= CertificateInspector.ExpiryWarningDays)
         {
            return new WarningState
            {
               Level = StatusLevel.Warning,
               Text = "The issued certificate expires on " + expires + ", in " + days + " days. The renewal task "
                      + "runs inside this window, so this is only a problem if the number stops falling - if it does, "
                      + "renewal is failing and the error log says why."
            };
         }

         return new WarningState
         {
            Level = StatusLevel.Good,
            Text = "A certificate is issued and valid until " + expires + ", " + days + " days from now. Renewal "
                   + "starts automatically inside the last " + CertificateInspector.ExpiryWarningDays + " days."
         };
      }

      /// <summary>The inspected ACME pair, and the folder it was inspected in.</summary>
      private CertificateHealth acmeHealth_;
      private string acmeHealthFolder_;

      /// <summary>
      /// Whether the ManageSieve listener will actually offer STARTTLS.
      ///
      /// It has no certificate setting of its own: ManageSieveServer::
      /// FindTlsCertificate_ borrows one from a TLS-capable IMAP, POP3 or SMTP
      /// port, in that order, and when there is none it starts anyway and offers
      /// plain text only, saying so once in the application log. So an
      /// administrator can configure the port, save, restart, connect - and hand
      /// their mailbox password across the network in the clear, with nothing
      /// anywhere in the GUI having suggested otherwise.
      /// </summary>
      private WarningState ComputeManageSieveTlsState_()
      {
         int port = LiveInt_("ManageSieveServerPort", 0);
         if (port <= 0)
            return null;

         string bind = LiveText_("ManageSieveServerBindAddress", "127.0.0.1").Trim();
         bool loopbackOnly = bind.StartsWith("127.") || bind == "::1";

         // Read once per page instance, including when the answer is "could not
         // tell": ??= alone would retry two COM collection walks on every keystroke
         // for as long as the session stayed broken.
         if (!tlsPortCertificateRead_)
         {
            tlsPortCertificate_ = FindManageSieveCertificateName_();
            tlsPortCertificateRead_ = true;
         }

         string certificate = tlsPortCertificate_;

         if (certificate == null)
         {
            return new WarningState
            {
               Level = StatusLevel.Information,
               Text = "Whether STARTTLS will be offered could not be determined: the TCP/IP ports and certificates "
                      + "could not be read. This listener borrows its certificate from a TLS-capable IMAP, POP3 or "
                      + "SMTP port and has none of its own."
            };
         }

         if (certificate.Length > 0)
         {
            // Deliberately "expected to be", not "will be". Everything checkable
            // from here has been checked - the certificate row exists and names
            // both files - but the listener only truly gets STARTTLS if
            // SslContextInitializer::InitServer can LOAD them at start-up, and a
            // file that has been moved, is unreadable by the service account, or
            // carries an encrypted key with no passphrase fails there. That
            // happens on the server, at start-up, and cannot be established by
            // reading configuration.
            return new WarningState
            {
               Level = StatusLevel.Good,
               Text = "STARTTLS is expected to be offered, using the certificate '" + certificate + "' borrowed from "
                      + "a TLS-capable mailbox port - this listener has no certificate setting of its own, by design: "
                      + "the client editing Sieve filters is the client reading the mailbox, so the certificate it "
                      + "already trusts is the right one to present. Whether the files actually load is decided when "
                      + "the service starts; if they do not, the listener runs in plain text and says so in the "
                      + "application log, and the SSL certificates page checks the files themselves."
            };
         }

         string missing = "No TLS-capable IMAP, POP3 or SMTP port has a certificate assigned, so this listener will "
                          + "offer plain text only - ManageSieve authenticates with the mailbox password, which would "
                          + "then cross the network in the clear. ";

         return new WarningState
         {
            Level = loopbackOnly ? StatusLevel.Information : StatusLevel.Warning,
            Text = loopbackOnly
               ? missing + "It is bound to " + bind + ", which keeps it on this machine - that is the supported way "
                 + "to run it without TLS. Assign a certificate to a mailbox port to offer STARTTLS."
               : missing + "It is bound to " + bind + ", so those passwords cross the network. Either assign a "
                 + "certificate to a TLS-capable mailbox port on the TCP/IP ports page, bind this listener to "
                 + "127.0.0.1, or put it behind a TLS terminator."
         };
      }

      /// <summary>
      /// The name of the certificate ManageSieve would borrow: "" when no
      /// TLS-capable mailbox port has one, or null when the question could not be
      /// asked. Cached per page instance - it is two COM collection walks, and a
      /// computed warning runs on every keystroke.
      ///
      /// The preference order is IMAP, then POP3, then SMTP, matching
      /// ManageSieveServer::FindTlsCertificate_. Reporting a different one would be
      /// worse than reporting none: an administrator would check the wrong
      /// certificate's expiry and host names.
      /// </summary>
      private string tlsPortCertificate_;
      private bool tlsPortCertificateRead_;

      private string FindManageSieveCertificateName_()
      {
         // Usable, not merely referenced. FindTlsCertificate_ skips a port whose
         // SSLCertificateID resolves to no certificate row, and skips one whose
         // certificate has an empty certificate or private-key path - in both cases
         // it KEEPS LOOKING rather than giving up.
         //
         // The first of those is reachable in one step: deleting a certificate
         // issues only "delete from hm_sslcertificates" and never clears
         // portsslcertificateid on the ports referencing it, so a dangling id is
         // the ordinary result of removing a certificate that was in use. Matching
         // on id alone reported "STARTTLS will be offered, using the certificate
         // '#7'" for a certificate that no longer exists, while the server was
         // logging that it had none and running the listener in plain text - with
         // ManageSieve carrying mailbox passwords.
         var usableCertificateNames = new Dictionary<int, string>();

         dynamic certificates = null;
         dynamic ports = null;

         try
         {
            certificates = ServerSession.Current.Application.Settings.SSLCertificates;
            int certificateCount = (int) certificates.Count;
            for (int i = 0; i < certificateCount; i++)
            {
               dynamic certificate = certificates.Item[i];
               try
               {
                  string certificateFile = ((string) certificate.CertificateFile ?? "").Trim();
                  string privateKeyFile = ((string) certificate.PrivateKeyFile ?? "").Trim();

                  if (certificateFile.Length == 0 || privateKeyFile.Length == 0)
                     continue;

                  usableCertificateNames[(int) certificate.ID] = (string) certificate.Name ?? "";
               }
               finally
               {
                  ServerSession.Release((object) certificate);
               }
            }

            // Per protocol, the FIRST port the server would accept - which is not
            // the first port of that protocol. The server walks the ports in order
            // and skips the unusable ones, so keeping the first referenced id and
            // then finding it unusable would name a different certificate from the
            // one actually borrowed.
            var certificateIdByProtocol = new Dictionary<int, int>();

            ports = ServerSession.Current.Application.Settings.TCPIPPorts;
            int portCount = (int) ports.Count;
            for (int i = 0; i < portCount; i++)
            {
               dynamic port = ports.Item[i];
               try
               {
                  int security = (int) port.ConnectionSecurity;
                  int certificateId = (int) port.SSLCertificateID;

                  // Only a port that will actually perform a handshake has a
                  // certificate to lend: security 0 is plaintext.
                  if (security <= 0 || certificateId <= 0)
                     continue;

                  if (!usableCertificateNames.ContainsKey(certificateId))
                     continue;

                  int protocol = (int) port.Protocol;
                  if (!certificateIdByProtocol.ContainsKey(protocol))
                     certificateIdByProtocol[protocol] = certificateId;
               }
               finally
               {
                  ServerSession.Release((object) port);
               }
            }

            foreach (int protocol in new[] { ServerSession.SessionImap, ServerSession.SessionPop3, ServerSession.SessionSmtp })
            {
               if (!certificateIdByProtocol.TryGetValue(protocol, out int certificateId))
                  continue;

               string name = usableCertificateNames[certificateId];
               return name.Length > 0 ? name : "#" + certificateId;
            }
         }
         catch (Exception)
         {
            return null;
         }
         finally
         {
            ServerSession.Release((object) ports);
            ServerSession.Release((object) certificates);
         }

         return "";
      }

      /// <summary>
      /// What an OAuth2 token actually has to satisfy to be accepted here.
      ///
      /// The first version of this method got the issuer and audience exactly
      /// backwards, and in the dangerous direction. It treated a blank
      /// OAuth2Issuer or OAuth2Audience as a fatal gap and told the administrator
      /// that "no client can log on with a token". OAuth2TokenValidator.cpp does
      /// the opposite: both checks sit behind `if (!config.issuer.IsEmpty())` and
      /// `if (!config.audience.IsEmpty())`, so a blank value means "do not check
      /// this at all". Blank is also the shipped default.
      ///
      /// So the configuration that warning fired on is not inert - it is
      /// PERMISSIVE. With both blank, any correctly-signed, unexpired token
      /// carrying the username claim is accepted, including one the identity
      /// provider minted for a completely different application. Describing that
      /// as a lockout hid the real risk behind an invented one, and an
      /// administrator acting on it would have typed a guess into a working
      /// configuration - where any value that does not exactly equal the token's
      /// own iss (or appear in its aud) then rejects every token there is.
      ///
      /// What genuinely stops every token, verified against the same file: no key
      /// material for the algorithms allowed (signature verification cannot run),
      /// and no username claim (the server has nothing to resolve to a mailbox).
      /// Those are the only two treated as blocking.
      /// </summary>
      private WarningState ComputeOAuth2State_()
      {
         if (!LiveBool_("OAuth2Enabled", false))
            return null;

         // The server implements exactly two algorithms, and nothing else. Reading
         // ValidateWithConfig: "HS256" and "RS256" have verifiers; "ES256" is
         // refused by name with "not supported by this server; use RS256 or HS256"
         // because JWS carries an ECDSA signature as a raw R||S pair while OpenSSL
         // wants X9.62 DER; and every other value falls into the final else and is
         // refused as unsupported.
         //
         // So a family test - "does the list contain RS" - is not good enough:
         // RS512 and PS256 pass it and are refused by the server, and the page
         // would have called that configuration complete. The list is matched
         // against the two names that exist.
         string algorithmSetting = LiveText_("OAuth2AllowedAlgorithms", "RS256").Trim();

         var named = algorithmSetting
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim().ToUpperInvariant())
            .Where(a => a.Length > 0)
            .ToList();

         bool allowsRs256 = named.Contains("RS256");
         bool allowsHs256 = named.Contains("HS256");
         List<string> unusable = named.Where(a => a != "RS256" && a != "HS256").ToList();

         var blocking = new List<string>();

         if (allowsRs256 && LiveText_("OAuth2PublicKeyFile", "").Trim().Length == 0)
            blocking.Add("the issuer's public key file, which RS256 tokens are verified against");

         if (allowsHs256 && !SecretConfigured_("OAuth2HmacSecret"))
            blocking.Add("the shared HMAC secret, which HS256 tokens are verified against");

         if (!allowsRs256 && !allowsHs256)
         {
            blocking.Add("any algorithm this server can verify - it implements RS256 and HS256 only, and \""
                         + algorithmSetting + "\" names neither, so every token is refused whatever key material "
                         + "is installed");
         }

         if (blocking.Count > 0)
         {
            return new WarningState
            {
               Level = StatusLevel.Warning,
               Text = "OAuth2 is switched on but no client can log on with a token, because it is missing "
                      + string.Join("; ", blocking)
                      + ". Signature verification cannot run at all, so every token is rejected and the log records "
                      + "only a failed logon - which reads as \"the password is wrong\" rather than as a "
                      + "configuration gap."
            };
         }

         // A list that mixes a usable algorithm with unusable ones is not fatal -
         // tokens signed with the usable one still work - but a client the
         // identity provider configured for one of the others fails with nothing
         // on this page to explain why, so it is named.
         if (unusable.Count > 0)
         {
            return new WarningState
            {
               Level = StatusLevel.Warning,
               Text = "Tokens signed with " + string.Join(" or ", unusable) + " are refused: this server implements "
                      + "RS256 and HS256 only. " + (unusable.Contains("ES256")
                         ? "ES256 in particular is rejected by name - JWS carries an ECDSA signature as a raw R||S "
                           + "pair and OpenSSL expects DER, so it never verified and the server refuses it rather "
                           + "than reporting a signature failure. "
                         : "")
                      + "Remove what the server cannot verify from the list, so a client offering it is refused for "
                      + "a reason the log makes plain rather than appearing to be allowed."
            };
         }

         // Signature verification works. What is left is how much a valid signature
         // is allowed to prove, and that is where the blanks matter.
         bool noIssuer = LiveText_("OAuth2Issuer", "").Trim().Length == 0;
         bool noAudience = LiveText_("OAuth2Audience", "").Trim().Length == 0;

         if (noIssuer || noAudience)
         {
            string unchecked_ = noIssuer && noAudience
               ? "neither the issuer (iss) nor the audience (aud) is checked"
               : noIssuer ? "the issuer (iss) is not checked" : "the audience (aud) is not checked";

            return new WarningState
            {
               Level = StatusLevel.Warning,
               Text = "Tokens are accepted, but " + unchecked_ + ": the server applies each of those only when you "
                      + "have set it, and blank is the default. That is wider than it looks. Any token your identity "
                      + "provider signs with this key is accepted - including one it issued to a different "
                      + "application entirely, which is enough to log in as whichever mailbox that token names. "
                      + "Set both to the exact values your provider puts in its tokens."
                      + (LiveBool_("OAuth2RequireTLS", true)
                         ? ""
                         : " Tokens are also accepted over unencrypted connections, so a recorded one can be "
                           + "replayed until it expires.")
            };
         }

         if (!LiveBool_("OAuth2RequireTLS", true))
         {
            return new WarningState
            {
               Level = StatusLevel.Warning,
               Text = "The configuration is complete, but tokens are accepted over connections that are not "
                      + "encrypted. A bearer token is a credential in plain text: anyone who records the connection "
                      + "can replay it until it expires. Turn TLS back on unless something in front of this server "
                      + "is already terminating it."
            };
         }

         return new WarningState
         {
            Level = StatusLevel.Good,
            Text = "Key material is present for the algorithms allowed, and both the issuer and the audience are "
                   + "checked, so a token has to have been minted by your provider for this server specifically. "
                   + "The mailbox it names still has to exist as a local account - a valid token for an address "
                   + "this server does not host is refused."
         };
      }

      /// <summary>
      /// Strict dotted-quad parse, mirroring the inet_pton call in
      /// MetricsServer::Start - IPAddress.TryParse would accept "127.1" and IPv6,
      /// both of which the listener itself refuses.
      /// </summary>
      private static bool TryParseIpv4_(string text, out byte firstOctet)
      {
         firstOctet = 0;
         if (string.IsNullOrWhiteSpace(text))
            return false;

         string[] parts = text.Trim().Split('.');
         if (parts.Length != 4)
            return false;

         var octets = new byte[4];
         for (int i = 0; i < 4; i++)
         {
            if (parts[i].Length == 0 || parts[i].Length > 3 || !parts[i].All(char.IsDigit))
               return false;
            if (!byte.TryParse(parts[i], out octets[i]))
               return false;
         }

         firstOctet = octets[0];
         return true;
      }

      public FeatureSettingsView(Section section)
      {
         InitializeComponent();
         section_ = section;
         BuildDefinition();
         BuildUi();
      }

      public void OnEnter()
      {
         // The page instance is cached by MainWindow, so re-read the INI from disk
         // on every navigation. This keeps the editors in sync with values changed
         // elsewhere (a prior save, another tool, or a hand edit) instead of showing
         // stale data captured when the page was first constructed.
         //
         // The same reasoning applies to what the computed warnings read from
         // outside the INI, which is cached per page instance so that it is not
         // re-read on every keystroke: the certificate a mailbox port lends to
         // ManageSieve, the account the service runs as, and the ACME pair on disk
         // can all have changed since this page was last open, and a warning
         // reasoning about a stale one is worse than no warning at all.
         ForgetExternalState_();

         BuildDefinition();
         BuildUi();
      }

      public void OnLeave()
      {
      }

      private void BuildDefinition()
      {
         cards_ = new List<CardDef>();

         switch (section_)
         {
            case Section.Security:
               TitleText.Text = "Transport security";
               SubtitleText.Text = "Outbound mail authentication and encryption policies (hMailServer.INI). " +
                                   "Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "DANE & DNSSEC",
                  Blurb = "Validates recipient TLSA records with in-process DNSSEC and blocks delivery over forged chains (RFC 7672).",
                  Settings =
                  {
                     new BoolSetting { Key = "DaneEnforcementEnabled", Default = true, Label = "Honor recipient DANE/TLSA records when sending" },
                     new BoolSetting { Key = "DnssecValidationEnabled", Default = true, Label = "Validate DNSSEC for DANE and SPF/DKIM/DMARC lookups" },
                     new TextSetting { Key = "DnssecTrustAnchors", Label = "Trust anchor override (tag alg digesttype hex; ...)", Placeholder = "Leave empty for the built-in root anchors" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "MTA-STS",
                  Blurb = "Discovers and enforces recipient MTA-STS policies before delivering over TLS (RFC 8461). " +
                          "To publish a policy for your own domains, see the Web services & autoconfiguration page.",
                  Settings =
                  {
                     new BoolSetting { Key = "MtaStsEnabled", Default = true, Label = "Honor recipient MTA-STS policies when sending" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "ARC sealing",
                  // The prerequisite sentence is stated rather than checked here:
                  // whether a hosted domain has a DKIM selector and key file lives
                  // in the database, behind the COM API, and this page works from
                  // hMailServer.INI alone - it must keep telling the truth when no
                  // COM session exists. The External setup page holds the live
                  // check and walks the actual domains.
                  Blurb = "Adds ARC seals to forwarded mail so downstream servers can trust original authentication results (RFC 8617). " +
                          "Sealing borrows the forwarding domain's DKIM selector and private key, so it needs at least one hosted " +
                          "domain with DKIM signing configured - without that the switch reads enabled and seals nothing, with only " +
                          "a debug log line to say so. The External setup page checks your domains for this.",
                  Settings =
                  {
                     new BoolSetting { Key = "ArcSealingEnabled", Default = false, Label = "Seal forwarded messages with the domain's DKIM key" }
                  }
               });
               cards_.Add(new CardDef
               {
                  // An information-only card, deliberately without editors. The two
                  // values it describes live in the hm_settings database table (the
                  // anti-spam PropertySet, seeded at database version 6010), which
                  // nothing in this Control Panel can reach: IniFeatureStore reads
                  // and writes only hMailServer.INI, and the COM AntiSpam interface
                  // has no accessor for them, so the COM-path pattern the other
                  // anti-spam settings use cannot reach them either. An editor here
                  // that wrote INI keys of the same names would look configured and
                  // change nothing - the exact defect this page's blurbs exist to
                  // prevent - so until a real accessor exists, the card only tells
                  // the truth about where the switch is and what it needs.
                  Title = "ARC inbound filtering",
                  // These two are database settings rather than ini values, so they are not
                  // editable on this page - which is for hMailServer.INI. They ARE editable,
                  // on the Anti-spam page, and this card points there. It used to say they
                  // could not be edited at all, which stopped being true the moment the COM
                  // properties landed; a card that describes a limitation the product no
                  // longer has is the same defect as one that claims a feature it lacks.
                  Blurb = "The counterpart to ARC sealing: on inbound mail, a valid ARC chain from a trusted sealer can " +
                          "offset the DMARC failure score for a message whose DMARC pass was destroyed by forwarding " +
                          "(RFC 8617). It is configured on the Anti-spam page, under sender authentication, because it " +
                          "is stored with the other anti-spam settings rather than in hMailServer.INI. With the " +
                          "trusted-sealer list empty the feature does nothing at all, by design: anyone can fabricate " +
                          "an entire ARC chain and seal it with keys published in their own DNS, and it will validate " +
                          "perfectly, so a passing chain proves nothing unless you already trust the sealer. The list " +
                          "is not an option of the feature - it is the feature. The offset never exceeds the DMARC " +
                          "failure score, and applies only while DMARC scoring is enabled on the Anti-spam page."
               });
               cards_.Add(new CardDef
               {
                  Title = "DKIM signature timestamps",
                  Blurb = "When a DKIM signature was made, and when it stops being one a verifier should honour (RFC 6376 3.5).",
                  Settings =
                  {
                     new TextSetting
                     {
                        Key = "DKIMSignatureValiditySeconds",
                        Default = "0",
                        Placeholder = "604800",
                        Label = "Validity window in seconds for signatures we produce (0 = no expiry)",
                        Blurb = "0 emits no expiry at all, which is the safe default: an expiry is a promise about mail " +
                                "already in flight, and a window shorter than the delay a retry, a greylist or a mailing " +
                                "list adds costs the message its DKIM pass and its DMARC alignment at the far end. " +
                                "604800 is a week, the usual choice for a sender who wants one. A signing timestamp is " +
                                "always sent regardless of this value."
                     },
                     new BoolSetting
                     {
                        Key = "DKIMEnforceSignatureExpiry",
                        Default = true,
                        Label = "Refuse signatures on incoming mail whose expiry has passed",
                        Blurb = "The expiry sits inside the bytes the signature covers, so it is the sending domain's own " +
                                "instruction rather than something a third party can add. Turning this off means a " +
                                "captured signed message can be replayed indefinitely."
                     },
                     new TextSetting
                     {
                        Key = "DKIMExpiryClockSkewSeconds",
                        Default = "300",
                        Placeholder = "300",
                        Label = "Clock-drift tolerance in seconds when checking an expiry",
                        Blurb = "Allows for this server's clock differing from the signer's. Without it, a clock running " +
                                "a few minutes fast turns other people's valid mail into DKIM failures."
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  // Defaults verified against IniFileSettings.cpp (empty = off) and
                  // DKIM::InitializeOversigning_ for the length cap, the invalid-name
                  // handling and the automatic From.
                  Title = "DKIM oversigning",
                  Blurb = "Oversigning (RFC 6376 5.4) lists a header field name in the signature's h= tag once more often " +
                          "than the field occurs, which makes ADDING another one - a second From:, an injected Subject: - " +
                          "break the signature. Off by default: oversigning a field that a mailing list or forwarder " +
                          "legitimately adds costs those messages their DKIM pass.",
                  Settings =
                  {
                     new TextSetting
                     {
                        Key = "DkimOversignHeaders",
                        Label = "Header fields to oversign in outbound DKIM signatures (comma separated, empty = off)",
                        Placeholder = "From, Subject, Reply-To",
                        Blurb = "From is included automatically whenever this list is non-empty - a prepended second From: " +
                                "is the attack oversigning exists for. Names must be printable ASCII without a colon; an " +
                                "invalid name is dropped with an error log entry, and a value longer than 256 characters " +
                                "is ignored entirely, also with an error entry, because h= has to fit on one unfolded line."
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  // No mention of SMTP AUTH verdicts here on purpose: the server's
                  // results carrier has an auth= slot, but nothing feeds it yet, so
                  // advertising it would be a capability claim with nothing behind it.
                  Title = "Authentication results on inbound mail",
                  Blurb = "Records the verdicts this server itself reached about each inbound message - SPF, DKIM and " +
                          "DMARC - as trace headers on the delivered message, for downstream filters and diagnostics " +
                          "(RFC 8601, RFC 7208).",
                  Settings =
                  {
                     new BoolSetting
                     {
                        Key = "AuthenticationResultsEnabled",
                        Default = false,
                        Label = "Write an Authentication-Results header on inbound mail",
                        Blurb = "Each accepted inbound message gets one Authentication-Results header (RFC 8601) carrying " +
                                "the SPF, DKIM and DMARC verdicts this server reached about it. Only checks that actually " +
                                "ran are reported - which checks run is configured on the Anti-spam page - and a message " +
                                "on which no check ran gets no header. An arriving message that already carries an " +
                                "Authentication-Results header claiming this server's own identity has that header " +
                                "removed first, so a sender cannot have a verdict written in this server's name believed " +
                                "downstream (RFC 8601 section 5)."
                     },
                     new BoolSetting
                     {
                        Key = "ReceivedSpfHeaderEnabled",
                        Default = false,
                        Label = "Write a Received-SPF header on inbound mail",
                        Blurb = "Records the SPF verdict for each accepted inbound message as a Received-SPF header " +
                                "(RFC 7208 section 9.1). The header is only written when the SPF check actually ran, " +
                                "so 'Check SPF' must be enabled on the Anti-spam page for it to appear."
                     },
                     new TextSetting
                     {
                        Key = "AuthenticationResultsIdentity",
                        Label = "Identity the results are written under (empty = this computer's name)",
                        Placeholder = "mail.yourdomain.com",
                        Blurb = "The authserv-id: the first token of every Authentication-Results header this server " +
                                "writes, always lower-cased, and the name a downstream filter checks before trusting " +
                                "the verdicts. The same name decides which arriving Authentication-Results headers are " +
                                "treated as forged: one claiming this identity is removed, while one naming any other " +
                                "identity is left completely untouched."
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "TLS reporting (TLS-RPT)",
                  Blurb = "Sends daily aggregate reports about TLS connection failures to recipient domains (RFC 8460).",
                  Settings =
                  {
                     new TextSetting { Key = "TlsRptFromAddress", Label = "Report sender address (empty = disabled)", Placeholder = "tlsrpt@yourdomain.com" },
                     new TextSetting { Key = "TlsRptOrganizationName", Default = "hMailServer", Label = "Organization name in reports" }
                  }
               });
               // Moved here from the catch-all INI page. This is the same subject as
               // the rest of this page - whether mail this server sends still passes
               // the recipient's authentication checks - and forwarding is the single
               // most common way a domain's SPF record starts failing for mail it
               // genuinely sent. The plain rewrite fallback comes with it, because
               // SRS silently replaces it and neither one can be judged alone.
               cards_.Add(new CardDef
               {
                  Title = "Forwarded mail & bounce protection (SRS / BATV)",
                  Blurb = "Forwarding a message keeps the original envelope sender, so the next hop checks SPF for a " +
                          "domain that never authorised this server and the message fails. These are the two answers. " +
                          "SRS rewrites the envelope sender into one this server can vouch for and can undo on the way " +
                          "back, so bounces still reach the original sender; BATV tags the envelope sender of outbound " +
                          "mail so a forged bounce - one for a message this server never sent - can be told apart from " +
                          "a real one. Both use a server-wide secret and do nothing at all until one is set. " +
                          "Changes take effect after a service restart.",
                  Settings =
                  {
                     new BoolSetting
                     {
                        Key = "RewriteEnvelopeFromWhenForwarding",
                        Default = false,
                        Label = "Rewrite the envelope sender when forwarding (the simple fallback, no secret needed)",
                        Blurb = "Replaces the envelope sender with the forwarding account's own address. That makes SPF " +
                                "pass at the next hop, at the cost of the original sender's address: a bounce comes back " +
                                "to the forwarding mailbox instead of to whoever wrote the message. SRS below does the " +
                                "same job without losing the return path, and while SRS is enabled this setting is not " +
                                "consulted at all."
                     },
                     new BoolSetting { Key = "SRSEnabled", Default = false, Label = "Enable Sender Rewriting Scheme (SRS) on forwarded mail" },
                     new SecretSetting { Key = "SRSSecret", OfferGenerate = true, Label = "SRS signing secret", Hint = "A random server-wide secret" },
                     new BoolSetting { Key = "BATVEnabled", Default = false, Label = "Tag outbound envelope senders with BATV and validate returning bounces" },
                     new SecretSetting { Key = "BATVSecret", OfferGenerate = true, Label = "BATV signing secret", Hint = "A random server-wide secret" }
                  },
                  Warnings =
                  {
                     // Verified in SMTPForwarding.cpp: SRS::Forward returns nothing
                     // when the secret is empty, and the RewriteEnvelopeFrom...
                     // fallback sits in an "else if" behind SRSEnabled - so SRS-on
                     // with no secret rewrites nothing AND suppresses the fallback.
                     new WarningDef
                     {
                        Aid = "SrsSecretMissingWarning",
                        Compute = () =>
                        {
                           if (!LiveBool_("SRSEnabled", false) || SecretConfigured_("SRSSecret"))
                              return null;
                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "SRS is switched on but no secret is set, so no envelope sender is rewritten - and " +
                                     "while SRS is on, the plain rewrite fallback at the top of this card is skipped as " +
                                     "well. This is strictly worse than switching SRS off. Generate or enter a secret."
                           };
                        }
                     },
                     new WarningDef
                     {
                        Aid = "BatvSecretMissingWarning",
                        Compute = () =>
                        {
                           if (!LiveBool_("BATVEnabled", false) || SecretConfigured_("BATVSecret"))
                              return null;
                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "BATV is switched on but no secret is set, so outbound senders are not tagged and " +
                                     "returning bounces are not validated - the switch reads enabled while it does " +
                                     "nothing. Generate or enter a secret."
                           };
                        }
                     }
                  }
               });
               break;

            case Section.Automation:
               TitleText.Text = "Automatic certificates (ACME)";
               SubtitleText.Text = "Built-in Let's Encrypt integration: certificates are issued, renewed, " +
                                   "assigned to TLS ports and hot-reloaded automatically.";
               cards_.Add(new CardDef
               {
                  Title = "ACME (Let's Encrypt)",
                  Blurb = "Issued certificates are stored in Data\\ACME and assigned to TLS ports without a restart. " +
                          "Key reuse keeps published DANE TLSA records valid across renewals.",
                  Settings =
                  {
                     new BoolSetting { Key = "AcmeEnabled", Default = false, Label = "Issue and renew certificates automatically" },
                     new TextSetting { Key = "AcmeContactEmail", Label = "Contact e-mail (CA expiry notices)", Placeholder = "admin@yourdomain.com" },
                     new TextSetting { Key = "AcmeDomains", Label = "Host names for the certificate (comma separated)", Placeholder = "mail.yourdomain.com, mta-sts.yourdomain.com" },
                     new TextSetting { Key = "AcmeDirectoryUrl", Default = "https://acme-v02.api.letsencrypt.org/directory", Label = "ACME directory URL" },
                     new TextSetting { Key = "AcmeHttpPort", Default = "80", Label = "Port for http-01 challenges" },
                     new PathSetting { Key = "AcmeCertificateDirectory", PickFolder = true, Label = "Certificate output folder (empty = Data\\ACME)", Placeholder = "Falls back to Data\\ACME" },
                     new BoolSetting { Key = "AcmeReuseKey", Default = true, Label = "Reuse the private key across renewals (keeps DANE TLSA records valid)" }
                  },
                  Warnings =
                  {
                     // The whole point of automatic certificates is that nobody has
                     // to think about them, which is also why a renewal that stops
                     // working is silent: every control on this card still reads
                     // exactly as it did. The expiry of the file on disk is the one
                     // fact that distinguishes "working" from "was working".
                     new WarningDef
                     {
                        Aid = "AcmeCertificateState",
                        Compute = ComputeAcmeCertificateState_
                     }
                  }
               });
               break;

            case Section.Integration:
               TitleText.Text = "API & monitoring";
               SubtitleText.Text = "REST administration API, Prometheus metrics and remote script management. " +
                                   "The public web services listener (autoconfiguration, MTA-STS hosting) is on the " +
                                   "Web services & autoconfiguration page; OAuth2 token authentication is on the " +
                                   "Authentication page.";
               cards_.Add(new CardDef
               {
                  Title = "REST administration API + Web Control Deck",
                  Blurb = "JSON API under /api/v1 plus the browser-based Control Deck at the listener root. " +
                          "Authenticated with the administrator password, or with a scoped API key - a key can be " +
                          "read-only, limited to named domains and source addresses, given an expiry and revoked on " +
                          "its own, none of which the administrator password can. Keys are managed on the REST API " +
                          "keys page. TLS is required unless the listener is bound to 127.0.0.1.",
                  Settings =
                  {
                     new ElsewhereSetting("apikeys", "Creating and revoking API keys"),
                     new TextSetting { Key = "RestApiPort", Default = "0", Label = "Port (0 = disabled)", Placeholder = "8045" },
                     new TextSetting { Key = "RestApiBindAddress", Default = "127.0.0.1", Label = "Bind address" },
                     new PathSetting { Key = "RestApiCertificateFile", FileFilter = "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new PathSetting { Key = "RestApiPrivateKeyFile", FileFilter = "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*", Label = "TLS private key file (PEM, optional)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Monitoring",
                  // This blurb used to advertise an OpenTelemetry metrics export
                  // alongside the trace export. Only the trace signal exists:
                  // OtelTracer hard-codes the /v1/traces path and there is no metric
                  // or log record builder, so a collector configured here receives
                  // spans and nothing else. An administrator who read that blurb and
                  // pointed their collector at the server expecting OTLP metrics got
                  // none, and no error to explain why - the metrics that do exist are
                  // the Prometheus endpoint above, which is a different mechanism.
                  //
                  // The withdrawn wording is pinned by SettingClaimsTests, which
                  // scans this file for it; do not reintroduce it, in a comment or
                  // otherwise, without a metrics exporter behind it.
                  Blurb = "Prometheus metrics (/metrics), OpenTelemetry trace export, a slow-query log, and "
                          + "JSON-structured log output for log aggregators (on the Logging page).",
                  Settings =
                  {
                     new TextSetting { Key = "MetricsServerPort", Default = "0", Label = "Metrics port (0 = disabled)", Placeholder = "9090" },
                     new TextSetting
                     {
                        Key = "MetricsServerBindAddress",
                        Default = "127.0.0.1",
                        Label = "Metrics bind address",
                        Blurb = "An IPv4 address, and the credential gate: anywhere in 127.0.0.0/8 the endpoints are open " +
                                "to this machine without authentication, exactly as before. On any other address /metrics " +
                                "answers 503 until a credential below is set - the exposition includes queue depth, " +
                                "session counts and version numbers, so it must not be network-readable unauthenticated."
                     },
                     // Access control and TLS for the exposition. All five default to
                     // empty (verified in IniFileSettings.cpp), which on a loopback
                     // bind is the old behaviour exactly: plain unauthenticated HTTP.
                     new SecretSetting
                     {
                        Key = "MetricsServerAuthToken",
                        OfferGenerate = true,
                        Label = "Bearer token for /metrics (empty = none)",
                        Hint = "A random token Prometheus will present on every scrape",
                        Blurb = "Presented as \"Authorization: Bearer ...\" - in Prometheus, the scrape config's " +
                                "bearer_token. Leading and trailing spaces are trimmed. The health probes /livez, /readyz " +
                                "and /healthz never require it, so load balancers keep working."
                     },
                     new TextSetting
                     {
                        Key = "MetricsServerAuthUsername",
                        Label = "HTTP Basic user name for /metrics (empty = Basic off)",
                        Placeholder = "metrics",
                        Blurb = "The alternative to the bearer token, for scrapers that only speak HTTP Basic. The user " +
                                "name and the password must BOTH be set: with only one of them the server logs a warning " +
                                "at startup and behaves as if neither were set."
                     },
                     new SecretSetting
                     {
                        Key = "MetricsServerAuthPassword",
                        OfferGenerate = true,
                        Label = "HTTP Basic password for /metrics",
                        Hint = "Only used together with the user name above",
                        Blurb = "The user name is trimmed of surrounding spaces; the password deliberately is not, " +
                                "because whitespace is legitimate inside a password."
                     },
                     new PathSetting
                     {
                        Key = "MetricsServerCertificateFile",
                        FileFilter = "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*",
                        Label = "TLS certificate file for the metrics listener (PEM)",
                        Placeholder = "Leave both empty for plain HTTP"
                     },
                     new PathSetting
                     {
                        Key = "MetricsServerPrivateKeyFile",
                        FileFilter = "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*",
                        Label = "TLS private key file for the metrics listener (PEM)",
                        Blurb = "Certificate and key must BOTH be set to serve HTTPS; with only one of them TLS is NOT " +
                                "enabled and the server logs it. Unlike the REST API listener there is no fall-back to " +
                                "the ACME certificate here. If TLS is configured but cannot be prepared (an unreadable " +
                                "file, a key that does not match), the health probes stay on plain HTTP and /metrics " +
                                "answers 503 rather than serving the exposition in the clear."
                     },
                     // JsonLogging moved to the Logging page, with the other log settings.
                     new TextSetting
                     {
                        Key = "OtelEndpoint",
                        Label = "OpenTelemetry OTLP endpoint for traces (empty = disabled)",
                        Placeholder = "http://localhost:4318",
                        Blurb = SettingClaims.NoteFor("OtelEndpoint")
                     },
                     new TextSetting { Key = "OtelServiceName", Default = "hmailserver", Label = "OpenTelemetry service name" },
                     new TextSetting { Key = "SlowQueryLogMilliseconds", Default = "0", Label = "Log database queries slower than N ms (0 = off)", Placeholder = "250" }
                  },
                  Warnings =
                  {
                     // Behaviour verified in MetricsServer::Start: an invalid bind
                     // address is refused outright; a non-loopback bind without a
                     // credential serves 503 on /metrics; half a Basic pair and
                     // half a TLS pair are each discarded with a log line; and a
                     // credential without TLS crosses the network in clear text.
                     new WarningDef
                     {
                        Aid = "MetricsBindAddressInvalidWarning",
                        Compute = () =>
                        {
                           if (LiveInt_("MetricsServerPort", 0) <= 0)
                              return null;
                           if (TryParseIpv4_(LiveText_("MetricsServerBindAddress", "127.0.0.1"), out _))
                              return null;
                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "The metrics bind address is not a plain IPv4 address (host names and IPv6 are not " +
                                     "accepted here), so the metrics listener will not start at all."
                           };
                        }
                     },
                     new WarningDef
                     {
                        Aid = "MetricsNeedsCredentialWarning",
                        Compute = () =>
                        {
                           if (LiveInt_("MetricsServerPort", 0) <= 0)
                              return null;
                           if (!TryParseIpv4_(LiveText_("MetricsServerBindAddress", "127.0.0.1"), out byte firstOctet))
                              return null;
                           if (firstOctet == 127)
                              return null;

                           bool tokenSet = SecretConfigured_("MetricsServerAuthToken");
                           bool basicSet = LiveText_("MetricsServerAuthUsername", "").Trim().Length > 0
                                        && SecretConfigured_("MetricsServerAuthPassword");
                           if (tokenSet || basicSet)
                              return null;

                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "The bind address is not a loopback address and no credential is set, so /metrics " +
                                     "answers 503 (Service Unavailable) to every scrape. Set a bearer token, or both " +
                                     "HTTP Basic fields, or bind to 127.0.0.1. The health probes keep answering either way."
                           };
                        }
                     },
                     new WarningDef
                     {
                        Aid = "MetricsHalfBasicWarning",
                        Compute = () =>
                        {
                           if (LiveInt_("MetricsServerPort", 0) <= 0)
                              return null;

                           bool userSet = LiveText_("MetricsServerAuthUsername", "").Trim().Length > 0;
                           bool passSet = SecretConfigured_("MetricsServerAuthPassword");
                           if (userSet == passSet)
                              return null;

                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "HTTP Basic needs both the user name and the password. Only " +
                                     (userSet ? "the user name" : "the password") +
                                     " is set, so Basic authentication is NOT enabled - the server logs this and behaves " +
                                     "as if neither were set."
                           };
                        }
                     },
                     new WarningDef
                     {
                        Aid = "MetricsHalfTlsWarning",
                        Compute = () =>
                        {
                           if (LiveInt_("MetricsServerPort", 0) <= 0)
                              return null;

                           bool certSet = LiveText_("MetricsServerCertificateFile", "").Trim().Length > 0;
                           bool keySet = LiveText_("MetricsServerPrivateKeyFile", "").Trim().Length > 0;
                           if (certSet == keySet)
                              return null;

                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "TLS for the metrics listener needs both the certificate and the private key. Only " +
                                     (certSet ? "the certificate" : "the private key") +
                                     " is set, so TLS is NOT enabled and scrapes stay plain HTTP."
                           };
                        }
                     },
                     new WarningDef
                     {
                        Aid = "MetricsClearTextCredentialWarning",
                        Compute = () =>
                        {
                           if (LiveInt_("MetricsServerPort", 0) <= 0)
                              return null;
                           if (!TryParseIpv4_(LiveText_("MetricsServerBindAddress", "127.0.0.1"), out byte firstOctet))
                              return null;
                           if (firstOctet == 127)
                              return null;

                           bool tokenSet = SecretConfigured_("MetricsServerAuthToken");
                           bool basicSet = LiveText_("MetricsServerAuthUsername", "").Trim().Length > 0
                                        && SecretConfigured_("MetricsServerAuthPassword");
                           if (!tokenSet && !basicSet)
                              return null;

                           bool tlsSet = LiveText_("MetricsServerCertificateFile", "").Trim().Length > 0
                                      && LiveText_("MetricsServerPrivateKeyFile", "").Trim().Length > 0;
                           if (tlsSet)
                              return null;

                           return new WarningState
                           {
                              Level = StatusLevel.Warning,
                              Text = "A credential is set but the listener has no TLS, so the credential crosses the " +
                                     "network in clear text on every scrape and can be replayed by anyone on the path. " +
                                     "Set the certificate and key files, or bind to 127.0.0.1. The server still starts - " +
                                     "it logs this same warning."
                           };
                        }
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "ManageSieve (RFC 5804)",
                  Blurb = "Lets mail clients upload and manage per-account Sieve filter scripts over TCP. " +
                          "Authentication is SASL PLAIN against the account database, so the mailbox password crosses " +
                          "this connection. STARTTLS is offered only when a TLS-capable mailbox port has a certificate " +
                          "to borrow - the status below says whether it will be.",
                  Settings =
                  {
                     new TextSetting { Key = "ManageSieveServerPort", Default = "0", Label = "ManageSieve port (0 = disabled)", Placeholder = "4190" },
                     new TextSetting { Key = "ManageSieveServerBindAddress", Default = "127.0.0.1", Label = "ManageSieve bind address" }
                  },
                  Warnings =
                  {
                     // This listener starts whether or not it can offer TLS, and
                     // records the difference in one application-log line at
                     // start-up. Nothing else anywhere said which of the two an
                     // administrator was about to get, and the answer decides
                     // whether mailbox passwords cross the network in the clear.
                     new WarningDef
                     {
                        Aid = "ManageSieveTlsState",
                        Compute = ComputeManageSieveTlsState_
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Operability",
                  // Log retention lives on the Logging page, next to the other
                  // logging settings, so there is exactly one editor per key.
                  Blurb = "Graceful shutdown behaviour for unattended / clustered operation. (Log retention is on the Logging page.)",
                  Settings =
                  {
                     new TextSetting { Key = "ShutdownDrainSeconds", Default = "0", Label = "On stop, wait up to N seconds for active sessions to finish (0 = stop immediately)", Placeholder = "30" }
                  }
               });
               break;

            // "Advanced INI settings" filed by storage mechanism, which is the one
            // thing an administrator never knows and never needs to. Everything on
            // it that belonged to a feature with a page of its own has gone to that
            // page; what is left is genuinely server-wide, so the page is now named
            // for that rather than for the file the values happen to live in. The
            // nav key stays "hardening" and the old titles stay as search aliases,
            // so every existing link and bookmark still lands here.
            case Section.Hardening:
               TitleText.Text = "Server limits & expert settings";
               SubtitleText.Text = "Server-wide ceilings, durability and abuse controls that belong to no single " +
                                   "protocol or feature. The defaults are safe; change these only with a specific " +
                                   "reason. Stored in hMailServer.INI, and unless a card says otherwise, changes " +
                                   "take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "Timeouts and queue bounds",
                  Blurb = "Server-wide ceilings that keep one slow operation from holding a resource forever. The defaults " +
                          "are deliberate; each one is here because there is a diagnosable situation in which it is the " +
                          "right thing to change. Note that 0 does not mean the same thing for all of them - each label says.",
                  Settings =
                  {
                     new TextSetting
                     {
                        Key = "FinalizationTimeout",
                        Default = "240",
                        Label = "Message finalization deadline (seconds; 0 = no deadline)",
                        Blurb = "How long the server will go on finalizing an accepted message before answering 451 and " +
                                "asking the sender to retry, rather than holding the connection open indefinitely."
                     },
                     new TextSetting { Key = "DNSQueryTimeout", Default = "10", Label = "DNS query timeout (seconds; 0 = no bound)" },
                     new TextSetting { Key = "ClientSessionCeiling", Default = "1800", Label = "Absolute lifetime of one client session (seconds)" },
                     new TextSetting { Key = "DBConnectionAcquireTimeout", Default = "60", Label = "Wait for a free database connection (seconds)" },
                     new TextSetting { Key = "ScriptTimeout", Default = "60", Label = "Event script execution timeout (seconds)" },
                     new TextSetting { Key = "ExternalProcessTimeout", Default = "300", Label = "External process timeout, e.g. a command-line virus scanner (seconds)" },
                     new TextSetting { Key = "AsyncQueueStallThreshold", Default = "120", Label = "Report the async work queue as stalled after (seconds)" },
                     new TextSetting { Key = "AsyncQueueReservedThreads", Default = "2", Label = "Threads reserved so the async queue cannot be starved" }
                  }
               });
               // Scanner timeouts moved to the scanner they configure: SpamAssassin
               // on the Anti-spam page and ClamAV on the Anti-virus page. The
               // resolver settings moved to Network > DNS resolver.
               cards_.Add(new CardDef
               {
                  Title = "Received headers",
                  Blurb = "Submission identity handling and the diagnostic headers added to received mail. " +
                          "(Where SMTP AUTH is offered is on the Authentication page.)",
                  Settings =
                  {
                     new TextSetting { Key = "AuthUserReplacementIP", Label = "Replace the client IP for authenticated users (empty = keep the real IP)", Placeholder = "e.g. 127.0.0.1" },
                     new BoolSetting { Key = "AddXAuthUserHeader", Default = false, Label = "Add an X-AuthUser header with the authenticated account" },
                     new BoolSetting { Key = "AddXAuthUserIP", Default = true, Label = "Include the client IP in the X-AuthUser header" },
                     new BoolSetting { Key = "AddXOriginalRcptTo", Default = false, Label = "Add an X-OriginalRcptTo header" }
                  }
               });
               cards_.Add(new CardDef
               {
                  // Verified in TCPServer.cpp: the hold applies where a session could
                  // not be created at all - a non-matching IP range or the connection
                  // limit for that range - and nowhere else. It is not an auto-ban
                  // setting, and the blurb says which page each of those is on so
                  // nobody comes here looking for one and changes this instead.
                  Title = "Refused connections",
                  Blurb = "What happens to a TCP connection this server refuses before any protocol conversation starts: " +
                          "one from an address no IP range allows, or one over that range's connection limit. Both of " +
                          "those are configured on the IP ranges page. Locking out an address that keeps failing to log " +
                          "on is a different mechanism entirely and is on the Auto-ban page.",
                  Settings =
                  {
                     new TextSetting
                     {
                        Key = "BlockedIPHoldSeconds",
                        Default = "0",
                        Label = "Hold a refused connection open before dropping it (seconds, 0 = drop immediately)",
                        Blurb = "Anti-pounding: a host that reconnects the instant it is dropped can do so thousands of " +
                                "times a minute, and holding the socket open slows it to one connection per interval " +
                                "without costing this server a thread. The connection is held by a timer, so it consumes " +
                                "nothing while it waits."
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Message store durability",
                  Blurb = "Crash-durability barrier and an optional integrity scan for the on-disk message store. " +
                          "The defaults preserve the previous behaviour; enabling fsync adds a small per-message cost.",
                  Settings =
                  {
                     new BoolSetting { Key = "MessageStoreFsync", Default = false, Label = "Flush each received message to disk before it is acknowledged (durable, slower)" },
                     new BoolSetting { Key = "MessageStoreConsistencyCheck", Default = false, Label = "Periodically cross-check message rows against files on disk (read-only; writes a report on divergence)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  // Defaults and the reload behaviour verified in RateLimiter.cpp:
                  // LoadSettings_ reads [SendingLimits]/[SendingLimitsOverrides]
                  // directly and MaybeRefreshSettings_ stats the INI every couple
                  // of seconds, so unlike everything else on this page these apply
                  // without a service restart.
                  Title = "Per-account sending limits",
                  Blurb = "Ceilings on what one authenticated account may submit over a rolling period - the brake on a " +
                          "compromised account being used to spam. Counted per account at SMTP submission: messages and " +
                          "envelope recipients separately, and a message refused by the limit gets a temporary error so " +
                          "a real client retries later. Unlike the rest of this page these live in the [SendingLimits] " +
                          "and [SendingLimitsOverrides] sections of hMailServer.INI and are re-read within a couple of " +
                          "seconds of the file changing - saving here applies them WITHOUT a service restart. Counters " +
                          "survive a restart via a state file in the data directory.",
                  Settings =
                  {
                     new SectionTextSetting
                     {
                        Key = "MaxMessagesPerAccountPerPeriod",
                        Section = "SendingLimits",
                        Default = "0",
                        Label = "Max messages per account per period (0 = no limit)",
                        Placeholder = "500"
                     },
                     new SectionTextSetting
                     {
                        Key = "MaxRecipientsPerAccountPerPeriod",
                        Section = "SendingLimits",
                        Default = "0",
                        Label = "Max recipients per account per period (0 = no limit)",
                        Placeholder = "2000",
                        Blurb = "Recipients are the stricter measure: one message to two thousand addresses is two " +
                                "thousand recipients."
                     },
                     new SectionTextSetting
                     {
                        Key = "PeriodHours",
                        Section = "SendingLimits",
                        Default = "24",
                        Label = "Period length in hours (default 24)",
                        Blurb = "Clamped to 1-168 by the server: a value outside that range is not an error, it is " +
                                "quietly pulled to the nearest bound."
                     },
                     new SectionTextSetting
                     {
                        Key = "StateSaveIntervalSeconds",
                        Section = "SendingLimits",
                        Default = "10",
                        Label = "Save the counters to disk every N seconds (default 10)",
                        Blurb = "How much sending history a crash can forget. Clamped to 1-3600; a value below 1 falls " +
                                "back to the default of 10."
                     },
                     new SectionLinesSetting
                     {
                        Key = "SendingLimitsOverrides",
                        Section = "SendingLimitsOverrides",
                        Label = "Per-address overrides, one per line: address=messages:recipients[:hours]",
                        Placeholder = "newsletter@yourdomain.com=5000:50000\nceo@yourdomain.com=0:0",
                        Blurb = "An override replaces the global ceilings for that address; 0:0 exempts it entirely. " +
                                "Without the optional :hours the override uses the global period. A malformed line is " +
                                "ignored with a log entry and the global limit still applies to that account. Comment " +
                                "lines in this INI section are not shown here and are dropped if the list is saved."
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Submission rate limits",
                  Blurb = "Throttle abusive senders submitting to this server. Limits are per minute; 0 disables the limit. " +
                          "(Throttling your own outbound rate to a destination is on the Delivery of e-mail page.)",
                  Settings =
                  {
                     new TextSetting { Key = "MaxSubmissionsPerIPPerMinute", Default = "0", Label = "Max authenticated submissions per client IP per minute (0 = unlimited)", Placeholder = "60" }
                  }
               });
               // Logging detail moved to the Logging page, indexer cadence to
               // Performance > Indexing and message archiving to Advanced &
               // scripting (next to the mirroring address), so each setting sits
               // with the feature it configures rather than on this catch-all page.
               cards_.Add(new CardDef
               {
                  Title = "Server-generated mail",
                  // The server builds mailer-daemon@<domain> from this and puts it
                  // in the From: header of bounces and virus notices; it has no
                  // effect on how or where mail is delivered.
                  Blurb = "Bounces and virus notifications are sent from mailer-daemon@<domain>. This overrides the " +
                          "<domain> part. Left empty, the server uses its own host name, then the local domain the " +
                          "message involves, then this computer's name. It is not a delivery setting and does not " +
                          "change where mail is routed.",
                  Settings =
                  {
                     new TextSetting { Key = "DaemonAddressDomain", Label = "Domain for the mailer-daemon sender address (empty = server host name)", Placeholder = "yourdomain.com" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Low-level tuning",
                  Blurb = "Specialist knobs — leave at the defaults unless you have a specific reason.",
                  Settings =
                  {
                     new TextSetting { Key = "SMTPDMaxSizeDrop", Default = "0", Label = "Drop oversized inbound messages above N bytes mid-transfer (0 = off)" },
                     // SAMoveVsCopy went to Anti-spam > SpamAssassin: it configures
                     // how a message reaches that scanner, and belongs with the host,
                     // port and timeouts that configure the rest of the same handoff.
                     new TextSetting { Key = "LoadHeaderReadSize", Default = "4000", Label = "Header read chunk size (bytes)" },
                     new TextSetting { Key = "LoadBodyReadSize", Default = "4000", Label = "Body read chunk size (bytes)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  // The blurb this replaces claimed that "sensitive values in
                  // hMailServer.INI (database password, OAuth/SRS/BATV secrets,
                  // password pepper) are encrypted with Windows DPAPI on the next
                  // service start". Read against the source, that was wrong three
                  // times over: nothing rewrites hMailServer.INI at start-up at
                  // all; Crypt::ProtectSecret is called only from Property::Save,
                  // PersistentRoute, PersistentFetchAccount and
                  // PersistentSSLCertificate, all of which write to the DATABASE;
                  // and OAuth2HmacSecret, SRSSecret, BATVSecret and PasswordPepper
                  // are each read with a plain ReadIniSettingString_, so this
                  // switch has never touched one of them. The database password is
                  // the only INI value it governs, and only at the moment it is
                  // set, through IniFileSettings::SetPassword.
                  Title = "Stored secret protection",
                  // "Turn it off to restore a backup onto a different machine" was
                  // the one reason this card gave, and it was misdirection: no
                  // backed-up secret is in a DPAPI envelope. Configuration::XMLStore
                  // writes the relayer password in clear (PropertySet decrypts at
                  // Refresh), and Route, FetchAccount and SSLCertificate hard-code
                  // Blowfish in both directions precisely so that backups restore
                  // onto replacement hardware; restore then re-envelopes under the
                  // destination machine's own setting. So a cross-machine restore
                  // works with this left on, and following the old advice downgraded
                  // every future secret write to a key that ships in the source.
                  Blurb = "Chooses the envelope hMailServer puts around the secrets it stores for its own use: machine-scoped " +
                          "Windows DPAPI, which cannot be decrypted on any other machine, or the legacy Blowfish scheme, " +
                          "which can. Leave it on. It does not affect backup and restore - nothing in a backup archive is " +
                          "DPAPI-protected, and a restore re-protects each secret under the destination machine's own " +
                          "setting - so a backup taken here restores onto another machine with this switched on.",
                  Settings =
                  {
                     new BoolSetting
                     {
                        Key = "ProtectStoredSecretsWithDPAPI",
                        Default = true,
                        Label = "Protect stored secrets with Windows DPAPI",
                        Blurb = "It covers exactly five things, and each is re-enveloped only when it is next saved rather " +
                                "than at start-up: the database password in hMailServer.INI, the SMTP relayer password, " +
                                "each route's authentication password, each external fetch account's password, and each " +
                                "SSL certificate's private-key passphrase. It does NOT cover the other secrets in " +
                                "hMailServer.INI - the SRS and BATV secrets, the OAuth2 HMAC secret, the password pepper, " +
                                "the metrics bearer token, the metrics HTTP Basic password and the Windows service " +
                                "account password are all stored as you typed them. Protecting those is the file's own " +
                                "permissions: hMailServer.INI should be readable only by Administrators and by the " +
                                "account the service runs as."
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  // The clearest case on this page of the rule that a setting whose
                  // effect happens outside the program has to say so. These two are
                  // read once, by ServiceManager::RegisterService, and the running
                  // service never looks at them again - so between saving here and
                  // running the registration, this page and the Service Control
                  // Manager disagree, and only one of them is what the machine
                  // actually does. Hence the live readout of the SCM below, and the
                  // exact command rather than a description of one.
                  Title = "Windows service account",
                  Blurb = "Which Windows account the hMailServer service logs on as. By default that is LocalSystem - the " +
                          "most privileged account on the machine - so a flaw reachable through SMTP, IMAP or POP3 is " +
                          "reachable with full control of the computer. Running the service as a dedicated account is the " +
                          "single largest reduction in what a compromise is worth, and the recommended one is the " +
                          "password-less virtual account NT SERVICE\\hMailServer.",
                  Settings =
                  {
                     new TextSetting
                     {
                        Key = "ServiceAccountName",
                        // NOT "empty = LocalSystem". Empty means LocalSystem only on
                        // the CreateService path, which runs when no hMailServer
                        // service exists yet. On any machine that already has one -
                        // which is every machine this page can read the SCM on -
                        // ServiceManager takes ReconfigureService_, where an empty
                        // value becomes NULL and ChangeServiceConfig is told to
                        // leave the logon account alone. Clearing the box to go back
                        // to LocalSystem therefore does nothing at all, silently.
                        Label = "Account for the service to log on as (empty = leave the current account unchanged)",
                        Placeholder = "NT SERVICE\\hMailServer",
                        Blurb = "Saving this does not move the service: it is read when the service is REGISTERED, so it " +
                                "takes effect only after the command in the status line below has been run. Clearing it " +
                                "does not move the service back to LocalSystem either - on an already-registered service " +
                                "an empty value means \"leave the account as it is\". To return to LocalSystem, set this " +
                                "to LocalSystem explicitly and re-register."
                     },
                     new SecretSetting
                     {
                        Key = "ServiceAccountPassword",
                        Label = "Password for that account (leave empty for NT SERVICE\\ and gMSA accounts)",
                        Hint = "Not needed for a virtual or managed account",
                        Blurb = "Stored in hMailServer.INI exactly as typed - the DPAPI switch above does not cover it, " +
                                "and there is nowhere else for the Service Control Manager's registration step to read " +
                                "it from. That is the strongest reason to use NT SERVICE\\hMailServer or a group managed " +
                                "service account instead: neither has a password to store. If you do set one here, clear " +
                                "it again once the registration has been run - the SCM keeps its own copy from then on."
                     }
                  },
                  Warnings =
                  {
                     new WarningDef
                     {
                        Aid = "ServiceAccountStatus",
                        Compute = ComputeServiceAccountState_
                     },
                     new WarningDef
                     {
                        Aid = "ServiceAccountGrants",
                        Compute = () =>
                        {
                           // Only worth saying once an account has been named: for
                           // LocalSystem none of it applies, and a permanent notice
                           // about work that is not needed is noise.
                           if (string.IsNullOrWhiteSpace(LiveText_("ServiceAccountName", "")))
                              return null;

                           // "Read access to the program folder" was wrong, and wrong
                           // in a way that breaks things quietly. hMailServer.INI
                           // lives in that folder, and the service process writes to
                           // it: hMailServer.exe is the out-of-process COM server, so
                           // every settings write executes under the service account.
                           // With read-only rights those writes fail silently -
                           // WriteIniSetting_ discards WritePrivateProfileString's
                           // result and SetAdministratorPassword returns S_OK anyway -
                           // so an administrator changes the administration password,
                           // is told it worked, and the old one comes back at the next
                           // restart. Following this card's own advice was what caused
                           // it.
                           return new WarningState
                           {
                              Level = StatusLevel.Information,
                              Text = "Three things have to be true of that account before the service will start under it, "
                                     + "and none of them can be done from here. It needs the \"Log on as a service\" right "
                                     + "(secpol.msc > Local Policies > User Rights Assignment, or your domain policy). It "
                                     + "needs full control of the data folder, the log folder and - for the built-in "
                                     + "database - the database folder, plus read access to the program folder AND write "
                                     + "access to hMailServer.INI inside it, because the service writes its own settings "
                                     + "there; if that file is read-only to the account, saved settings are lost at the "
                                     + "next restart with no error. And for an external database it needs whatever that "
                                     + "server requires, which for MSSQL with integrated security means a login of its "
                                     + "own. A service that cannot log on reports error 1069 in the Windows event log "
                                     + "and does not start."
                           };
                        }
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Settings that used to be on this page",
                  Blurb = "This page used to collect every hMailServer.INI value without a home, which meant it was " +
                          "filed by where the value is stored rather than by what it does - and where a value is " +
                          "stored is the one thing you never need to know. Each of these now sits with the rest of " +
                          "its own feature. Nothing was removed and no value changed; only the page it is edited on.",
                  Settings =
                  {
                     new ElsewhereSetting("antispam", "Greylisting record expiration, and SpamAssassin move-vs-copy"),
                     new ElsewhereSetting("protocols", "IMAP search time and size limits, and the eight per-protocol idle timeouts"),
                     new ElsewhereSetting("tls", "TLS session tickets, session cache size, resumption lifetime and ticket-key rotation"),
                     new ElsewhereSetting("delivery", "Extra delivery attempts per additional MX host"),
                     new ElsewhereSetting("performance", "Max parallel external POP3 fetch threads"),
                     new ElsewhereSetting("security", "SRS, BATV and the plain envelope-sender rewrite for forwarded mail")
                  }
               });
               break;

            case Section.Authentication:
               TitleText.Text = "Authentication";
               SubtitleText.Text = "How mailbox users prove who they are: external identity providers, how " +
                                   "passwords are stored, and where SMTP AUTH is offered (hMailServer.INI). " +
                                   "Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "OAuth2 / external identity provider",
                  Blurb = "Accept OAuth2 / OpenID Connect bearer tokens (XOAUTH2) from an external identity provider for IMAP, POP3 and SMTP submission, validated against the issuer's signing key. " +
                          "The mailbox named by the token still has to exist as a local account.",
                  Settings =
                  {
                     new BoolSetting { Key = "OAuth2Enabled", Default = false, Label = "Accept OAuth2 bearer tokens (XOAUTH2)" },
                     new BoolSetting { Key = "OAuth2RequireTLS", Default = true, Label = "Require TLS for token authentication" },
                     new TextSetting { Key = "OAuth2Issuer", Label = "Expected token issuer (iss)", Placeholder = "https://login.microsoftonline.com/<tenant>/v2.0" },
                     new TextSetting { Key = "OAuth2Audience", Label = "Expected audience (aud)", Placeholder = "your application / client id" },
                     new TextSetting { Key = "OAuth2AllowedAlgorithms", Default = "RS256", Label = "Allowed signing algorithms (comma separated)", Placeholder = "RS256, ES256" },
                     new TextSetting { Key = "OAuth2UsernameClaim", Default = "email", Label = "Claim that holds the mailbox address", Placeholder = "email" },
                     new PathSetting { Key = "OAuth2PublicKeyFile", FileFilter = "PEM/key files (*.pem;*.crt;*.cer;*.key;*.pub)|*.pem;*.crt;*.cer;*.key;*.pub|All files (*.*)|*.*", Label = "RSA/EC public key file (PEM, for RS*/ES* tokens)", Placeholder = "Path to the issuer's public key" },
                     new SecretSetting { Key = "OAuth2HmacSecret", Label = "Shared HMAC secret (only for HS256/384/512 tokens)", Hint = "Only needed for HS* algorithms" }
                  },
                  Warnings =
                  {
                     // Seven settings that only work as a set, and no way to see
                     // whether the set is complete. Every one of them missing
                     // produces the same symptom - a token that is rejected and a
                     // log line saying only that a logon failed - so an incomplete
                     // configuration reads to the administrator, and to the user
                     // on the phone, as "the password is wrong".
                     new WarningDef
                     {
                        Aid = "OAuth2CoherenceState",
                        Compute = ComputeOAuth2State_
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Password storage",
                  Blurb = "How account passwords are hashed, and the weakest stored hash still allowed to log on. " +
                          "Existing passwords keep their current hash until they are next changed.",
                  Settings =
                  {
                     new ChoiceSetting
                     {
                        Key = "PreferredHashAlgorithm",
                        Default = 4,
                        Label = "Password hash for new or changed account passwords",
                        Options = new (int, string)[]
                        {
                           (5, "Argon2id (strongest)"),
                           (4, "PBKDF2 (default)"),
                           (3, "SHA-256"),
                           (2, "MD5 (legacy)"),
                           (1, "Blowfish (legacy)")
                        }
                     },
                     new ChoiceSetting
                     {
                        Key = "MinimumAcceptedHashAlgorithm",
                        Default = 0,
                        Label = "Reject logins using a weaker stored hash than",
                        Options = new (int, string)[]
                        {
                           (0, "Accept any stored hash"),
                           (3, "SHA-256 or stronger"),
                           (4, "PBKDF2 or stronger"),
                           (5, "Argon2id only")
                        }
                     },
                     new SecretSetting { Key = "PasswordPepper", Label = "Password pepper — WARNING: set before creating accounts; changing it later invalidates ALL existing passwords", Hint = "Server-wide secret mixed into password hashes" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "SMTP authentication",
                  Blurb = "AUTH is normally offered on every SMTP port. List the local TCP ports where it should not be, " +
                          "for example a port 25 that only accepts inbound mail from other servers.",
                  Settings =
                  {
                     new TextSetting { Key = "DisableAUTHList", Label = "Do not offer AUTH on these local TCP ports (comma separated)", Placeholder = "25" }
                  }
               });
               break;

            case Section.Dns:
               TitleText.Text = "DNS resolver";
               SubtitleText.Text = "How this server resolves MX, PTR, SPF, DKIM, DMARC and blacklist lookups " +
                                   "(hMailServer.INI). Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "Name servers",
                  Blurb = "Which resolver hMailServer queries. Leave empty to use the name servers Windows is configured with. " +
                          "The override takes a single IPv4 address and is used on port 53; DNSSEC validation and its trust " +
                          "anchors are on the Transport security page.",
                  Settings =
                  {
                     new TextSetting { Key = "DNSServer", Label = "Override DNS server (empty = the name servers Windows uses)", Placeholder = "1.1.1.1" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "DNS cache",
                  Blurb = "Keeps DNS answers in memory for their TTL so repeated lookups for the same host do not go out to " +
                          "the network again. It only reduces the number of lookups — it is not a substitute for a local " +
                          "caching resolver on a busy server. Setting an override DNS server above always bypasses the cache.",
                  Settings =
                  {
                     new BoolSetting { Key = "UseDNSCache", Default = true, Label = "Cache DNS lookup results in memory" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "DNS blacklist checks",
                  Blurb = "When during the SMTP conversation blacklist lookups happen. Which blacklists are queried is on the " +
                          "DNS blacklists page.",
                  Settings =
                  {
                     new BoolSetting { Key = "DNSBLChecksAfterMailFrom", Default = true, Label = "Run DNSBL checks after MAIL FROM (rather than at connect)" }
                  }
               });
               break;

            case Section.WebServices:
               TitleText.Text = "Web services & client autoconfiguration";
               SubtitleText.Text = "The built-in HTTP listener that serves mail-client autoconfiguration and MTA-STS " +
                                   "policies for your local domains (hMailServer.INI). Changes take effect after a " +
                                   "service restart.";
               cards_.Add(new CardDef
               {
                  Title = "Listener",
                  Blurb = "Nothing below is served until a port is set here. The server answers on either port, but an " +
                          "MTA-STS policy is only valid over HTTPS and Outlook only accepts autodiscover over HTTPS — " +
                          "so in practice set the HTTPS port and a certificate covering the host names clients use.",
                  Settings =
                  {
                     new TextSetting { Key = "WebServicesHttpPort", Default = "0", Label = "HTTP port (80 to enable, 0 = disabled)" },
                     new TextSetting { Key = "WebServicesHttpsPort", Default = "0", Label = "HTTPS port (443 to enable, 0 = disabled)" },
                     new TextSetting { Key = "WebServicesBindAddress", Default = "0.0.0.0", Label = "Bind address" },
                     new PathSetting { Key = "WebServicesCertificateFile", FileFilter = "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new PathSetting { Key = "WebServicesPrivateKeyFile", FileFilter = "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*", Label = "TLS private key file (PEM, optional)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Client autoconfiguration (autoconfig & autodiscover)",
                  Blurb = "Hands mail clients the right host names, ports and security settings automatically: Thunderbird-style " +
                          "autoconfig and Outlook autodiscover, for every local domain. Point autoconfig.<domain> and " +
                          "autodiscover.<domain> at this server in DNS.",
                  Settings =
                  {
                     new BoolSetting { Key = "AutoconfigEnabled", Default = true, Label = "Thunderbird autoconfig + Outlook autodiscover" },
                     new TextSetting { Key = "AutoconfigClientHost", Label = "Host name clients connect to (empty = server host name)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "MTA-STS policy hosting",
                  Blurb = "Publishes https://mta-sts.<domain>/.well-known/mta-sts.txt so other servers require TLS when they " +
                          "deliver to you. Honouring other domains' policies is on the Transport security page.",
                  Settings =
                  {
                     new BoolSetting { Key = "MtaStsHostingEnabled", Default = true, Label = "Serve MTA-STS policies for local domains" },
                     new TextSetting { Key = "MtaStsPolicyMode", Default = "enforce", Label = "MTA-STS policy mode (enforce / testing / none)" },
                     new TextSetting { Key = "MtaStsPolicyMaxAge", Default = "604800", Label = "Policy max age (seconds; default 604800 = 7 days)" },
                     new TextSetting { Key = "MtaStsPolicyMx", Label = "Policy MX host patterns (empty = derive from each domain's MX)", Placeholder = "mail.yourdomain.com, *.yourdomain.com" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Calendar and contacts discovery (CalDAV / CardDAV)",
                  Blurb = "hMailServer does NOT implement CalDAV or CardDAV. These settings only answer the well-known " +
                          "discovery URLs (RFC 6764) with a redirect to the server that does, so a client configured " +
                          "with a user's mail address finds their calendar without being told a second address. Leave " +
                          "both empty unless you run such a server: empty means the discovery URLs answer 404, which " +
                          "is the honest response when there is nothing to point at. They need the web services " +
                          "listener above to be running, like everything else on this page.",
                  Settings =
                  {
                     new TextSetting
                     {
                        Key = "CalDavRedirectUrl",
                        Label = "Redirect /.well-known/caldav to (empty = answer 404)",
                        Placeholder = "https://calendar.yourdomain.com/dav/",
                        Blurb = "Must be an absolute URL. A relative one is refused and the discovery URL answers 404 instead, " +
                                "with the reason reported once in the error log."
                     },
                     new TextSetting
                     {
                        Key = "CardDavRedirectUrl",
                        Label = "Redirect /.well-known/carddav to (empty = answer 404)",
                        Placeholder = "https://contacts.yourdomain.com/dav/",
                        Blurb = "Must be an absolute URL, as above."
                     }
                  }
               });
               break;
         }
      }

      /// <summary>
      /// Works out what a screen reader should call each editor, before any of them
      /// are built.
      ///
      /// No label on these seven pages currently repeats within its own page - that
      /// was counted rather than assumed - so nothing here is qualified today. It
      /// still goes through the same resolver as the COM settings pages: the naming
      /// rule must not be one thing on one settings page and another on the next,
      /// and a card added to this page tomorrow that repeats a label somewhere else
      /// on it will be handled without anybody having to remember to.
      /// <see cref="AccessibleNames"/> holds the decision.
      /// </summary>
      private void AssignAccessibleNames()
      {
         var settings = new List<Setting>();
         var editors = new List<LabelledEditor>();

         foreach (CardDef card in cards_)
         foreach (Setting setting in card.Settings)
         {
            settings.Add(setting);
            editors.Add(new LabelledEditor(setting.Label, card.Title, setting.Key));
         }

         IReadOnlyList<string> names = AccessibleNames.Resolve(editors);
         for (int i = 0; i < settings.Count; i++)
            settings[i].AccessibleName = names[i];
      }

      private void BuildUi()
      {
         CardsPanel.Children.Clear();
         warningRows_.Clear();

         AssignAccessibleNames();

         if (!store_.IsAvailable)
         {
            SubtitleText.Text = "hMailServer.INI was not found on this machine. " +
                                "These settings can only be edited on the server itself.";
            SaveButton.IsEnabled = false;
            return;
         }

         foreach (CardDef card in cards_)
         {
            var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
            border.SetResourceReference(StyleProperty, "Card");

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = card.Title,
               FontSize = Typography.SectionHeading,
               FontWeight = FontWeights.SemiBold,
               Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock
            {
               Text = card.Blurb,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 0, 0, 14)
            });

            FrameworkElement lastEditor = null;
            foreach (FrameworkElement editor in card.Settings.Select(s => s.CreateEditor(store_)))
            {
               editor.Margin = new Thickness(0, 0, 0, 12);
               panel.Children.Add(editor);
               lastEditor = editor;
            }

            if (lastEditor != null)
               lastEditor.Margin = new Thickness(0, 0, 0, 2);

            foreach (WarningDef def in card.Warnings)
               panel.Children.Add(BuildWarningRow_(def));

            border.Child = panel;
            CardsPanel.Children.Add(border);
         }

         // Wire every editor to the warnings AFTER all the editors exist, because
         // a warning may read a setting on a later card than its own.
         foreach (CardDef card in cards_)
         foreach (Setting setting in card.Settings)
            setting.OnEditorChanged(RefreshWarnings_);

         RefreshWarnings_();

         StatusText.Text = "Editing " + store_.IniPath;
      }

      /// <summary>
      /// One (initially collapsed) warning row: the shape mark and the text beside
      /// it, in the same three channels - colour, shape and word - as the dashboard
      /// and Spam overview badges, so none of the three is load-bearing alone.
      /// </summary>
      private FrameworkElement BuildWarningRow_(WarningDef def)
      {
         var row = new Grid { Margin = new Thickness(0, 10, 0, 2), Visibility = Visibility.Collapsed };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         if (!string.IsNullOrEmpty(def.Aid))
            System.Windows.Automation.AutomationProperties.SetAutomationId(row, def.Aid);

         var mark = new System.Windows.Shapes.Path
         {
            Width = 11,
            Height = 11,
            Stretch = Stretch.Fill,
            Margin = new Thickness(0, 4, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
         };
         row.Children.Add(mark);

         var text = new TextBlock
         {
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap
         };
         Grid.SetColumn(text, 1);
         row.Children.Add(text);

         warningRows_.Add(new WarningRow { Def = def, Row = row, Mark = mark, Text = text });
         return row;
      }

      /// <summary>
      /// Re-evaluates every computed warning on the page against the editors as
      /// they stand. Runs once after the page is built and again on every editor
      /// change, so ticking a switch that makes a configuration inert says so
      /// immediately - not after a save, a restart and a support thread.
      /// </summary>
      private void RefreshWarnings_()
      {
         foreach (WarningRow row in warningRows_)
         {
            WarningState state;
            try
            {
               state = row.Def.Compute();
            }
            catch
            {
               // A warning must never take the page down; a state it cannot
               // evaluate is a state it does not report.
               state = null;
            }

            if (state == null)
            {
               row.Row.Visibility = Visibility.Collapsed;
               continue;
            }

            StatusPresentation presentation = StatusSemantics.For(state.Level);
            ShapeMarkVisuals.ApplyMark(row.Mark, presentation.Shape, presentation.BrushKey);

            row.Text.Inlines.Clear();
            row.Text.Inlines.Add(new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold });
            row.Text.Inlines.Add(new Run(state.Text));

            row.Row.Visibility = Visibility.Visible;
         }
      }

      private void Reload_Click(object sender, RoutedEventArgs e)
      {
         // Reload means reload, including the state read from outside the INI.
         // Without this the button re-read the file but kept the cached Service
         // Control Manager snapshot, so an administrator who ran the registration
         // command in the other window and pressed Reload - the obvious thing to
         // do, and what the status line invites - was shown the pre-registration
         // account and told the change had not been applied.
         ForgetExternalState_();

         BuildDefinition();
         BuildUi();
      }

      /// <summary>
      /// Drops everything the computed warnings cache from outside hMailServer.INI.
      ///
      /// Each is cached per page instance because a computed warning re-runs on
      /// every keystroke and these are expensive - a WMI query, two COM collection
      /// walks, and parsing two PEM files. That makes them stale by construction,
      /// so both entry points that mean "show me the current state" clear them.
      /// </summary>
      private void ForgetExternalState_()
      {
         serviceInfo_ = null;
         tlsPortCertificate_ = null;
         tlsPortCertificateRead_ = false;
         acmeHealth_ = null;
         acmeHealthFolder_ = null;
      }

      private void Save_Click(object sender, RoutedEventArgs e)
      {
         try
         {
            foreach (CardDef card in cards_)
               foreach (Setting setting in card.Settings)
                  setting.Save(store_);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save: " + ex.Message, "Control Panel",
               MessageBoxButton.OK, MessageBoxImage.Error);
            return;
         }

         StatusText.Text = "Saved " + DateTime.Now.ToLongTimeString() + " - restart the service to apply.";

         if (MessageBox.Show(
                "Settings saved. The hMailServer service must be restarted for the changes to take effect.\n\nRestart it now?",
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
         {
            RestartService();
         }
      }

      private async void RestartService()
      {
         StatusText.Text = "Restarting the hMailServer service...";

         string error = await Task.Run(() => TryRestartService());
         if (error != null)
         {
            StatusText.Text = "The service could not be restarted.";
            MessageBox.Show("Could not restart the service: " + error,
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         StatusText.Text = "Service restarted - settings are live.";
         Reattach();
      }

      /// <summary>
      /// Stops and starts the service, returning null on success or a message
      /// describing the failure. Stopping a service needs administrator rights
      /// and the Control Panel runs asInvoker, so a non-elevated session hands
      /// the work to net.exe behind a single UAC prompt instead of failing
      /// with an opaque "Cannot open hMailServer service" error.
      /// </summary>
      private static string TryRestartService()
      {
         try
         {
            bool elevated;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
               elevated = new System.Security.Principal.WindowsPrincipal(identity)
                  .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            if (elevated)
            {
               using var controller = new ServiceController("hMailServer");
               if (controller.Status != ServiceControllerStatus.Stopped)
               {
                  if (controller.Status == ServiceControllerStatus.Running ||
                      controller.Status == ServiceControllerStatus.Paused)
                     controller.Stop();
                  controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
               }
               controller.Start();
               controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
               return null;
            }

            // "net stop" fails harmlessly when the service is already stopped,
            // so chain with "&" rather than "&&".
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
               FileName = "cmd.exe",
               Arguments = "/c net stop hMailServer & net start hMailServer",
               UseShellExecute = true,
               Verb = "runas",
               WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using (var process = System.Diagnostics.Process.Start(startInfo))
               process?.WaitForExit();

            using (var controller = new ServiceController("hMailServer"))
               controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return null;
         }
         catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
         {
            return "the elevation prompt was cancelled.";
         }
         catch (Exception ex)
         {
            return ex.GetBaseException().Message;
         }
      }

      /// <summary>
      /// The COM server lives inside the service process, so restarting it
      /// invalidates every interface pointer the Control Panel holds. Rebuild
      /// the session straight away rather than letting the next page fail with
      /// "The RPC server is unavailable". The service registers its COM class
      /// factory a moment before it can serve calls, hence the retries.
      /// </summary>
      private void Reattach()
      {
         ServerSession session = ServerSession.Current;
         if (session == null)
            return;

         Mouse.OverrideCursor = Cursors.Wait;
         try
         {
            if (session.Reconnect(20, TimeSpan.FromMilliseconds(500), out string error))
               return;

            session.Invalidate();
            StatusText.Text = "Service restarted, but the Control Panel could not reconnect.";
            MessageBox.Show(
               "The service was restarted but the Control Panel could not reconnect to it: " + error +
               "\n\nIt will keep trying as you use the application.",
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
         finally
         {
            Mouse.OverrideCursor = null;
         }
      }
   }
}
