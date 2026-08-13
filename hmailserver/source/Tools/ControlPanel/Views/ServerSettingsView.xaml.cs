using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using TextBox = Wpf.Ui.Controls.TextBox;
using System.Linq;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Tabbed, data-driven settings pages backed by the COM Settings object.
   /// Mirrors the classic Administrator layout: each section is a TabControl
   /// whose tabs group related cards. Property paths are dotted
   /// ("AntiSpam.SpamMarkThreshold") and resolved against app.Settings.
   /// </summary>
   public partial class ServerSettingsView : UserControl, IPageLifecycle
   {
      public enum Section
      {
         Protocols,
         Delivery,
         AntiSpam,
         AntiVirus,

         /// <summary>
         /// TLS versions, ciphers and remote-certificate verification. Auto-ban
         /// used to be a third tab here; it is <see cref="AutoBan"/> now, because
         /// brute-force lockout and transport encryption share nothing but the
         /// letters SSL.
         /// </summary>
         Tls,
         AutoBan,
         Logging,
         Performance,
         Advanced,
         AdminAccess
      }

      // ---- editor model ------------------------------------------------------

      private abstract class ComSetting
      {
         public string Path;   // dotted path under app.Settings
         public string Label;

         /// <summary>
         /// What a screen reader should call this editor. Assigned by
         /// <see cref="AssignAccessibleNames"/> before the editors are built,
         /// because the answer depends on the whole page - see
         /// <see cref="AccessibleNames"/>.
         /// </summary>
         public string AccessibleName;

         /// <summary>
         /// A caption printed under the editor and attached to it as accessible
         /// help text. Some rows already carried one; the honest statements in
         /// <see cref="SettingClaims"/> about what the server actually does with a
         /// value are the reason it is now on every row type rather than on two of
         /// them.
         /// </summary>
         public string Blurb;

         public virtual bool WantsInitialValue => true;
         public abstract FrameworkElement CreateEditor(object value);
         public abstract object ReadEditor();

         // Give the interactive editor control a stable AutomationId so
         // UI-automation can address it reliably.
         protected static void SetAid(FrameworkElement element, string id)
         {
            if (element != null && !string.IsNullOrEmpty(id))
               System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
         }

         /// <summary>
         /// The AutomationId plus the accessible name.
         ///
         /// An AutomationId is not spoken; it exists for test automation. Every
         /// editor on these pages is labelled by a separate TextBlock above it, and
         /// WPF does not connect the two, so before this each of the ~150 settings
         /// on these pages was announced as a bare "edit" or "combo box" with no
         /// name at all. Checkboxes were the only exception, because their label is
         /// their Content.
         /// </summary>
         protected void Describe(FrameworkElement element, string id)
         {
            SetAid(element, id);

            if (element != null && !string.IsNullOrEmpty(AccessibleName))
               System.Windows.Automation.AutomationProperties.SetName(element, AccessibleName);
         }

         /// <summary>
         /// Prints <see cref="Blurb"/> under the control and attaches it to the
         /// control as accessible help text.
         ///
         /// Both, deliberately. A caption sitting loose in the panel is a separate
         /// node in the automation tree, so a listener reaches it only after moving
         /// past the editor - and a note saying "this value is overridden" or "the
         /// server does less than this suggests" is exactly the part that has to be
         /// heard with the control, not after it.
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

         protected static string Slug(string text)
         {
            if (string.IsNullOrEmpty(text))
               return "";
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text.ToLowerInvariant())
               sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
         }

         public virtual void Write(object owner, string property)
         {
            object value = ReadEditor();
            if (value is long n)
               SetProperty(owner, property, (int) n);
            else
               SetProperty(owner, property, value);
         }
      }

      private class ComBool : ComSetting
      {
         private CheckBox box_;

         /// <summary>The created checkbox (for cross-field dependency wiring).</summary>
         public CheckBox Box => box_;

         public override FrameworkElement CreateEditor(object value)
         {
            box_ = new CheckBox { Content = Label, IsChecked = value is bool b && b, FontSize = 13.5 };
            SetAid(box_, Path);

            // A checkbox is named by its Content, so it needs an override only
            // when the resolved name differs from the label - which happens when
            // the same wording appears on more than one card and has been
            // qualified with the card title.
            if (!string.IsNullOrEmpty(AccessibleName) &&
                !string.Equals(AccessibleName, Label, StringComparison.Ordinal))
            {
               System.Windows.Automation.AutomationProperties.SetName(box_, AccessibleName);
            }

            // Blurb was silently dropped on this row type: every other editor here
            // calls Annotate and this one returned the bare checkbox, so a note
            // written on a COM-backed checkbox appeared nowhere and no build
            // warning said so. IniBool - the same control backed by the INI file -
            // has always wrapped and annotated, which is what makes the omission a
            // bug rather than a decision. Only wrapped when there is something to
            // say, so the ~60 checkboxes with no Blurb keep the visual tree they
            // had.
            if (string.IsNullOrEmpty(Blurb))
               return box_;

            var panel = new StackPanel();
            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override object ReadEditor() => box_.IsChecked == true;
      }

      private class ComText : ComSetting
      {
         public bool Numeric;
         public int Divisor = 1;   // numeric display scaling (e.g. hours stored, days shown)
         public bool BrowseFile;   // show a "..." file picker next to the box
         public bool BrowseFolder; // show a "..." folder picker next to the box
         public string FileFilter = "All files (*.*)|*.*";
         private TextBox box_;
         private Wpf.Ui.Controls.NumberBox number_;

         /// <summary>Current text in the editor (for live test buttons).</summary>
         public string CurrentText
            => number_ != null
               ? ((long) (number_.Value ?? 0)).ToString()
               : (box_?.Text?.Trim() ?? "");

         /// <summary>Overwrites the editor text (for preset pickers / auto-detect).</summary>
         public void SetText(string text)
         {
            if (number_ != null)
               number_.Value = double.TryParse(text, out double d) ? d : 0;
            else if (box_ != null)
               box_.Text = text ?? "";
         }
         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            object shown = value;
            if (Numeric && Divisor > 1 && value != null)
            {
               try { shown = Convert.ToInt64(value) / Divisor; } catch (Exception) { shown = value; }
            }

            // Numeric settings get an up/down NumberBox; everything else a text box.
            if (Numeric)
            {
               double current = 0;
               try { current = Convert.ToDouble(shown); } catch (Exception) { current = 0; }
               number_ = new Wpf.Ui.Controls.NumberBox
               {
                  Value = current,
                  Minimum = 0,
                  MaxDecimalPlaces = 0,
                  SmallChange = 1,
                  LargeChange = 10,
                  FontSize = 13,
                  MaxWidth = 180,
                  MinWidth = 120,
                  HorizontalAlignment = HorizontalAlignment.Left
               };
               Describe(number_, Path);
               panel.Children.Add(number_);
               Annotate(number_, panel);
               return panel;
            }

            box_ = new TextBox
            {
               Text = Convert.ToString(shown) ?? "",
               FontSize = 13,
               MaxWidth = 520,
               HorizontalAlignment = HorizontalAlignment.Left,
               MinWidth = 320
            };
            Describe(box_, Path);

            if (BrowseFile || BrowseFolder)
            {
               var row = new Grid { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
               row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
               row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
               box_.HorizontalAlignment = HorizontalAlignment.Stretch;
               box_.MaxWidth = double.PositiveInfinity;
               box_.MinWidth = 0;
               Grid.SetColumn(box_, 0);
               row.Children.Add(box_);

               var browse = new Wpf.Ui.Controls.Button
               {
                  Content = "\u2026",
                  MinWidth = 40,
                  Margin = new Thickness(8, 0, 0, 0),
                  VerticalAlignment = VerticalAlignment.Bottom,
                  ToolTip = BrowseFolder ? "Browse for a folder" : "Browse for a file"
               };
               SetAid(browse, Path + "Browse");
               // The button's content is a single ellipsis character, so its
               // content names it "…" and a listener is told nothing at all about
               // which of the several browse buttons on the page they are on.
               System.Windows.Automation.AutomationProperties.SetName(browse,
                  (BrowseFolder ? "Browse for a folder for " : "Browse for a file for ")
                  + (AccessibleName ?? Label ?? "this setting"));
               browse.Click += (s, e) =>
               {
                  string picked = BrowseFolder
                     ? Services.PathPicker.PickFolder(box_.Text)
                     : Services.PathPicker.PickFile(box_.Text, FileFilter);
                  if (picked != null)
                     box_.Text = picked;
               };
               Grid.SetColumn(browse, 1);
               row.Children.Add(browse);
               panel.Children.Add(row);
            }
            else
            {
               panel.Children.Add(box_);
            }

            Annotate(box_, panel);
            return panel;
         }

         public override object ReadEditor()
         {
            if (Numeric)
            {
               long number = number_ != null ? (long) (number_.Value ?? 0) : 0L;
               return number * Divisor;
            }
            return box_.Text.Trim();
         }
      }

      /// <summary>
      /// Marker for settings stored in hMailServer.ini rather than in the COM
      /// settings tree, so the save path can route them to the INI store.
      /// </summary>
      private interface IIniSetting
      {
         void SaveToIni();
      }

      /// <summary>
      /// A yes/no setting stored in hMailServer.ini. Same purpose as IniNumber:
      /// it lets an INI-backed switch sit on the page where admins look for it.
      /// </summary>
      private class IniBool : ComSetting, IIniSetting
      {
         public bool Default;
         public IniFeatureStore IniStore;
         private CheckBox box_;

         /// <summary>The created checkbox (for cross-field dependency wiring).</summary>
         public CheckBox Box => box_;

         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            bool current = Default;
            if (IniStore != null && IniStore.IsAvailable)
               current = IniStore.ReadBool(Path, Default);

            var panel = new StackPanel();

            box_ = new CheckBox { Content = Label, IsChecked = current, FontSize = 13.5 };
            SetAid(box_, Path);
            if (!string.IsNullOrEmpty(AccessibleName) &&
                !string.Equals(AccessibleName, Label, StringComparison.Ordinal))
            {
               System.Windows.Automation.AutomationProperties.SetName(box_, AccessibleName);
            }

            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override object ReadEditor() => box_?.IsChecked == true;

         public void SaveToIni()
         {
            if (IniStore == null || !IniStore.IsAvailable || box_ == null)
               return;

            IniStore.WriteBool(Path, box_.IsChecked == true);
         }
      }

      /// <summary>
      /// A numeric setting stored in hMailServer.ini rather than in the COM
      /// settings tree. It exists so an INI-backed knob can sit on the page
      /// where users look for it (log retention belongs with the other logging
      /// settings, not on an unrelated page). Path is the INI key name.
      /// </summary>
      private class IniNumber : ComSetting, IIniSetting
      {
         public int Default;
         private Wpf.Ui.Controls.NumberBox number_;

         // The value comes from the INI file, not from a COM property.
         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var store = IniStore;
            int current = Default;
            if (store != null && store.IsAvailable)
            {
               string raw = store.Read(Path, Default.ToString());
               if (!int.TryParse(raw, out current))
                  current = Default;
            }

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            number_ = new Wpf.Ui.Controls.NumberBox
            {
               Value = current,
               Minimum = 0,
               MaxDecimalPlaces = 0,
               SmallChange = 1,
               LargeChange = 10,
               FontSize = 13,
               MaxWidth = 180,
               MinWidth = 120,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            Describe(number_, Path);
            panel.Children.Add(number_);
            Annotate(number_, panel);
            return panel;
         }

         public override object ReadEditor() => (long) (number_?.Value ?? Default);

         /// <summary>Persists to hMailServer.ini. Save_Click calls this instead
         /// of the COM write path.</summary>
         public void SaveToIni()
         {
            var store = IniStore;
            if (store == null || !store.IsAvailable || number_ == null)
               return;

            store.Write(Path, ((long) (number_.Value ?? Default)).ToString());
         }

         /// <summary>Shared store, assigned by the view when the page is built.</summary>
         public IniFeatureStore IniStore;
      }

      /// <summary>
      /// A free-text setting stored in hMailServer.ini. Same purpose as IniBool
      /// and IniNumber: it puts an INI-backed value on the page that owns the
      /// feature it configures (the archive folder belongs next to the mirroring
      /// address, not on a catch-all page). Path is the INI key name.
      /// </summary>
      private class IniText : ComSetting, IIniSetting
      {
         public string Placeholder = "";
         public bool BrowseFolder;
         public IniFeatureStore IniStore;
         private TextBox box_;

         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            string current = "";
            if (IniStore != null && IniStore.IsAvailable)
               current = IniStore.Read(Path, "");

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            box_ = new TextBox
            {
               Text = current,
               PlaceholderText = Placeholder,
               FontSize = 13,
               HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Describe(box_, Path);

            if (BrowseFolder)
            {
               var row = new Grid { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
               row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
               row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
               Grid.SetColumn(box_, 0);
               row.Children.Add(box_);

               var browse = new Wpf.Ui.Controls.Button
               {
                  Content = "\u2026",
                  MinWidth = 40,
                  Margin = new Thickness(8, 0, 0, 0),
                  VerticalAlignment = VerticalAlignment.Bottom,
                  ToolTip = "Browse for a folder"
               };
               SetAid(browse, Path + "Browse");
               // See ComText: "\u2026" is not a name.
               System.Windows.Automation.AutomationProperties.SetName(browse,
                  "Browse for a folder for " + (AccessibleName ?? Label ?? "this setting"));
               browse.Click += (s, e) =>
               {
                  string picked = Services.PathPicker.PickFolder(box_.Text);
                  if (picked != null)
                     box_.Text = picked;
               };
               Grid.SetColumn(browse, 1);
               row.Children.Add(browse);
               panel.Children.Add(row);
            }
            else
            {
               box_.HorizontalAlignment = HorizontalAlignment.Left;
               box_.MinWidth = 320;
               box_.MaxWidth = 520;
               panel.Children.Add(box_);
            }

            Annotate(box_, panel);
            return panel;
         }

         public override object ReadEditor() => box_?.Text?.Trim() ?? "";

         public void SaveToIni()
         {
            if (IniStore == null || !IniStore.IsAvailable || box_ == null)
               return;

            IniStore.Write(Path, box_.Text.Trim());
         }
      }

      private class ComCombo : ComSetting
      {
         public (int Value, string Label)[] Options;
         private ComboBox combo_;

         /// <summary>The created combo (for cross-field dependency wiring).</summary>
         public ComboBox Combo => combo_;

         /// <summary>The value currently selected, or 0 before the editor is built.</summary>
         public int SelectedValue
            => combo_?.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : 0;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            combo_ = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13 };
            int sel;
            try { sel = Convert.ToInt32(value); } catch (Exception) { sel = 0; }
            foreach ((int v, string l) in Options)
            {
               var item = new ComboBoxItem { Content = l, Tag = v };
               combo_.Items.Add(item);
               if (v == sel)
                  combo_.SelectedItem = item;
            }
            if (combo_.SelectedItem == null && combo_.Items.Count > 0)
               combo_.SelectedIndex = 0;
            Describe(combo_, Path);
            panel.Children.Add(combo_);
            Annotate(combo_, panel);
            return panel;
         }

         public override object ReadEditor()
            => combo_.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : 0;
      }

      private class ComPassword : ComSetting
      {
         public string MethodName;   // e.g. SetSMTPRelayerPassword
         private Wpf.Ui.Controls.PasswordBox box_;
         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            box_ = new Wpf.Ui.Controls.PasswordBox
            {
               FontSize = 13,
               MinWidth = 320,
               MaxWidth = 520,
               Padding = new Thickness(6),
               HorizontalAlignment = HorizontalAlignment.Left
            };
            Describe(box_, Path);
            panel.Children.Add(box_);
            Annotate(box_, panel);
            return panel;
         }

         public override object ReadEditor() => box_.Password;

         public override void Write(object owner, string property)
         {
            string pw = box_.Password;
            if (string.IsNullOrEmpty(pw))
               return;   // leave the current password unchanged
            owner.GetType().InvokeMember(MethodName, BindingFlags.InvokeMethod, null, owner, new object[] { pw });
         }
      }

      /// <summary>A non-persistent action button (e.g. "Test connection") with a result line.</summary>
      private class ComAction : ComSetting
      {
         public string ButtonText;
         public Func<(bool ok, string text)> Action;
         private System.Windows.Controls.TextBlock result_;
         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            var btn = new Wpf.Ui.Controls.Button
            {
               Content = ButtonText,
               Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            result_ = new System.Windows.Controls.TextBlock
            {
               FontSize = 12,
               Margin = new Thickness(0, 8, 0, 0),
               TextWrapping = TextWrapping.Wrap
            };

            // A polite live region, so the outcome of "Test ClamAV connection" is
            // announced rather than only coloured. Colour is the only other channel
            // this line has - green for success, red for failure - so without this a
            // screen-reader user pressed the button and was told nothing whatsoever,
            // and there is no way to discover the answer by moving around, because
            // the text did not exist before the click.
            //
            // Polite rather than assertive, and safe to make a live region here in a
            // way the dashboard's summary line is not (see AccessibleChartCard): this
            // text changes only when the user presses the button, so an announcement
            // is the answer to something they just asked.
            System.Windows.Automation.AutomationProperties.SetLiveSetting(
               result_, System.Windows.Automation.AutomationLiveSetting.Polite);
            System.Windows.Automation.AutomationProperties.SetName(result_,
               (ButtonText ?? "Test") + " result");

            btn.Click += (s, e) =>
            {
               try
               {
                  (bool ok, string text) r = Action();
                  result_.Text = r.text;
                  result_.Foreground = r.ok
                     ? Services.ThemeTokens.Success
                     : Services.ThemeTokens.Danger;
               }
               catch (Exception ex)
               {
                  result_.Text = "Test failed: " + ex.Message;
                  result_.Foreground = Services.ThemeTokens.Danger;
               }
            };
            panel.Children.Add(btn);
            panel.Children.Add(result_);
            SetAid(btn, "action-" + Slug(ButtonText));

            // Blurb was silently dropped on this row type too - the same omission that
            // was found and fixed on the checkbox row. Every ComAction until now
            // happened to have no Blurb, so nothing was visibly missing; the first one
            // that needed a caption (the log folder path) would have set it and got
            // nothing. Annotate also attaches the text to the button as accessible
            // help, which is where a caption about what the button will do belongs.
            Annotate(btn, panel);

            return panel;
         }

         public override object ReadEditor() => null;
         public override void Write(object owner, string property) { }
      }

      /// <summary>
      /// A non-persistent preset picker that fills other <see cref="ComText"/>
      /// editors when a preset is chosen (e.g. fill the custom-scanner command line
      /// and infected return value for a known AV engine).
      /// </summary>
      private class ComPreset : ComSetting
      {
         public (string Name, string Exe, int ReturnValue)[] Presets;
         public ComText ExeTarget;
         public ComText ReturnTarget;
         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13 };
            combo.Items.Add(new ComboBoxItem { Content = "Choose a preset\u2026", Tag = -1 });
            for (int i = 0; i < Presets.Length; i++)
               combo.Items.Add(new ComboBoxItem { Content = Presets[i].Name, Tag = i });
            combo.SelectedIndex = 0;

            combo.SelectionChanged += (s, e) =>
            {
               if (combo.SelectedItem is ComboBoxItem cbi && cbi.Tag is int idx && idx >= 0 && idx < Presets.Length)
               {
                  (string Name, string Exe, int ReturnValue) p = Presets[idx];
                  ExeTarget?.SetText(p.Exe);
                  ReturnTarget?.SetText(p.ReturnValue.ToString());
               }
            };

            panel.Children.Add(combo);
            Describe(combo, "preset-" + Slug(Label));
            return panel;
         }

         public override object ReadEditor() => null;
         public override void Write(object owner, string property) { }
      }

      /// <summary>
      /// Marker for a row that writes nothing, so <see cref="Save_Click"/> does not
      /// count it among the settings it saved.
      /// </summary>
      private interface INotPersisted
      {
      }

      /// <summary>
      /// A setting the server stores and never reads - see
      /// <see cref="SettingClaims"/> for how one is established, and
      /// WorkerThreadPriority for the one this exists for.
      ///
      /// The value is shown and cannot be changed. Both halves are deliberate. An
      /// editable control is the failure being fixed: an administrator raises a
      /// number, saves, gets no error, and believes something happened. Removing
      /// the row instead would hide a value somebody may well have set years ago
      /// and be relying on, and would leave them with no way to find out that it
      /// never did anything. The row also stays in the generated search index, so
      /// Ctrl+K still finds the setting by name and lands the reader on the note
      /// that explains it, rather than on nothing at all.
      /// </summary>
      private class ComInert : ComSetting, INotPersisted
      {
         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            var box = new TextBox
            {
               Text = Convert.ToString(value) ?? "",
               FontSize = 13,
               MaxWidth = 180,
               MinWidth = 120,
               HorizontalAlignment = HorizontalAlignment.Left,
               IsReadOnly = true
            };

            // Read-only rather than disabled: a disabled control is skipped by the
            // keyboard and, in most screen readers, not reachable at all - so
            // disabling it would hide the value and the explanation from precisely
            // the reader who cannot see the greyed-out styling either. Read-only
            // keeps it in the tab order and announced, and still refuses the edit.
            Describe(box, Path);

            panel.Children.Add(box);
            Annotate(box, panel);
            return panel;
         }

         public override object ReadEditor() => null;

         public override void Write(object owner, string property)
         {
         }
      }

      private class CardDef
      {
         public string Title;
         public string Blurb;
         public readonly List<ComSetting> Settings = new();
      }

      private class TabDef
      {
         public string Header;
         public readonly List<CardDef> Cards = new();
      }

      // ---- enum option tables ------------------------------------------------

      private static readonly (int, string)[] ConnSecurity =
      {
         (0, "None"), (1, "SSL/TLS"), (2, "STARTTLS (optional)"), (3, "STARTTLS (required)")
      };

      private static readonly (int, string)[] AntivirusAction =
      {
         (0, "Delete entire e-mail"), (1, "Delete infected attachments only")
      };

      private readonly Section section_;
      private List<TabDef> tabs_;
      private string diag_;
      private int failedReads_;
      private Action afterBuildUi_;

      // Most settings on these pages live in the COM settings tree, but a few
      // (log retention) live in hMailServer.ini; IniNumber rows use this store.
      private readonly IniFeatureStore iniStore_ = new IniFeatureStore();

      public ServerSettingsView(Section section)
      {
         InitializeComponent();
         section_ = section;
         BuildDefinition();
      }

      public void OnEnter() => BuildUi();

      public void OnLeave()
      {
      }

      // ---- definitions -------------------------------------------------------

      private TabDef Tab(string header)
      {
         var t = new TabDef { Header = header };
         tabs_.Add(t);
         return t;
      }

      private static CardDef Card(string title, string blurb = null)
         => new() { Title = title, Blurb = blurb };

      /// <summary>
      /// Adapts an option table from <see cref="SettingClaims"/> to the array
      /// <see cref="ComCombo"/> takes. The tables live there rather than here
      /// because the wording of an option is a claim about what the server can
      /// produce, and a claim buried in a view literal is one no test can see.
      /// </summary>
      private static (int Value, string Label)[] ToOptions(IReadOnlyList<(int Value, string Label)> options)
      {
         var result = new (int Value, string Label)[options.Count];
         for (int i = 0; i < options.Count; i++)
            result[i] = options[i];
         return result;
      }

      private void BuildDefinition()
      {
         tabs_ = new List<TabDef>();

         switch (section_)
         {
            case Section.Protocols: BuildProtocols(); break;
            case Section.Delivery: BuildDelivery(); break;
            case Section.AntiSpam: BuildAntiSpam(); break;
            case Section.AntiVirus: BuildAntiVirus(); break;
            case Section.Tls: BuildTls(); break;
            case Section.AutoBan: BuildAutoBan(); break;
            case Section.Logging: BuildLogging(); break;
            case Section.Performance: BuildPerformance(); break;
            case Section.Advanced: BuildAdvanced(); break;
            case Section.AdminAccess: BuildAdminAccess(); break;
         }
      }

      private void BuildProtocols()
      {
         TitleText.Text = "Protocols";
         SubtitleText.Text = "Which services this server runs, connection limits and greetings.";

         var services = Card("Services",
            "Enable or disable the protocol servers. Changes apply after pressing Save. " +
            "ManageSieve (for managing Sieve scripts) is enabled on the API & monitoring page; " +
            "OAuth2 token authentication is on the Authentication page.");
         services.Settings.Add(new ComBool { Path = "ServiceSMTP", Label = "SMTP server" });
         services.Settings.Add(new ComBool { Path = "ServiceIMAP", Label = "IMAP server" });
         services.Settings.Add(new ComBool { Path = "ServicePOP3", Label = "POP3 server" });
         Tab("Services").Cards.Add(services);

         var smtp = Card("SMTP");
         smtp.Settings.Add(new ComText { Path = "HostName", Label = "Host name (HELO/EHLO greeting)" });
         smtp.Settings.Add(new ComText { Path = "MaxSMTPConnections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         smtp.Settings.Add(new ComText { Path = "MaxMessageSize", Label = "Max message size (KB, 0 = unlimited)", Numeric = true });
         smtp.Settings.Add(new ComText { Path = "MaxSMTPRecipientsInBatch", Label = "Max recipients per message", Numeric = true });
         smtp.Settings.Add(new ComText { Path = "WelcomeSMTP", Label = "Welcome banner (empty = default)" });
         smtp.Settings.Add(new ComBool { Path = "AllowSMTPAuthPlain", Label = "Allow plain-text authentication (AUTH PLAIN/LOGIN)" });
         smtp.Settings.Add(new ComBool { Path = "DenyMailFromNull", Label = "Reject empty sender addresses (MAIL FROM:<>)" });
         smtp.Settings.Add(new ComBool { Path = "AllowIncorrectLineEndings", Label = "Allow incorrect line endings" });
         smtp.Settings.Add(new ComBool { Path = "DisconnectInvalidClients", Label = "Disconnect clients sending too many invalid commands" });
         smtp.Settings.Add(new ComText { Path = "MaxNumberOfInvalidCommands", Label = "Invalid command limit", Numeric = true });
         Tab("SMTP").Cards.Add(smtp);

         var imap = Card("IMAP");
         imap.Settings.Add(new ComText { Path = "MaxIMAPConnections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         imap.Settings.Add(new ComText { Path = "WelcomeIMAP", Label = "Welcome banner (empty = default)" });
         imap.Settings.Add(new ComBool { Path = "IMAPIdleEnabled", Label = "IDLE (push mail)" });
         imap.Settings.Add(new ComBool { Path = "IMAPQuotaEnabled", Label = "QUOTA" });
         imap.Settings.Add(new ComBool { Path = "IMAPSortEnabled", Label = "SORT" });
         imap.Settings.Add(new ComBool { Path = "IMAPACLEnabled", Label = "ACL (shared folder permissions)" });
         imap.Settings.Add(new ComBool { Path = "IMAPSASLPlainEnabled", Label = "Allow SASL PLAIN authentication" });
         imap.Settings.Add(new ComBool { Path = "IMAPSASLInitialResponseEnabled", Label = "Allow SASL initial client response" });
         imap.Settings.Add(new ComText { Path = "IMAPPublicFolderName", Label = "Public folder name" });
         imap.Settings.Add(new ComText { Path = "IMAPMasterUser", Label = "Master user (empty = disabled)" });
         imap.Settings.Add(new ComText { Path = "IMAPHierarchyDelimiter", Label = "Folder hierarchy delimiter" });
         Tab("IMAP").Cards.Add(imap);

         var pop3 = Card("POP3");
         pop3.Settings.Add(new ComText { Path = "MaxPOP3Connections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         pop3.Settings.Add(new ComText { Path = "WelcomePOP3", Label = "Welcome banner (empty = default)" });
         Tab("POP3").Cards.Add(pop3);
      }

      private void BuildDelivery()
      {
         TitleText.Text = "Delivery of e-mail";
         SubtitleText.Text = "Outbound delivery behavior, retries and smart-host relaying.";

         var del = Card("Delivery of e-mail",
            "Outbound delivery, retries and throttling. Keeping forwarded mail SPF-aligned (SRS) and " +
            "bounce tagging (BATV) are on the Advanced INI settings page.");
         del.Settings.Add(new ComText { Path = "SMTPNoOfTries", Label = "Number of delivery retries", Numeric = true });
         del.Settings.Add(new ComText { Path = "SMTPMinutesBetweenTry", Label = "Minutes between retries", Numeric = true });
         del.Settings.Add(new ComText { Path = "MaxNumberOfMXHosts", Label = "Max MX hosts to try (0 = all)", Numeric = true });
         del.Settings.Add(new ComText { Path = "SMTPDeliveryBindToIP", Label = "Bind outbound connections to IP (empty = any)" });
         del.Settings.Add(new ComCombo { Path = "SMTPConnectionSecurity", Label = "Outbound delivery security (after MX lookup)", Options = ConnSecurity });
         del.Settings.Add(new ComBool { Path = "AddDeliveredToHeader", Label = "Add Delivered-To header" });
         // Quick retries override "Minutes between retries" above for the first N
         // attempts, so the two have to be visible together to make sense.
         del.Settings.Add(new IniNumber { Path = "QuickRetries", Label = "Quick early retries before the normal schedule (0 = off)", Default = 0, IniStore = iniStore_ });
         del.Settings.Add(new IniNumber { Path = "QuickRetriesMinutes", Label = "Minutes between quick retries", Default = 6, IniStore = iniStore_ });
         del.Settings.Add(new IniNumber { Path = "QueueRandomnessMinutes", Label = "Random jitter added to retry times (minutes, 0 = off)", Default = 0, IniStore = iniStore_ });
         del.Settings.Add(new IniNumber
         {
            Path = "MaxOutboundPerDestinationPerMinute",
            Label = "Max outbound messages per destination domain per minute (0 = unlimited)",
            Default = 0,
            Blurb = "Throttling your own outbound rate to a domain that rate-limits you. Deferred messages consume the retry budget above.",
            IniStore = iniStore_
         });
         Tab("Delivery").Cards.Add(del);

         var relay = Card("SMTP relayer (smart host)",
            "Route all outbound mail through another SMTP server instead of delivering directly. " +
            "For failover, separate several hosts with a vertical bar: the next host is tried when one " +
            "cannot be reached (all hosts share the port, security and credentials below).");
         relay.Settings.Add(new ComText { Path = "SMTPRelayer", Label = "Relay host name (empty = direct delivery; use host1|host2 for failover)" });
         relay.Settings.Add(new ComText { Path = "SMTPRelayerPort", Label = "Port", Numeric = true });
         relay.Settings.Add(new ComCombo { Path = "SMTPRelayerConnectionSecurity", Label = "Connection security", Options = ConnSecurity });
         relay.Settings.Add(new ComBool { Path = "SMTPRelayerRequiresAuthentication", Label = "Relay requires authentication" });
         relay.Settings.Add(new ComText { Path = "SMTPRelayerUsername", Label = "User name" });
         relay.Settings.Add(new ComPassword { Path = "SetSMTPRelayerPassword", MethodName = "SetSMTPRelayerPassword", Label = "Password (leave empty to keep current)" });
         Tab("Relayer").Cards.Add(relay);

         var rules = Card("Rules");
         rules.Settings.Add(new ComText { Path = "RuleLoopLimit", Label = "Rule loop limit", Numeric = true });
         Tab("Rules").Cards.Add(rules);
      }

      private void BuildAntiSpam()
      {
         TitleText.Text = "Anti-spam";
         SubtitleText.Text = "Score-based spam filtering: SPF, DKIM, DMARC, host checks, greylisting and SpamAssassin.";

         var general = Card("Thresholds & actions");
         general.Settings.Add(new ComText { Path = "AntiSpam.SpamMarkThreshold", Label = "Spam mark threshold (score)", Numeric = true });
         general.Settings.Add(new ComText { Path = "AntiSpam.SpamDeleteThreshold", Label = "Spam delete threshold (score)", Numeric = true });
         general.Settings.Add(new ComBool { Path = "AntiSpam.AddHeaderSpam", Label = "Add X-hMailServer-Spam header" });
         general.Settings.Add(new ComBool { Path = "AntiSpam.AddHeaderReason", Label = "Add X-hMailServer-Reason header" });
         general.Settings.Add(new ComBool { Path = "AntiSpam.PrependSubject", Label = "Prepend text to subject" });
         general.Settings.Add(new ComText { Path = "AntiSpam.PrependSubjectText", Label = "Subject prefix" });
         general.Settings.Add(new ComText { Path = "AntiSpam.MaximumMessageSize", Label = "Max message size to spam-scan (KB, 0 = unlimited)", Numeric = true });
         Tab("General").Cards.Add(general);

         var auth = Card("Sender authentication");
         auth.Settings.Add(new ComBool { Path = "AntiSpam.UseSPF", Label = "Check SPF" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.UseSPFScore", Label = "SPF failure score", Numeric = true });
         auth.Settings.Add(new ComBool { Path = "AntiSpam.DKIMVerificationEnabled", Label = "Verify DKIM signatures" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.DKIMVerificationFailureScore", Label = "DKIM failure score", Numeric = true });
         auth.Settings.Add(new ComBool { Path = "AntiSpam.DMARCEnabled", Label = "Evaluate DMARC policies" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.DMARCFailureScore", Label = "DMARC failure score", Numeric = true });
         Tab("Sender auth").Cards.Add(auth);

         var host = Card("Connecting host checks");
         host.Settings.Add(new ComBool { Path = "AntiSpam.CheckHostInHelo", Label = "Check host in HELO" });
         host.Settings.Add(new ComText { Path = "AntiSpam.CheckHostInHeloScore", Label = "HELO check score", Numeric = true });
         host.Settings.Add(new ComBool { Path = "AntiSpam.CheckPTR", Label = "Check PTR record" });
         host.Settings.Add(new ComText { Path = "AntiSpam.CheckPTRScore", Label = "PTR check score", Numeric = true });
         host.Settings.Add(new ComBool { Path = "AntiSpam.UseMXChecks", Label = "Check sender MX records" });
         host.Settings.Add(new ComText { Path = "AntiSpam.UseMXChecksScore", Label = "MX check score", Numeric = true });
         Tab("Host checks").Cards.Add(host);

         var grey = Card("Greylisting", "Temporarily rejects mail from unknown senders; legitimate servers retry and pass.");
         grey.Settings.Add(new ComBool { Path = "AntiSpam.GreyListingEnabled", Label = "Enable greylisting" });
         grey.Settings.Add(new ComText { Path = "AntiSpam.GreyListingInitialDelay", Label = "Initial delay (minutes)", Numeric = true });
         grey.Settings.Add(new ComText { Path = "AntiSpam.GreyListingInitialDelete", Label = "Delete unconfirmed after (days)", Numeric = true, Divisor = 24 });
         grey.Settings.Add(new ComText { Path = "AntiSpam.GreyListingFinalDelete", Label = "Delete confirmed after (days)", Numeric = true, Divisor = 24 });
         grey.Settings.Add(new ComBool { Path = "AntiSpam.BypassGreylistingOnMailFromMX", Label = "Bypass when sender matches MX" });
         grey.Settings.Add(new ComBool { Path = "AntiSpam.BypassGreylistingOnSPFSuccess", Label = "Bypass on SPF success" });
         grey.Settings.Add(new ComAction
         {
            Path = "AntiSpam.GreyListingEnabled",
            ButtonText = "Clear greylisting triplets",
            Action = () =>
            {
               dynamic a = ServerSession.Current.Application.Settings.AntiSpam;
               try { a.ClearGreyListingTriplets(); return (true, "Greylisting triplets cleared."); }
               finally { ServerSession.Release((object) a); }
            }
         });
         Tab("Greylisting").Cards.Add(grey);

         var sa = Card("SpamAssassin");
         sa.Settings.Add(new ComBool { Path = "AntiSpam.SpamAssassinEnabled", Label = "Use SpamAssassin" });
         var saHost = new ComText { Path = "AntiSpam.SpamAssassinHost", Label = "Host" };
         var saPort = new ComText { Path = "AntiSpam.SpamAssassinPort", Label = "Port", Numeric = true };
         sa.Settings.Add(saHost);
         sa.Settings.Add(saPort);
         sa.Settings.Add(new ComBool { Path = "AntiSpam.SpamAssassinMergeScore", Label = "Merge SpamAssassin score into hMailServer score" });
         sa.Settings.Add(new ComText { Path = "AntiSpam.SpamAssassinScore", Label = "Score when not merging", Numeric = true });
         sa.Settings.Add(new ComAction
         {
            Path = "AntiSpam.SpamAssassinEnabled",
            ButtonText = "Test SpamAssassin connection",
            Action = () =>
            {
               string host = saHost.CurrentText;
               int.TryParse(saPort.CurrentText, out int port);
               return TestSpamAssassin(host, port);
            }
         });
         // Timeouts belong with the scanner they apply to: an admin whose mail is
         // backing up looks at the SpamAssassin tab, not at a hardening page.
         sa.Settings.Add(new IniNumber { Path = "SAMinTimeout", Label = "Minimum timeout (seconds)", Default = 30, IniStore = iniStore_ });
         sa.Settings.Add(new IniNumber
         {
            Path = "SAMaxTimeout",
            Label = "Maximum timeout (seconds)",
            Default = 90,
            Blurb = "The effective timeout moves between these values with server load.",
            IniStore = iniStore_
         });
         Tab("SpamAssassin").Cards.Add(sa);
      }

      private void BuildAntiVirus()
      {
         TitleText.Text = "Anti-virus";
         SubtitleText.Text = "Virus scanning and attachment blocking of received messages.";

         var general = Card("Action & notifications");
         general.Settings.Add(new ComCombo { Path = "AntiVirus.Action", Label = "When a virus is found", Options = AntivirusAction });
         general.Settings.Add(new ComBool { Path = "AntiVirus.NotifySender", Label = "Notify sender" });
         general.Settings.Add(new ComBool { Path = "AntiVirus.NotifyReceiver", Label = "Notify receiver" });
         general.Settings.Add(new ComText { Path = "AntiVirus.MaximumMessageSize", Label = "Max message size to virus-scan (KB, 0 = unlimited)", Numeric = true });
         general.Settings.Add(new ComBool { Path = "AntiVirus.EnableAttachmentBlocking", Label = "Enable attachment blocking (manage list on the Blocked attachments page)" });
         Tab("General").Cards.Add(general);

         var clamav = Card("ClamAV (network daemon)");
         clamav.Settings.Add(new ComBool { Path = "AntiVirus.ClamAVEnabled", Label = "Scan with clamd" });
         var clamHost = new ComText { Path = "AntiVirus.ClamAVHost", Label = "Host" };
         var clamPort = new ComText { Path = "AntiVirus.ClamAVPort", Label = "Port", Numeric = true };
         clamav.Settings.Add(clamHost);
         clamav.Settings.Add(clamPort);
         clamav.Settings.Add(new ComAction
         {
            ButtonText = "Test ClamAV connection",
            Action = () => TestClamAv(clamHost.CurrentText, ParsePort(clamPort.CurrentText))
         });
         // See the SpamAssassin tab: scanner timeouts live with their scanner.
         clamav.Settings.Add(new IniNumber { Path = "ClamMinTimeout", Label = "Minimum timeout (seconds)", Default = 15, IniStore = iniStore_ });
         clamav.Settings.Add(new IniNumber
         {
            Path = "ClamMaxTimeout",
            Label = "Maximum timeout (seconds)",
            Default = 90,
            Blurb = "The effective timeout moves between these values with server load.",
            IniStore = iniStore_
         });
         Tab("ClamAV").Cards.Add(clamav);

         var clamwin = Card("ClamWin (local executable)");
         clamwin.Settings.Add(new ComBool { Path = "AntiVirus.ClamWinEnabled", Label = "Scan with ClamWin" });
         var clamWinExe = new ComText { Path = "AntiVirus.ClamWinExecutable", Label = "clamscan.exe path", BrowseFile = true, FileFilter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
         var clamWinDb = new ComText { Path = "AntiVirus.ClamWinDBFolder", Label = "Database folder", BrowseFolder = true };
         clamwin.Settings.Add(clamWinExe);
         clamwin.Settings.Add(clamWinDb);
         clamwin.Settings.Add(new ComAction
         {
            ButtonText = "Auto-detect ClamWin",
            Action = () => AutoDetectClamWin(clamWinExe, clamWinDb)
         });
         clamwin.Settings.Add(new ComAction
         {
            ButtonText = "Test ClamWin scanner",
            Action = () => TestClamWin(clamWinExe.CurrentText, clamWinDb.CurrentText)
         });
         Tab("ClamWin").Cards.Add(clamwin);

         var custom = Card("Custom scanner", "Run an external command; a configured return value indicates an infected message. Use %FILE% where the file to scan should be passed on the command line. For engines without a CLI (HTTP/API, DLP, SIEM), use an OnAcceptMessage handler on the Event scripts page instead.");
         custom.Settings.Add(new ComBool { Path = "AntiVirus.CustomScannerEnabled", Label = "Use a custom virus scanner" });
         var customExe = new ComText { Path = "AntiVirus.CustomScannerExecutable", Label = "Command line (use %FILE% for the scanned file)" };
         var customReturn = new ComText { Path = "AntiVirus.CustomScannerReturnValue", Label = "Return value for infected", Numeric = true };
         custom.Settings.Add(new ComPreset
         {
            Label = "Preset engine (fills the command line and return value below \u2013 adjust the path/exit code for your version)",
            ExeTarget = customExe,
            ReturnTarget = customReturn,
            Presets = CustomScannerPresets
         });
         custom.Settings.Add(customExe);
         custom.Settings.Add(customReturn);
         custom.Settings.Add(new ComAction
         {
            ButtonText = "Test custom scanner",
            Action = () => TestCustomScanner(customExe.CurrentText)
         });
         Tab("Custom").Cards.Add(custom);
      }

      private void BuildTls()
      {
         TitleText.Text = "SSL/TLS";
         SubtitleText.Text = "Which TLS versions and ciphers this server negotiates, for its own listeners and " +
                             "for the connections it makes when delivering. Certificates are on the SSL certificates " +
                             "page; DANE and MTA-STS are on Transport security; brute-force lockout is on Auto-ban.";

         var ver = Card("Protocol versions", "TLS 1.2 and 1.3 are the recommended baseline; older versions exist only for legacy clients.");
         var tls10 = new ComBool { Path = "TlsVersion10Enabled", Label = "TLS 1.0 (legacy)" };
         var tls11 = new ComBool { Path = "TlsVersion11Enabled", Label = "TLS 1.1 (legacy)" };
         var tls12 = new ComBool { Path = "TlsVersion12Enabled", Label = "TLS 1.2" };
         var tls13 = new ComBool { Path = "TlsVersion13Enabled", Label = "TLS 1.3" };
         ver.Settings.Add(tls10);
         ver.Settings.Add(tls11);
         ver.Settings.Add(tls12);
         ver.Settings.Add(tls13);
         Tab("Protocol versions").Cards.Add(ver);

         var ciph = Card("Ciphers & verification");
         var preferServer = new ComBool { Path = "TlsOptionPreferServerCiphersEnabled", Label = "Prefer server cipher order" };
         var chacha = new ComBool { Path = "TlsOptionPrioritizeChaChaEnabled", Label = "Prioritize ChaCha20-Poly1305 when the client prefers it (needs TLS 1.2 or 1.3)" };
         ciph.Settings.Add(new ComText { Path = "SslCipherList", Label = "Cipher list (OpenSSL format)" });
         ciph.Settings.Add(preferServer);
         ciph.Settings.Add(chacha);
         ciph.Settings.Add(new ComBool { Path = "VerifyRemoteSslCertificate", Label = "Verify remote certificates when delivering" });
         Tab("Ciphers").Cards.Add(ciph);

         // ChaCha prioritization only takes effect when the server chooses the
         // cipher order and a modern TLS version is enabled. Reflect that
         // dependency live in the UI instead of letting it silently no-op.
         afterBuildUi_ = () => WireChaChaDependency(preferServer, chacha, tls12, tls13);
      }

      /// <summary>
      /// Brute-force lockout, which used to be the third tab of the SSL/TLS page.
      ///
      /// The two subjects shared a page and nothing else: you arrive at one of them
      /// because somebody is guessing passwords and at the other because a client
      /// cannot negotiate a cipher, and whichever you came for, half the page was
      /// noise. The roadmap's words for the old title were "an admission that the
      /// page is a bucket".
      ///
      /// The three notes below are the reason this is worth more than a move. Every
      /// one of them is a way the feature silently does nothing, and none of them
      /// was visible anywhere in the interface:
      ///
      ///   - AccountLogon::RegisterFailedLogin returns immediately when
      ///     GetAutoBanLogonEnabled() is false OR MaxInvalidLogonAttempts is 0, so
      ///     a limit of zero turns auto-ban off with the switch still on;
      ///   - with AutoBanMinutes at 0 it disconnects the client and creates no
      ///     range at all, so nothing is blocked;
      ///   - the "within" value is not a sliding window evaluated at logon time. It
      ///     is how long a failure record survives: RemoveExpiredRecords (every
      ///     minute) deletes failures older than it, and only while auto-ban is
      ///     enabled.
      /// </summary>
      private void BuildAutoBan()
      {
         TitleText.Text = "Auto-ban";
         SubtitleText.Text = "Automatic lockout of an address that keeps failing to log on. " +
                             "The ban itself is an expiring IP range, so it is listed on the IP ranges page.";

         var ban = Card("Auto-ban",
            "Counted per connecting IP address across every protocol that authenticates - SMTP AUTH, POP3, IMAP, " +
            "ManageSieve and the REST API all feed the same counter. On reaching the limit the server clears that " +
            "address's counted failures and creates an IP range named \"Auto-ban: <user>\" at priority 100 covering " +
            "that one address, which expires on its own.");
         ban.Settings.Add(new ComBool
         {
            Path = "AutoBanOnLogonFailure",
            Label = "Enable auto-ban",
            Blurb = "Also turned off by a limit of 0 below, whatever this box says."
         });
         ban.Settings.Add(new ComText
         {
            Path = "MaxInvalidLogonAttempts",
            Label = "Max invalid logon attempts",
            Numeric = true,
            Blurb = "0 disables auto-ban entirely, even with the box above ticked."
         });
         ban.Settings.Add(new ComText
         {
            Path = "MaxInvalidLogonAttemptsWithin",
            Label = "...within (minutes)",
            Numeric = true,
            Blurb = "How long a counted failure is kept, rather than a sliding window: a housekeeping pass running " +
                    "once a minute deletes failure records older than this - and only while auto-ban is enabled."
         });
         ban.Settings.Add(new ComText
         {
            Path = "AutoBanMinutes",
            Label = "Ban duration (minutes)",
            Numeric = true,
            Blurb = "0 means the connection is dropped but no range is created, so the address is not actually banned."
         });
         ban.Settings.Add(new ComAction
         {
            Path = "AutoBanOnLogonFailure",
            ButtonText = "Clear logon-failure list",
            Action = () =>
            {
               dynamic s = ServerSession.Current.Application.Settings;
               try
               {
                  s.ClearLogonFailureList();
                  // Deliberately explicit about what this does NOT do. It calls
                  // Configuration::ClearOldLogonFailures, i.e. ClearOldFailures(-1)
                  // on the failure table; the ban is a separate IP range and is
                  // untouched. Somebody pressing this to release a locked-out user
                  // and getting "cleared" would otherwise reasonably conclude the
                  // lockout was lifted.
                  return (true, "Counted logon failures cleared. Addresses already banned stay banned until their " +
                                "\"Auto-ban:\" range expires - delete it on the IP ranges page to release one now.");
               }
               finally { ServerSession.Release((object) s); }
            }
         });
         Tab("Auto-ban").Cards.Add(ban);
      }

      private void BuildLogging()
      {
         TitleText.Text = "Logging";
         SubtitleText.Text = "What the server writes to its log files (viewable on the Live logs page).";

         // One tab holding the three cards: calling Tab() again would add a second
         // tab with the same header rather than reuse this one.
         TabDef logging = Tab("Logging");

         var log = Card("Log categories");
         log.Settings.Add(new ComBool { Path = "Logging.Enabled", Label = "Logging enabled" });
         log.Settings.Add(new ComBool { Path = "Logging.LogApplication", Label = "Application events" });
         log.Settings.Add(new ComBool { Path = "Logging.LogSMTP", Label = "SMTP conversations" });
         log.Settings.Add(new ComBool { Path = "Logging.LogIMAP", Label = "IMAP conversations" });
         log.Settings.Add(new ComBool { Path = "Logging.LogPOP3", Label = "POP3 conversations" });
         log.Settings.Add(new ComBool { Path = "Logging.LogTCPIP", Label = "TCP/IP activity" });
         log.Settings.Add(new ComBool { Path = "Logging.LogDebug", Label = "Debug messages" });
         log.Settings.Add(new ComBool { Path = "Logging.AWStatsEnabled", Label = "AWStats-compatible log" });
         log.Settings.Add(new ComBool { Path = "Logging.KeepFilesOpen", Label = "Keep log files open (performance)" });
         log.Settings.Add(new ComCombo
         {
            Path = "Logging.Device",
            Label = "Log destination",
            Options = ToOptions(SettingClaims.LogDeviceOptions),
            Blurb = SettingClaims.NoteFor("Logging.Device")
         });

         // The two log-rendering controls are wired together below, because the
         // server has a precedence between them that nothing in the interface used
         // to show: Logger::Render_ tests the line-format setting first, so choosing
         // NCSA makes the JSON switch inert. Two ways of formatting a log line on
         // one card, one of which silently wins, is the same defect class as a
         // setting that does nothing at all.
         //
         // The option wording itself lives in SettingClaims, and the claim the old
         // wording made is pinned there by a test that scans this file for it.
         var format = new ComCombo
         {
            Path = "Logging.LogFormat",
            Label = "Log line format",
            Options = ToOptions(SettingClaims.LogFormatOptions),
            Blurb = SettingClaims.NoteFor("Logging.LogFormat")
         };
         var json = new IniBool
         {
            Path = "JsonLogging",
            Label = "Write logs as JSON lines",
            Default = false,
            Blurb = "Machine-readable output for log shippers. Applies after a service restart.",
            IniStore = iniStore_
         };
         log.Settings.Add(format);
         log.Settings.Add(json);
         afterBuildUi_ = () => WireLogFormatDependency(format, json);
         logging.Cards.Add(log);

         // How much is written, next to what is written and how long it is kept -
         // an admin dealing with log volume should not have to find three pages.
         var detail = Card("Log detail",
            "How much detail each entry carries. Lowering the level is the other half of controlling log volume.");
         detail.Settings.Add(new IniNumber
         {
            Path = "LogLevel",
            Label = "Log level (3 or above = full detail; 2 or lower = quieter)",
            Default = 9,
            Blurb = "There is only one step, at 2. From 3 upwards (the default is 9) every enabled category is logged " +
                    "in full. At 2 or lower the server drops IMAP FETCH and STATUS responses from the log and shortens " +
                    "lines longer than the limit below to their first and last characters. Turning on Debug messages " +
                    "above restores full detail whatever the level.",
            IniStore = iniStore_
         });
         detail.Settings.Add(new IniNumber
         {
            Path = "MaxLogLineLen",
            Label = "Maximum characters per log line (minimum 100; only applied at log level 2 or lower)",
            Default = 500,
            IniStore = iniStore_
         });
         detail.Settings.Add(new IniBool
         {
            Path = "SepSvcLogs",
            Label = "Write a separate log file per service component",
            Default = false,
            IniStore = iniStore_
         });
         logging.Cards.Add(detail);

         var retention = Card("Log retention",
            "Housekeeping for the log folder, so logs do not accumulate indefinitely.");
         retention.Settings.Add(new IniNumber
         {
            Path = "LogDeleteDays",
            Label = "Delete logs older than N days (0 = keep everything)",
            Default = 0,
            Blurb = "Checked shortly after the service starts and every six hours after that. " +
                    "Only hMailServer's own dated log files (hmailserver_*.log and ERROR_hmailserver_*.log) " +
                    "are removed; nothing else in the log folder is touched.",
            IniStore = iniStore_
         });
         logging.Cards.Add(retention);

         // The classic Administrator's Logging pane showed the log directory and gave
         // you a button to open it. The Control Panel had neither, so the first
         // question anybody asks on this page - "where are these files?" - could only
         // be answered by opening hMailServer.INI by hand, and the live-logs page
         // shows contents rather than a location.
         //
         // The path is read rather than assumed, and the blurb says so when it cannot
         // be: the Control Panel can be run from a different machine to the server, in
         // which case there is no INI to read and a button that opened "the log folder"
         // would open the wrong machine's idea of one.
         string logFolder = iniStore_ != null && iniStore_.IsAvailable
            ? iniStore_.GetLogFolder()
            : null;

         var logFiles = Card("Log files",
            "Where the server writes the logs configured above.");

         logFiles.Settings.Add(new ComAction
         {
            Label = "Log folder",
            ButtonText = "Open log folder",
            Blurb = string.IsNullOrWhiteSpace(logFolder)
               ? "Read from [Directories] LogFolder in hMailServer.INI, which is not readable from this machine - " +
                 "the Control Panel is running somewhere other than the server."
               : logFolder,
            Action = () => OpenLogFolder(logFolder)
         });

         logging.Cards.Add(logFiles);
      }

      /// <summary>
      /// Opens the log folder in Explorer, and says why it could not rather than
      /// failing silently - the two ordinary reasons are a Control Panel running away
      /// from the server, and a LogFolder that names a directory which is not there.
      /// </summary>
      private static (bool ok, string text) OpenLogFolder(string path)
      {
         if (string.IsNullOrWhiteSpace(path))
            return (false, "No log folder could be read from hMailServer.INI on this machine.");

         if (!System.IO.Directory.Exists(path))
            return (false, "The configured log folder does not exist on this machine: " + path);

         try
         {
            using var process = System.Diagnostics.Process.Start(
               new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

            return (true, "Opened " + path);
         }
         catch (Exception ex)
         {
            return (false, "Could not open the folder: " + ex.Message);
         }
      }

      private void BuildPerformance()
      {
         TitleText.Text = "Performance";
         SubtitleText.Text = "Thread pools, in-memory caches and message indexing.";

         var threads = Card("Threads", "Thread pool sizing. Defaults suit most installations; raise for very busy servers.");
         threads.Settings.Add(new ComText { Path = "MaxDeliveryThreads", Label = "Max delivery threads", Numeric = true });
         threads.Settings.Add(new ComText { Path = "MaxAsynchronousThreads", Label = "Max asynchronous task threads", Numeric = true });
         threads.Settings.Add(new ComText { Path = "TCPIPThreads", Label = "TCP/IP threads", Numeric = true });

         // Not a NumberBox any more. The server writes this value to the settings
         // table and reads it back only through the COM getter: there is no
         // SetThreadPriority call anywhere in the tree, so nothing has ever acted on
         // it. The label says so as well as the note, because the label is what the
         // Ctrl+K palette shows.
         threads.Settings.Add(new ComInert
         {
            Path = "WorkerThreadPriority",
            Label = "Worker thread priority (stored, but the server does not use it)",
            Blurb = SettingClaims.NoteFor("WorkerThreadPriority")
         });
         Tab("Threads").Cards.Add(threads);

         var cache = Card("Cache", "Caches domain/account/alias lookups in memory to reduce database round-trips. TTL in seconds.");
         cache.Settings.Add(new ComBool { Path = "Cache.Enabled", Label = "Enable caching" });
         cache.Settings.Add(new ComText { Path = "Cache.DomainCacheTTL", Label = "Domain cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.AccountCacheTTL", Label = "Account cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.AliasCacheTTL", Label = "Alias cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.DistributionListCacheTTL", Label = "Distribution-list cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.DomainCacheMaxSizeKb", Label = "Domain cache max size (KB)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.AccountCacheMaxSizeKb", Label = "Account cache max size (KB)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.AliasCacheMaxSizeKb", Label = "Alias cache max size (KB)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.DistributionListCacheMaxSizeKb", Label = "Distribution-list cache max size (KB)", Numeric = true });
         Tab("Cache").Cards.Add(cache);

         var index = Card("Message indexing", "Builds a search index so IMAP SEARCH and the web client are faster.");
         index.Settings.Add(new ComBool { Path = "MessageIndexing.Enabled", Label = "Enable message indexing" });
         // Tuning lives with the feature it tunes: a tab holding a single on/off
         // switch reads as "there is nothing to adjust here".
         index.Settings.Add(new IniNumber { Path = "IndexerFullMinutes", Label = "Full re-index interval (minutes)", Default = 720, IniStore = iniStore_ });
         index.Settings.Add(new IniNumber { Path = "IndexerFullLimit", Label = "Messages per full-index pass", Default = 25000, IniStore = iniStore_ });
         index.Settings.Add(new IniNumber { Path = "IndexerQuickLimit", Label = "Messages per quick-index pass", Default = 1000, IniStore = iniStore_ });
         Tab("Indexing").Cards.Add(index);
      }

      private void BuildAdvanced()
      {
         TitleText.Text = "Advanced";
         SubtitleText.Text = "Server-wide defaults, keeping copies of mail and the scripting engine.";

         var general = Card("General",
            "The administrator password and two-factor authentication are on the Administrative access page.");
         general.Settings.Add(new ComText { Path = "DefaultDomain", Label = "Default domain (for unqualified logons)" });
         general.Settings.Add(new ComBool { Path = "IPv6PreferredEnabled", Label = "Prefer IPv6 when delivering" });

         // The old label said "legacy COM admin tools", which was closer to the
         // truth than most of this page but still let an administrator believe it
         // would translate something they can see. It cannot: the server hands the
         // value straight back over COM and translates nothing itself, and this
         // Control Panel has no resx at all, so the only consumer is a third-party
         // tool. Left editable rather than made inert for exactly that reason - a
         // script may be reading it.
         general.Settings.Add(new ComText
         {
            Path = "UserInterfaceLanguage",
            Label = "Administrator UI language (third-party COM tools only)",
            Blurb = SettingClaims.NoteFor("UserInterfaceLanguage")
         });
         Tab("General").Cards.Add(general);

         // Mirroring and archiving are the two ways of keeping a copy of every
         // message, so they belong on one tab. Archiving lived on the catch-all
         // INI page, where an admin looking for "keep a copy" never found it.
         TabDef copies = Tab("Copies of mail");

         var mirror = Card("Mirroring", "Sends a copy of every message passing through the server to one address (compliance archiving).");
         mirror.Settings.Add(new ComText { Path = "MirrorEMailAddress", Label = "Mirror address (empty = disabled)" });
         copies.Cards.Add(mirror);

         var archive = Card("Message archiving",
            "Keeps a copy of every message received over SMTP in a folder tree, in addition to delivering it as " +
            "normal - it does not divert or hold back mail. Mail from a local sender is filed under " +
            "<domain>\\<mailbox>, mail from elsewhere under Inbound, and messages with no envelope sender " +
            "(bounces and delivery reports) under Error, plus a copy in the folder of each local recipient.");
         archive.Settings.Add(new IniText
         {
            Path = "ArchiveDir",
            Label = "Archive folder (empty = archiving off)",
            Placeholder = @"D:\MailArchive",
            BrowseFolder = true,
            IniStore = iniStore_
         });
         archive.Settings.Add(new IniBool
         {
            Path = "ArchiveHardLinks",
            Label = "Hard-link each recipient's copy instead of copying it",
            Default = false,
            Blurb = "Every local recipient's copy becomes another name for the same file inside the archive folder, " +
                    "so a message to ten mailboxes costs one copy rather than ten. Needs an NTFS archive folder; " +
                    "if the link cannot be created the server copies the file and says so in the SMTP log.",
            IniStore = iniStore_
         });
         copies.Cards.Add(archive);

         var script = Card("Scripting", "Runs event scripts (OnAcceptMessage, OnDeliveryStart...) from the Events folder. The script engine reloads when you save.");
         script.Settings.Add(new ComBool { Path = "Scripting.Enabled", Label = "Enable server-side event scripts" });
         script.Settings.Add(new ComText { Path = "Scripting.Language", Label = "Language (VBScript or JScript)" });
         Tab("Scripting").Cards.Add(script);
      }

      /// <summary>
      /// Who may administer this server. Both settings existed but had no
      /// reachable home: the password was buried at the bottom of the Advanced
      /// page, and two-factor setup could only be opened from the logon screen,
      /// so nobody already signed in could find either.
      /// </summary>
      private void BuildAdminAccess()
      {
         TitleText.Text = "Administrative access";
         SubtitleText.Text = "The credentials used to administer this server.";

         var password = Card("Administrator password",
            "The main hMailServer administration password. It is used by this Control Panel, the REST API and any " +
            "script that connects through the COM API, and is stored hashed in hMailServer.ini. Changing it does not " +
            "affect mailbox passwords.");
         password.Settings.Add(new ComPassword
         {
            Path = "SetAdministratorPassword",
            Label = "New administrator password (leave empty to keep the current one)",
            MethodName = "SetAdministratorPassword"
         });
         Tab("Password").Cards.Add(password);

         var twoFactor = Card("Two-factor authentication",
            "An optional one-time code, asked for after the password when signing in to this Control Panel. The secret " +
            "is stored per machine under HKLM, so enabling or disabling it needs local administrator rights. It does " +
            "not apply to the REST API or to scripts using the COM API.");
         twoFactor.Settings.Add(new ComAction
         {
            ButtonText = "Set up or turn off two-factor authentication\u2026",
            Action = () =>
            {
               new TotpSetupDialog(Application.Current?.MainWindow).ShowDialog();
               return TotpManager.IsConfigured()
                  ? (true, "Two-factor authentication is on for this Control Panel.")
                  : (false, "Two-factor authentication is off — only the password is required.");
            }
         });
         Tab("Two-factor").Cards.Add(twoFactor);
      }

      // ---- COM resolution ----------------------------------------------------

      private static object ResolveOwner(string path, out string property)
      {
         dynamic current = ServerSession.Current.Application.Settings;
         string[] parts = path.Split('.');
         for (int i = 0; i < parts.Length - 1; i++)
            current = GetProperty(current, parts[i]);
         property = parts[^1];
         return current;
      }

      private static object GetProperty(object owner, string name)
         => owner.GetType().InvokeMember(name, BindingFlags.GetProperty, null, owner, null);

      private static void SetProperty(object owner, string name, object value)
         => owner.GetType().InvokeMember(name, BindingFlags.SetProperty, null, owner, new[] { value });

      // ---- UI ----------------------------------------------------------------

      /// <summary>
      /// Works out what a screen reader should call each editor, before any of
      /// them are built.
      ///
      /// It has to be done for the whole page at once, which is why it is not
      /// simply part of CreateEditor: on the Protocols page "Max simultaneous
      /// connections (0 = unlimited)" and "Welcome banner (empty = default)" each
      /// appear three times, once per protocol card, and only a pass over the page
      /// can tell which labels need qualifying with their card title.
      /// <see cref="AccessibleNames"/> holds the decision.
      /// </summary>
      private void AssignAccessibleNames()
      {
         var settings = new List<ComSetting>();
         var editors = new List<LabelledEditor>();

         foreach (TabDef tab in tabs_)
         foreach (CardDef card in tab.Cards)
         foreach (ComSetting setting in card.Settings)
         {
            settings.Add(setting);
            editors.Add(new LabelledEditor(setting.Label, card.Title, setting.Path));
         }

         IReadOnlyList<string> names = AccessibleNames.Resolve(editors);
         for (int i = 0; i < settings.Count; i++)
            settings[i].AccessibleName = names[i];
      }

      private void BuildUi()
      {
         SettingsTabs.Items.Clear();
         diag_ = null;
         failedReads_ = 0;

         AssignAccessibleNames();

         foreach (TabDef tab in tabs_)
         {
            // Cap the settings form to a readable column instead of letting cards
            // stretch the full window width (which left short, left-aligned inputs
            // floating in empty space).
            var panel = new StackPanel { Margin = new Thickness(2, 4, 14, 4), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

            foreach (CardDef card in tab.Cards)
            {
               var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
               border.SetResourceReference(StyleProperty, "Card");

               var inner = new StackPanel();
               inner.Children.Add(new TextBlock
               {
                  Text = card.Title,
                  FontSize = Typography.SectionHeading,
                  FontWeight = FontWeights.SemiBold,
                  Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(card.Blurb) ? 12 : 4)
               });
               if (!string.IsNullOrEmpty(card.Blurb))
               {
                  inner.Children.Add(new TextBlock
                  {
                     Text = card.Blurb,
                     FontSize = Typography.Caption,
                     TextWrapping = TextWrapping.Wrap,
                     Opacity = 0.65,
                     Margin = new Thickness(0, 0, 0, 14)
                  });
               }

               FrameworkElement lastEditor = null;
               foreach (ComSetting setting in card.Settings)
               {
                  object value = null;
                  if (setting.WantsInitialValue)
                  {
                     try
                     {
                        object owner = ResolveOwner(setting.Path, out string property);
                        value = GetProperty(owner, property);
                     }
                     catch (Exception ex)
                     {
                        failedReads_++;
                        diag_ ??= ServerSession.DescribeComError(ex);
                        continue;   // value could not be read; skip this editor
                     }
                  }

                  FrameworkElement editor = setting.CreateEditor(value);
                  editor.Margin = new Thickness(0, 0, 0, 12);
                  inner.Children.Add(editor);
                  lastEditor = editor;
               }

               if (lastEditor != null)
                  lastEditor.Margin = new Thickness(0, 0, 0, 2);

               border.Child = inner;
               panel.Children.Add(border);
            }

            var scroll = new ScrollViewer
            {
               VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
               Content = panel
            };
            SettingsTabs.Items.Add(new TabItem { Header = tab.Header, Content = scroll });
         }

         if (SettingsTabs.Items.Count > 0)
            SettingsTabs.SelectedIndex = 0;

         StatusText.Text = failedReads_ == 0
            ? "Values read from the server."
            : failedReads_ + " setting(s) could not be read — " + diag_;

         afterBuildUi_?.Invoke();
      }

      // ---- live test / dependency helpers ------------------------------------

      private static (bool ok, string text) TestSpamAssassin(string host, int port)
      {
         if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return (false, "Enter a host name and port first.");

         dynamic antispam = ServerSession.Current.Application.Settings.AntiSpam;
         try
         {
            object[] args = { host, port, "" };
            object ret = ((object) antispam).GetType().InvokeMember(
               "TestSpamAssassinConnection", BindingFlags.InvokeMethod, null, (object) antispam, args);
            bool ok = ret is bool b && b;
            string msg = args.Length > 2 ? args[2] as string : null;
            if (string.IsNullOrEmpty(msg))
               msg = ok ? "Connection succeeded." : "Connection failed.";
            return (ok, msg);
         }
         finally
         {
            ServerSession.Release((object) antispam);
         }
      }

      // ---- anti-virus scanner test / preset helpers --------------------------

      // Common single-engine custom-scanner command lines. Exit codes vary by
      // product version, so the preset is a starting point the admin can adjust.
      private static readonly (string Name, string Exe, int ReturnValue)[] CustomScannerPresets =
      {
         ("Microsoft Defender (MpCmdRun)",
            "\"C:\\Program Files\\Windows Defender\\MpCmdRun.exe\" -Scan -ScanType 3 -File \"%FILE%\" -DisableRemediation", 2),
         ("Sophos (savscan)",
            "\"C:\\Program Files\\Sophos\\Sophos Anti-Virus\\savscan.exe\" -ss -archive \"%FILE%\"", 3),
         ("ESET (ecls)",
            "\"C:\\Program Files\\ESET\\ESET Security\\ecls.exe\" \"%FILE%\"", 50),
         ("Bitdefender (bdscan)",
            "\"C:\\Program Files\\Bitdefender\\Endpoint Security\\bdscan.exe\" \"%FILE%\"", 1),
         ("Kaspersky (avp.com)",
            "\"C:\\Program Files (x86)\\Kaspersky Lab\\Kaspersky Endpoint Security\\avp.com\" SCAN \"%FILE%\"", 2),
      };

      private static int ParsePort(string text) => int.TryParse(text, out int p) ? p : 0;

      private static (bool ok, string text) TestClamAv(string host, int port)
      {
         if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return (false, "Enter a host name and port first.");

         dynamic av = ServerSession.Current.Application.Settings.AntiVirus;
         try
         {
            object[] args = { host, port, "" };
            object ret = ((object) av).GetType().InvokeMember(
               "TestClamAVScanner", BindingFlags.InvokeMethod, null, (object) av, args);
            bool ok = ret is bool b && b;
            string msg = args.Length > 2 ? args[2] as string : null;
            if (string.IsNullOrEmpty(msg))
               msg = ok ? "Connection succeeded." : "Connection failed.";
            return (ok, msg);
         }
         finally
         {
            ServerSession.Release((object) av);
         }
      }

      private static (bool ok, string text) TestClamWin(string executable, string database)
      {
         if (string.IsNullOrWhiteSpace(executable))
            return (false, "Enter the clamscan.exe path first.");

         dynamic av = ServerSession.Current.Application.Settings.AntiVirus;
         try
         {
            object[] args = { executable, database ?? "", "" };
            object ret = ((object) av).GetType().InvokeMember(
               "TestClamWinScanner", BindingFlags.InvokeMethod, null, (object) av, args);
            bool ok = ret is bool b && b;
            string msg = args.Length > 2 ? args[2] as string : null;
            if (string.IsNullOrEmpty(msg))
               msg = ok ? "Scanner test succeeded." : "Scanner test failed.";
            return (ok, msg);
         }
         finally
         {
            ServerSession.Release((object) av);
         }
      }

      private static (bool ok, string text) TestCustomScanner(string command)
      {
         if (string.IsNullOrWhiteSpace(command))
            return (false, "Enter the scanner command first.");

         string path = ExtractProgramPath(command);
         if (string.IsNullOrEmpty(path))
            return (false, "Couldn't determine the executable from the command.");

         if (System.IO.File.Exists(path))
            return (true, "Executable found: " + path);

         return (false, "Executable not found: " + path);
      }

      // Pulls the program path out of a command line that may be quoted and carry
      // arguments / the %FILE% macro.
      private static string ExtractProgramPath(string command)
      {
         command = command.Trim();
         if (command.StartsWith("\""))
         {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : "";
         }
         int space = command.IndexOf(' ');
         return space < 0 ? command : command.Substring(0, space);
      }

      private static (bool ok, string text) AutoDetectClamWin(ComText exeField, ComText dbField)
      {
         string[] exeCandidates =
         {
            @"C:\Program Files\ClamWin\bin\clamscan.exe",
            @"C:\Program Files (x86)\ClamWin\bin\clamscan.exe",
         };

         foreach (string candidate in exeCandidates.Where(System.IO.File.Exists))
         {
            exeField.SetText(candidate);
            string db = FindClamWinDatabase();
            if (!string.IsNullOrEmpty(db))
               dbField.SetText(db);
            return (true, "Found ClamWin at " + candidate +
               (string.IsNullOrEmpty(db) ? "" : "; database folder " + db));
         }

         return (false, "ClamWin was not found in the standard install locations.");
      }

      private static string FindClamWinDatabase()
      {
         string[] candidates =
         {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"ClamWin\db"),
            @"C:\ProgramData\ClamWin\db",
         };

         return candidates.FirstOrDefault(System.IO.Directory.Exists) ?? "";
      }


      /// <summary>
      /// Makes the log-format precedence visible.
      ///
      /// Logger::Render_ tests the NCSA format before it looks at JsonLogging, so
      /// with NCSA selected the JSON switch does nothing - and until now the
      /// Logging page offered both, side by side, with no hint that one silently
      /// beat the other. That is the same defect as a setting the server ignores
      /// entirely: the administrator ticks a box, gets no error, and does not get
      /// JSON logs.
      ///
      /// The checkbox is disabled rather than cleared, so the stored INI value is
      /// left alone: a disabled CheckBox keeps its IsChecked state, SaveToIni
      /// writes that state back unchanged, and switching the format away from NCSA
      /// restores the JSON logging the administrator had configured. Clearing it
      /// would silently discard a setting on a page visit.
      /// </summary>
      private static void WireLogFormatDependency(ComCombo format, IniBool json)
      {
         if (format?.Combo == null || json?.Box == null)
            return;

         void Update()
         {
            bool overridden = format.SelectedValue == SettingClaims.LogFormatNcsa;

            json.Box.IsEnabled = !overridden;
            json.Box.ToolTip = overridden ? SettingClaims.JsonOverriddenByNcsa : null;

            // Kept on the checkbox itself as well as in the tool tip: a tool tip
            // needs a hover, and a disabled control that gives no reason for being
            // disabled is indistinguishable from a broken page.
            System.Windows.Automation.AutomationProperties.SetHelpText(json.Box,
               overridden ? SettingClaims.JsonOverriddenByNcsa : "");
         }

         format.Combo.SelectionChanged += (s, e) => Update();
         Update();
      }

      private static void WireChaChaDependency(ComBool preferServer, ComBool chacha, ComBool tls12, ComBool tls13)
      {
         if (preferServer?.Box == null || chacha?.Box == null || tls12?.Box == null || tls13?.Box == null)
            return;

         void Update()
         {
            bool eligible = preferServer.Box.IsChecked == true
               && (tls12.Box.IsChecked == true || tls13.Box.IsChecked == true);
            chacha.Box.IsEnabled = eligible;
            chacha.Box.ToolTip = eligible
               ? null
               : "Requires 'Prefer server cipher order' and TLS 1.2 or 1.3 to be enabled.";
         }

         void Handler(object s, RoutedEventArgs e) => Update();
         preferServer.Box.Checked += Handler;
         preferServer.Box.Unchecked += Handler;
         tls12.Box.Checked += Handler;
         tls12.Box.Unchecked += Handler;
         tls13.Box.Checked += Handler;
         tls13.Box.Unchecked += Handler;
         Update();
      }

      private void Reload_Click(object sender, RoutedEventArgs e) => BuildUi();

      private void Save_Click(object sender, RoutedEventArgs e)
      {
         int saved = 0, failed = 0;

         bool iniWritten = false;

         foreach (TabDef tab in tabs_)
         foreach (CardDef card in tab.Cards)
         foreach (ComSetting setting in card.Settings)
         {
            try
            {
               // INI-backed rows are not in the COM settings tree.
               if (setting is IIniSetting ini)
               {
                  ini.SaveToIni();
                  iniWritten = true;
                  saved++;
                  continue;
               }

               // A row for a setting the server never reads writes nothing, and must
               // not be counted among the settings that were saved - "Saved 18
               // settings" has to be true.
               if (setting is INotPersisted)
                  continue;

               // Buttons and preset pickers persist nothing and name no property;
               // resolving one would fail and be counted as a save error.
               if (string.IsNullOrEmpty(setting.Path))
                  continue;

               object owner = ResolveOwner(setting.Path, out string property);
               setting.Write(owner, property);
               saved++;
            }
            catch (Exception)
            {
               failed++;
            }
         }

         // hMailServer.ini is read when the service starts, so an INI-backed row
         // does not take effect until it is restarted - don't claim otherwise.
         string appliedNote = iniWritten
            ? " - server settings applied immediately; hMailServer.ini settings apply after a service restart."
            : " - applied immediately.";

         StatusText.Text = failed == 0
            ? "Saved " + saved + " settings at " + DateTime.Now.ToLongTimeString() + appliedNote
            : "Saved " + saved + " settings, " + failed + " could not be written.";

         if (failed == 0)
            Services.Toast.Success("Saved " + saved + " settings" + (iniWritten ? " \u2014 INI settings need a service restart." : " \u2014 applied immediately."));
         else
            Services.Toast.Info(failed + " setting(s) could not be written.", "Partly saved");

         // Reload the script engine after scripting changes.
         if (section_ == Section.Advanced)
         {
            try
            {
               dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
               scripting.Reload();
               ServerSession.Release(scripting);
            }
            catch (Exception)
            {
            }
         }
      }
   }
}
