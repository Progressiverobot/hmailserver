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

         /// <summary>
         /// Lowest value the box will accept. Zero for almost everything, because
         /// almost every numeric INI value is a count or a number of seconds. It is
         /// settable because a few are not: the TLS session cache size uses a
         /// negative value to mean "no server-side cache at all", and a box that
         /// silently refused to go below zero would hide that option entirely.
         /// </summary>
         public int MinimumValue;

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
               Minimum = MinimumValue,
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
      /// A numeric setting in a named INI section other than [Settings].
      ///
      /// The database tuning values live in [Database]. Editing them through
      /// IniNumber would write keys of the right name into [Settings], where the
      /// server never looks - the value would appear saved, read back correctly on
      /// the next visit, and do nothing at all.
      /// </summary>
      private class SectionIniNumber : ComSetting, IIniSetting
      {
         public string Section;
         public int Default;
         public int MinimumValue;
         public IniFeatureStore IniStore;
         private Wpf.Ui.Controls.NumberBox number_;

         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            int current = Default;
            if (IniStore != null && IniStore.IsAvailable)
            {
               if (!int.TryParse(IniStore.ReadFrom(Section, Path, Default.ToString()), out current))
                  current = Default;
            }

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            number_ = new Wpf.Ui.Controls.NumberBox
            {
               Value = current,
               Minimum = MinimumValue,
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

         public void SaveToIni()
         {
            if (IniStore == null || !IniStore.IsAvailable || number_ == null)
               return;

            IniStore.WriteTo(Section, Path, ((long) (number_.Value ?? Default)).ToString());
         }
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

         /// <summary>
         /// What the SERVER uses when the key is absent from the ini - not a
         /// suggestion, and not necessarily empty. Without it this editor showed a
         /// blank box for a key the server defaults to a real value, which is a
         /// false statement about the running configuration; and because saving a
         /// page writes every field on it, the blank was then written back and the
         /// default silently lost. Both OAuth2 host lists default to a Microsoft
         /// host, so that is exactly the shape it bit.
         /// </summary>
         public string Default = "";

         private TextBox box_;

         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            string current = Default;
            if (IniStore != null && IniStore.IsAvailable)
               current = IniStore.Read(Path, Default);

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

      /// <summary>
      /// A read-only line of live server state, next to the settings that produce it.
      ///
      /// Not an editor and not a button: some things an administrator needs from a
      /// settings page are neither. The cache TTLs are only adjustable with
      /// judgement if the hit rate they produce is visible, and a page of four
      /// numbers with no feedback is a page you can only tune by guessing.
      /// </summary>
      private class ComStat : ComSetting, INotPersisted
      {
         /// <summary>Produces the text, or throws - which is reported in place of it.</summary>
         public Func<string> Read;

         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();

            if (!string.IsNullOrEmpty(Label))
               panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            string text;
            try
            {
               text = Read();
            }
            catch (Exception ex)
            {
               text = "This could not be read from the server: " + ServerSession.DescribeComError(ex);
            }

            var line = new TextBlock
            {
               Text = text,
               FontSize = 13,
               TextWrapping = TextWrapping.Wrap,
               MaxWidth = 620,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            Describe(line, Path);
            panel.Children.Add(line);
            Annotate(line, panel);
            return panel;
         }

         /// <summary>Nothing to read back: this row edits nothing.</summary>
         public override object ReadEditor() => null;

         /// <summary>Nothing to write. Marked so Save_Click does not count it.</summary>
         public override void Write(object owner, string property)
         {
         }
      }

      /// <summary>
      /// A non-persistent action button (e.g. "Test connection") with a result line.
      ///
      /// Marked INotPersisted, which it was not. Nothing was ever written wrongly -
      /// Write below is a no-op override, which is what made the omission harmless -
      /// but Save's skip test is "INotPersisted, or an empty Path", and the comment
      /// on that second test ("buttons and preset pickers persist nothing and name
      /// no property") is not true of these buttons: several carry a Path so they
      /// sit beside the switch they act on - "Clear greylisting triplets" names
      /// AntiSpam.GreyListingEnabled, "Test SpamAssassin connection" names
      /// AntiSpam.SpamAssassinEnabled, and the index and repair actions name theirs.
      ///
      /// So each of those fell through to the no-op Write and then incremented the
      /// counter, and "Saved 18 settings" counted buttons among the settings. That
      /// sentence has to be true - it is the only confirmation an administrator
      /// gets that a save did anything - so the row is excluded by the marker that
      /// means what it says, rather than by a Path test that describes these rows
      /// incorrectly.
      /// </summary>
      private class ComAction : ComSetting, INotPersisted
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

         // These two are INI values rather than COM ones, and they used to be on
         // the catch-all INI page for that reason alone - which put the answer to
         // "IMAP search stopped working" on a page nobody opens looking for IMAP.
         // Storage mechanism is not a subject heading; the search limits belong
         // beside the SORT switch they interact with.
         var imapSearch = Card("IMAP search limits",
            "Ceilings on a single IMAP SEARCH or SORT, measured from the start of the search, so that one client " +
            "searching a very large mailbox cannot occupy a connection indefinitely. If users with big mailboxes " +
            "report searches failing or returning nothing, these are the two to raise. Either half can be turned " +
            "off on its own by setting it to 0. Applies after a service restart.");
         imapSearch.Settings.Add(new IniNumber
         {
            Path = "IMAPSearchTimeout",
            Label = "Maximum time for one IMAP search (seconds; 0 = no limit)",
            Default = 60,
            Blurb = "The search stops when this is reached and the client is told the search failed, rather than being " +
                    "given a partial result it would mistake for a complete one.",
            IniStore = iniStore_
         });
         imapSearch.Settings.Add(new IniNumber
         {
            Path = "IMAPSearchMaxMegabytes",
            Label = "Maximum message content read for one IMAP search (MB; 0 = no limit)",
            Default = 2048,
            Blurb = "A separate ceiling from the time limit, because a search that reads enormous amounts of message " +
                    "content is expensive even when it finishes quickly.",
            IniStore = iniStore_
         });
         Tab("IMAP").Cards.Add(imapSearch);

         var pop3 = Card("POP3");
         pop3.Settings.Add(new ComText { Path = "MaxPOP3Connections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         pop3.Settings.Add(new ComText { Path = "WelcomePOP3", Label = "Welcome banner (empty = default)" });
         Tab("POP3").Cards.Add(pop3);

         // RFC 2449 LOGIN-DELAY. On the POP3 page rather than the ini page because
         // that is where the question is asked, and next to the connection ceiling it
         // is the gentler alternative to.
         var pop3Polling = Card("How often a client may check for mail",
            "POP3 has no way for the server to tell a client that mail has arrived, so clients that want to look " +
            "responsive poll - and some poll every few seconds. Each poll is a new connection, a TLS handshake and a " +
            "password verification, which with Argon2id is deliberately expensive, so a handful of eager clients can " +
            "cost more than the mail does. Setting a delay tells clients the interval in the server's capability " +
            "list and refuses logins that arrive early with a code the client understands as \"slow down\" rather " +
            "than as a wrong password. Applies after a service restart.");
         pop3Polling.Settings.Add(new IniNumber
         {
            Path = "Pop3LoginDelaySeconds",
            Label = "Minimum seconds between logins to one account (0 = no limit)",
            Default = 0,
            Blurb = "Counted per account from the last login that was allowed, so a refused attempt does not push the " +
                    "next one further away. It is advertised only while it is set, because announcing a limit that is " +
                    "not enforced is worse than announcing nothing.",
            IniStore = iniStore_
         });
         Tab("POP3").Cards.Add(pop3Polling);

         // The idle timeouts follow the same reasoning as the IMAP search limits
         // above: they are per-protocol settings that sat on the INI page because
         // of where they are stored. They get a tab rather than being split three
         // ways across the protocol tabs because the server/client distinction is
         // the thing that confuses people, and it only reads clearly when all
         // eight are in one place.
         var idle = Card("Idle timeouts",
            "How long a connection may sit idle before it is closed, per protocol, in seconds. " +
            "\"Server\" is hMailServer accepting a connection from a mail client or another mail server; " +
            "\"client\" is hMailServer connecting out to deliver mail or to fetch it from an external POP3 " +
            "account. The effective timeout moves between the minimum and the maximum with server load, so a " +
            "busy server drops idle connections sooner. Applies after a service restart.");
         idle.Settings.Add(new IniNumber { Path = "SMTPDMinTimeout", Label = "SMTP server minimum timeout (s)", Default = 10, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "SMTPDMaxTimeout", Label = "SMTP server maximum timeout (s)", Default = 1800, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "SMTPCMinTimeout", Label = "SMTP client minimum timeout (s)", Default = 30, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "SMTPCMaxTimeout", Label = "SMTP client maximum timeout (s)", Default = 600, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "POP3DMinTimeout", Label = "POP3 server minimum timeout (s)", Default = 10, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "POP3DMaxTimeout", Label = "POP3 server maximum timeout (s)", Default = 600, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "POP3CMinTimeout", Label = "POP3 client minimum timeout (s)", Default = 30, IniStore = iniStore_ });
         idle.Settings.Add(new IniNumber { Path = "POP3CMaxTimeout", Label = "POP3 client maximum timeout (s)", Default = 900, IniStore = iniStore_ });
         Tab("Timeouts").Cards.Add(idle);

         // On the IMAP tab because that is what it repairs, and because somebody
         // arrives at this needing it - after a restored database, or after
         // clients start showing the wrong message - rather than browsing for it.
         var repair = Card("Repair",
            "Maintenance for the IMAP folder state. Nothing here changes a message; the folder UID counter is "
            + "bookkeeping that IMAP clients rely on to tell one message from another.");
         repair.Settings.Add(new ComAction
         {
            Path = "ServiceIMAP",
            ButtonText = "Recalculate folder UID counters",
            Blurb = "Each IMAP folder keeps a counter for the next message UID it will issue. If that counter falls "
                    + "behind the messages already in the folder - which a restored or hand-edited database can do - "
                    + "the folder issues a UID it has already used, and a client that caches by UID shows one message "
                    + "where another should be. This moves every counter up to the highest UID its folder actually "
                    + "holds. It never moves one down, so it cannot cause the fault it repairs.",
            Action = () => RecalculateFolderUids_()
         });
         Tab("IMAP").Cards.Add(repair);
      }

      private void BuildDelivery()
      {
         TitleText.Text = "Delivery of e-mail";
         SubtitleText.Text = "Outbound delivery behavior, retries and smart-host relaying.";

         var del = Card("Delivery of e-mail",
            "Outbound delivery, retries and throttling. Keeping forwarded mail SPF-aligned (SRS) and " +
            "bounce tagging (BATV) are on the Transport security page.");
         del.Settings.Add(new ComText { Path = "SMTPNoOfTries", Label = "Number of delivery retries", Numeric = true });
         del.Settings.Add(new ComText { Path = "SMTPMinutesBetweenTry", Label = "Minutes between retries", Numeric = true });
         del.Settings.Add(new ComText { Path = "MaxNumberOfMXHosts", Label = "Max MX hosts to try (0 = all)", Numeric = true });
         // Directly modifies the retry count three rows up, and only makes sense
         // read together with the MX host limit immediately above it, so it moved
         // here from the catch-all INI page.
         del.Settings.Add(new IniNumber
         {
            Path = "MXTriesFactor",
            Label = "Extra delivery attempts per additional MX host (0 = default)",
            Default = 0,
            Blurb = "A recipient domain with several MX hosts is worth more attempts than one with a single host, " +
                    "because a retry can land on a different server. This multiplies the retry count above by the " +
                    "number of MX hosts actually tried.",
            IniStore = iniStore_
         });
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

         var oauth = Card("OAuth2 for the relay (Microsoft 365)",
            "Microsoft 365 turns Basic authentication for SMTP AUTH off at the end of December 2026. " +
            "When the relay host below is listed here, hMailServer authenticates with an OAuth2 bearer " +
            "token (XOAUTH2) instead of the password - fetched from your tenant's token endpoint with " +
            "the client credentials of an app registration that has the SMTP.SendAsApp permission. The " +
            "user name above stays: it is the mailbox being relayed as.");
         oauth.Settings.Add(new IniText
         {
            Path = "OutboundOAuth2Hosts",
            Label = "Relay hosts that use OAuth2 (comma separated; blank this to use none)",
            Default = "smtp.office365.com",
            Placeholder = "smtp.office365.com",
            Blurb = "Only destinations named here get a bearer token; every other relay keeps password " +
                    "authentication. This is deliberate - a token presented to the wrong relay is a leaked token.",
            IniStore = iniStore_
         });
         oauth.Settings.Add(new IniText
         {
            Path = "OutboundOAuth2TokenUrl",
            Label = "Token endpoint (https)",
            Placeholder = "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token",
            IniStore = iniStore_
         });
         oauth.Settings.Add(new IniText
         {
            Path = "OutboundOAuth2ClientId",
            Label = "Application (client) ID",
            IniStore = iniStore_
         });
         oauth.Settings.Add(new IniText
         {
            Path = "OutboundOAuth2ClientSecret",
            Label = "Client secret",
            Blurb = "Stored in hMailServer.ini on the server. The token it buys is cached in memory until " +
                    "80% of its lifetime has passed and refetched automatically.",
            IniStore = iniStore_
         });
         oauth.Settings.Add(new IniText
         {
            Path = "OutboundOAuth2Scope",
            Label = "Scope (empty = the Microsoft default)",
            Placeholder = "https://outlook.office365.com/.default",
            IniStore = iniStore_
         });
         oauth.Settings.Add(new IniText
         {
            Path = "OutboundOAuth2FixedToken",
            Label = "Fixed token (advanced; used verbatim instead of fetching)",
            Blurb = "For tokens obtained outside this server. Leave empty in normal use - fixed tokens " +
                    "expire and nothing here will refresh them.",
            IniStore = iniStore_
         });
         // The collecting half of the same Microsoft 365 story, and it was the one
         // setting of the pair with no field: an administrator could configure
         // sending through M365 here and had to hand-edit the ini to configure
         // fetching from it, which is the same credential problem in the other
         // direction.
         oauth.Settings.Add(new IniText
         {
            Path = "FetchOAuth2Hosts",
            Label = "External-account hosts to FETCH from with OAuth2 (comma separated; blank this to use none)",
            Default = "outlook.office365.com",
            Placeholder = "outlook.office365.com",
            Blurb = "External accounts whose server is named here log in with a bearer token from the same token " +
                    "endpoint above; every other external account keeps USER/PASS. Collecting from Microsoft 365 " +
                    "with a password has not been possible since 2022.",
            IniStore = iniStore_
         });
         Tab("Relayer").Cards.Add(oauth);

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

         // Directly under the delete threshold, because this setting changes what that
         // threshold DOES rather than adding a feature beside it.
         var quarantine = Card("Quarantine instead of refusing",
            "Changes what happens at the delete threshold. Off, a message over it is refused during the SMTP "
          + "conversation with a 550 and the sender is told. On, it is accepted with a 250 and held for review "
          + "instead - the sender believes it was delivered and will not retry, which is what makes a false "
          + "positive recoverable without bouncing to a return path that is probably forged, and also means the "
          + "review queue becomes the only place that message exists. Applies to checks that run after the "
          + "message body has been received; a verdict reached before that (a blacklisted connecting IP, a bad "
          + "HELO) has no message to hold and stays a refusal. Applies after a service restart.");
         quarantine.Settings.Add(new IniBool
         {
            Path = "QuarantineEnabled",
            Label = "Hold messages for review instead of refusing them",
            Blurb = "Off by default. Turning it on means the server starts storing mail it considers spam, so "
                  + "watch the retention window below.",
            IniStore = iniStore_
         });
         quarantine.Settings.Add(new IniNumber
         {
            Path = "QuarantineRetentionDays",
            Label = "Delete quarantined messages after (days; 0 = never)",
            Default = 30,
            Blurb = "0 is a real answer, not a disabled one - it keeps everything, which is what an "
                  + "administrator collecting evidence wants. Everything else is a promise to the disk.",
            IniStore = iniStore_
         });
         Tab("General").Cards.Add(quarantine);

         var auth = Card("Sender authentication");
         auth.Settings.Add(new ComBool { Path = "AntiSpam.UseSPF", Label = "Check SPF" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.UseSPFScore", Label = "SPF failure score", Numeric = true });
         auth.Settings.Add(new ComBool { Path = "AntiSpam.DKIMVerificationEnabled", Label = "Verify DKIM signatures" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.DKIMVerificationFailureScore", Label = "DKIM failure score", Numeric = true });
         auth.Settings.Add(new ComBool { Path = "AntiSpam.DMARCEnabled", Label = "Evaluate DMARC policies" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.DMARCFailureScore", Label = "DMARC failure score", Numeric = true });
         auth.Settings.Add(new IniBool
         {
            Path = "DmarcTreeWalkEnabled",
            Label = "Find organizational domains by DNS tree walk (RFC 9989)",
            Blurb = "How the server decides that mail.example.com and example.com belong to the same organization, " +
                    "which is what DMARC's default relaxed alignment compares. The old answer was the Public Suffix " +
                    "List - a file of every registry's delegation rules, compiled in and refreshed at build time. " +
                    "DMARCbis replaced it with a walk up the DNS asking each level whether it is an organizational " +
                    "boundary, so a domain owner can state where their boundary is instead of petitioning a list. " +
                    "Turning this off falls back to the compiled list, which is also what happens for any single " +
                    "lookup the walk cannot complete because DNS was unavailable - a resolver outage must not " +
                    "quietly turn relaxed alignment into strict. Costs up to eight DNS queries per domain, cached " +
                    "for five minutes. Applies after a service restart.",
            IniStore = iniStore_
         });
         auth.Settings.Add(new ComBool
         {
            Path = "AntiSpam.ArcFilteringEnabled",
            Label = "Use ARC results from trusted forwarders to offset a DMARC failure",
            Blurb = "Forwarding breaks SPF, because the envelope sender changes, and often breaks DKIM, because the body " +
                    "is modified - so a mailing list or a forwarded mailbox can turn a message that passed DMARC at the " +
                    "sender into one that fails it here. A valid ARC chain (RFC 8617) from a forwarder you trust carries " +
                    "the original result, and this cancels exactly the DMARC failure score, never more. It needs " +
                    "'Evaluate DMARC policies' above to be on, since it only ever offsets a penalty that test adds."
         });
         auth.Settings.Add(new ComText
         {
            Path = "AntiSpam.ArcTrustedSealers",
            Label = "Trusted ARC sealer domains",
            Blurb = "Required. With this empty the setting above does nothing at all, and that is deliberate rather than " +
                    "an oversight: anyone can fabricate a whole ARC chain and seal it with a key they publish in their " +
                    "own DNS, and it will validate perfectly, so a passing chain proves nothing unless you already trust " +
                    "the sealer. This list is not an option of the feature - it is the feature. Name the exact domains " +
                    "whose seals you honour (the d= of their ARC-Seal), separated by commas, semicolons or spaces; " +
                    "matching is exact, so a suffix of a trusted name is not trusted."
         });
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
         // The last two greylisting settings are stored in hMailServer.INI rather
         // than in the settings database, and they used to be on the catch-all INI
         // page because of it - so half of greylisting was configured here and half
         // somewhere an administrator would only find by accident. They apply after
         // a service restart, unlike the COM rows above; the labels say so.
         grey.Settings.Add(new IniBool
         {
            Path = "GreylistingEnabledDuringRecordExpiration",
            Label = "Keep greylisting active during the record-expiration window (restart required)",
            Default = true,
            Blurb = "A triplet that has been seen before but has not yet been confirmed sits in a window before its " +
                    "record expires. Turning this off lets mail through during that window instead of greylisting it " +
                    "again, which is gentler on senders that retry slowly and weaker against a sender that retries once.",
            IniStore = iniStore_
         });
         grey.Settings.Add(new IniNumber
         {
            Path = "GreylistingRecordExpirationInterval",
            Label = "Record expiration interval, minutes (restart required)",
            Default = 240,
            Blurb = "How long that window lasts. Four hours by default.",
            IniStore = iniStore_
         });
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
         sa.Settings.Add(new IniBool
         {
            Path = "SAMoveVsCopy",
            Label = "Move the message file to SpamAssassin instead of copying it (restart required)",
            Default = false,
            Blurb = "How the message reaches SpamAssassin when it runs on this machine: a move saves writing a second " +
                    "copy of every message to disk, which matters on a busy server. It only works when SpamAssassin " +
                    "reads from the same volume; against a remote spamd it is the wrong choice.",
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

         // The posture question the rest of this page cannot answer: what happens
         // to a message when the scanner is switched on and cannot run. Until
         // AVFailAction existed the answer was "delivered, with one line in the
         // error log", which is indistinguishable from "scanned and found clean" -
         // so it belongs here, next to the scanners it applies to, rather than in
         // an ini file the people who care about it will never open.
         var failure = Card("When a scanner cannot run",
            "A scan that errors, times out or cannot reach its engine is not a clean verdict - nobody looked. " +
            "The shipped behaviour is to deliver the message anyway, which is what this server has always done; " +
            "holding it instead is the stricter choice and the one to make deliberately.");
         failure.Settings.Add(new IniNumber
         {
            Path = "AVFailAction",
            Label = "If a message cannot be scanned: 0 = deliver it, 1 = hold it and eventually return it",
            Default = 0,
            Blurb = "0 is the shipped default and preserves today's behaviour exactly. 1 never delivers unscanned mail.",
            IniStore = iniStore_
         });
         failure.Settings.Add(new IniNumber
         {
            Path = "AVFailRetryMinutes",
            Label = "Minutes between scan attempts while held",
            Default = 15,
            IniStore = iniStore_
         });
         failure.Settings.Add(new IniNumber
         {
            Path = "AVFailMaxHolds",
            Label = "How many times to hold before returning it to the sender",
            Default = 16,
            Blurb = "16 holds at 15 minutes is about four hours. 0 means never queue unscanned mail at all - tell the " +
                    "sender immediately. A held message is delivered as soon as any scanner answers; the count is kept " +
                    "in memory, so a service restart gives a held message a fresh budget.",
            IniStore = iniStore_
         });
         Tab("General").Cards.Add(failure);

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
         // Labelled "TLS 1.2 and below" rather than just "Cipher list", because that is
         // what it governs and the old label implied otherwise. OpenSSL keeps the TLS
         // 1.3 suites in a separate list that SSL_CTX_set_cipher_list does not touch, so
         // removing a cipher here and confirming it with a TLS 1.2 scan proved nothing
         // about the version most clients actually negotiate.
         ciph.Settings.Add(new ComText
         {
            Path = "SslCipherList",
            Label = "Cipher list for TLS 1.2 and below (OpenSSL format)",
            Blurb = "The single value AEAD-ONLY (case-insensitive) is a named preset rather than an OpenSSL cipher " +
                    "string: forward-secret AEAD suites only (ECDHE or DHE with AES-GCM or ChaCha20-Poly1305), " +
                    "excluding every CBC-mode suite - the Lucky13 padding-oracle family - and static-RSA key exchange. " +
                    "The cost: TLS 1.2 clients that only speak CBC suites cannot connect, and TLS 1.0/1.1 are left with " +
                    "no usable cipher at all, so do not enable those protocols alongside it. TLS 1.3 is unaffected - its " +
                    "suites are AEAD by construction and are configured separately below. A misspelled preset name is " +
                    "rejected and reported, not silently ignored."
         });
         ciph.Settings.Add(new IniText
         {
            Path = "TlsCipherSuites13",
            Label = "TLS 1.3 cipher suites (empty = OpenSSL defaults)",
            Placeholder = "TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256:TLS_AES_128_GCM_SHA256",
            Blurb = "TLS 1.3 has its own suite list, its own names and its own setter, so the cipher list above " +
                    "does not restrict it. Colon separated, most preferred first, using the RFC 8446 names " +
                    "(TLS_AES_256_GCM_SHA384, TLS_CHACHA20_POLY1305_SHA256, TLS_AES_128_GCM_SHA256, " +
                    "TLS_AES_128_CCM_SHA256, TLS_AES_128_CCM_8_SHA256) - not the OpenSSL cipher-list names. " +
                    "Leave empty to keep OpenSSL's defaults; a name this build does not recognise is skipped " +
                    "and reported rather than failing the whole list.",
            IniStore = iniStore_
         });
         ciph.Settings.Add(preferServer);
         ciph.Settings.Add(chacha);
         ciph.Settings.Add(new ComBool { Path = "VerifyRemoteSslCertificate", Label = "Verify remote certificates when delivering" });
         Tab("Ciphers").Cards.Add(ciph);

         // Resumption governs every SSL/TLS and STARTTLS listener this server runs,
         // so it belongs on the TLS page and nowhere else; it sat on the catch-all
         // INI page only because that is where it is stored. Every claim below is
         // from SslContextInitializer::SetSessionResumption_, which follows the
         // house rule that a default value makes no OpenSSL call at all - so the
         // card can honestly promise that the defaults change nothing.
         var resume = Card("Session resumption",
            "Session caching and session tickets let a returning client skip the full handshake. At the defaults " +
            "these four settings make no OpenSSL call at all, so resumption keeps OpenSSL's stock behaviour - " +
            "they are here for administrators who need to bound how long a resumption secret stays useful. " +
            "Applies after a service restart.");
         resume.Settings.Add(new IniBool
         {
            Path = "TlsSessionTicketsEnabled",
            Label = "Issue TLS session tickets so clients can resume sessions",
            Default = true,
            Blurb = "Turning this off stops session tickets on every TLS version: TLS 1.2 and older clients fall back " +
                    "to the server-side session cache, which never leaves this process, and TLS 1.3 clients are sent " +
                    "no ticket at all. For an administrator whose policy is that nothing derived from a long-lived key " +
                    "ever goes on the wire. The only cost is resumption efficiency - every client can still connect " +
                    "with a full handshake.",
            IniStore = iniStore_
         });
         resume.Settings.Add(new IniNumber
         {
            Path = "TlsSessionCacheSize",
            Label = "Server-side session cache size (0 = OpenSSL's default cap of 20480; negative = no cache)",
            Default = 0,
            // The one numeric INI setting on any page where a negative value is
            // meaningful rather than a mistake, which is why IniNumber grew a
            // minimum at all.
            MinimumValue = -1,
            Blurb = "Each cached session costs server memory, and a client that churns handshakes grows the cache - " +
                    "this cap is the defence. A positive value replaces OpenSSL's default cap of 20480 sessions. " +
                    "-1 turns the server-side cache off entirely, which stops session-ID resumption but leaves " +
                    "tickets - which cost the server no memory - unaffected. 0 leaves OpenSSL alone.",
            IniStore = iniStore_
         });
         resume.Settings.Add(new IniNumber
         {
            Path = "TlsSessionTimeoutSeconds",
            Label = "Resumption lifetime for cached sessions and tickets, seconds (0 = OpenSSL's default of 300)",
            Default = 0,
            Blurb = "How long a session stays resumable, for cached sessions and tickets alike. It bounds how long a " +
                    "leaked resumption secret stays useful: a ticket recorded off the wire, or a session read out of a " +
                    "memory dump, stops working once it expires.",
            IniStore = iniStore_
         });
         resume.Settings.Add(new IniNumber
         {
            Path = "TlsTicketKeyRotationSeconds",
            Label = "Rotate the session-ticket key every N seconds (0 = OpenSSL's single non-rotating key)",
            Default = 0,
            Blurb = "What rotation defends: OpenSSL's default ticket key is generated once, when the listener starts, " +
                    "and is never rotated, so every ticket that listener issues for the life of the process is sealed " +
                    "under the same key - and that key, recovered later, decrypts every ticket ever recorded. With an " +
                    "interval set, a captured ticket is useless after at most two intervals. 86400 is a day. Only " +
                    "meaningful while session tickets are enabled above.",
            IniStore = iniStore_
         });
         Tab("Session resumption").Cards.Add(resume);

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

         // The other half of the same subject, and the reason it is on this page:
         // auto-ban counts per ADDRESS, so a distributed attack that spends a few
         // guesses per address against one mailbox never trips it. This counts per
         // NAME, which is the one thing such an attack cannot vary. An
         // administrator reading a page called "Auto-ban" is entitled to find both.
         var lockout = Card("Per-name lockout",
            "Counted per user name rather than per address, across every protocol that authenticates, so a botnet " +
            "spreading its guesses over thousands of addresses still locks the mailbox it is guessing at. A locked " +
            "name is refused with the ordinary invalid-credentials reply and cannot be unlocked by typing the right " +
            "password - the lock is checked first - so the ceiling is worth setting with the cost in mind. Failures " +
            "against a name that does not exist lock that name too, deliberately: doing otherwise would let an " +
            "attacker use the lockout to discover which accounts are real.");
         lockout.Settings.Add(new IniNumber
         {
            Path = "AccountLockoutThreshold",
            Label = "Lock a name after this many failures (0 = off)",
            Default = 0,
            Blurb = "0 is the shipped default and disables the whole mechanism.",
            IniStore = iniStore_
         });
         lockout.Settings.Add(new IniNumber
         {
            Path = "AccountLockoutWindowMinutes",
            Label = "...counted within (minutes)",
            Default = 30,
            IniStore = iniStore_
         });
         lockout.Settings.Add(new IniNumber
         {
            Path = "AccountLockoutMinutes",
            Label = "Lockout duration (minutes)",
            Default = 30,
            Blurb = "A successful logon clears the name's counters, and attempts made while it is locked are not " +
                    "counted - so an attacker cannot hold a mailbox locked indefinitely with a trickle of guesses.",
            IniStore = iniStore_
         });
         Tab("Auto-ban").Cards.Add(lockout);
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

         // Beside log retention rather than in a section of its own: the two answer the
         // same operational question - how much history is kept, and for how long.
         var trace = Card("Message trace",
            "Records a queryable row for every delivery, refusal and quarantine, so \"what happened to the "
          + "message Jane sent at 14:20\" can be answered by searching instead of grepping logs. The events "
          + "are the same ones the AWStats journal has always seen; this gives them somewhere to be asked "
          + "questions. Off by default, because it stores sender and recipient addresses - which makes it a "
          + "record of who corresponds with whom. Applies after a service restart.");
         trace.Settings.Add(new IniBool
         {
            Path = "MessageTraceEnabled",
            Label = "Record a queryable message trace",
            Blurb = "Independent of the AWStats log above - either can be on without the other.",
            IniStore = iniStore_
         });
         trace.Settings.Add(new IniNumber
         {
            Path = "MessageTraceRetentionDays",
            Label = "Delete trace events after (days; 0 = never)",
            Default = 30,
            Blurb = "For a record of who corresponds with whom, keeping it for ever should be a decision "
                  + "rather than a default - which is why 0 is offered as an option instead of being one.",
            IniStore = iniStore_
         });
         logging.Cards.Add(trace);

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
         // The fourth thread pool, and the only one that was not on this card:
         // it is an INI value rather than a COM one, which is not a reason for an
         // administrator sizing thread pools to have to look somewhere else.
         threads.Settings.Add(new IniNumber
         {
            Path = "MaxNumberOfExternalFetchThreads",
            Label = "Max parallel external POP3 fetch threads (restart required)",
            Default = 15,
            Blurb = "How many external accounts, configured under a domain's accounts as external POP3 downloads, are " +
                    "collected at the same time. Each one holds a connection to somebody else's server for as long as " +
                    "the download takes.",
            IniStore = iniStore_
         });

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
         // The four max-size limits are the one thing on this card that does NOT
         // survive a restart. Verified in InterfaceCache/CacheContainer: the
         // setters adjust the live Cache<T> and nothing else - no property row,
         // no OnPropertyChanged arm - and the constructor hard-codes 10 MB at
         // startup. Kept editable because the server honours the value
         // immediately and the getter reads the live value back; labelled and
         // noted as session-only because the label is what the Ctrl+K palette
         // shows, and an administrator must be told before they type, not after
         // the next restart quietly discards it.
         cache.Settings.Add(new ComText { Path = "Cache.DomainCacheMaxSizeKb", Label = "Domain cache max size (KB, resets at service restart)", Numeric = true, Blurb = SettingClaims.NoteFor("Cache.DomainCacheMaxSizeKb") });
         cache.Settings.Add(new ComText { Path = "Cache.AccountCacheMaxSizeKb", Label = "Account cache max size (KB, resets at service restart)", Numeric = true, Blurb = SettingClaims.NoteFor("Cache.AccountCacheMaxSizeKb") });
         cache.Settings.Add(new ComText { Path = "Cache.AliasCacheMaxSizeKb", Label = "Alias cache max size (KB, resets at service restart)", Numeric = true, Blurb = SettingClaims.NoteFor("Cache.AliasCacheMaxSizeKb") });
         cache.Settings.Add(new ComText { Path = "Cache.DistributionListCacheMaxSizeKb", Label = "Distribution-list cache max size (KB, resets at service restart)", Numeric = true, Blurb = SettingClaims.NoteFor("Cache.DistributionListCacheMaxSizeKb") });
         Tab("Cache").Cards.Add(cache);

         // Four TTLs and four size caps, and until now no way to see what any of
         // them achieved. The server has counted hits and misses per cache all
         // along and exposes the rate over COM; without it on the page, the only
         // way to tune a TTL was to change it and hope. Read-only, and read fresh
         // on every visit to this page.
         var cacheStats = Card("How the caches are performing",
            "Live, from the running server. The hit rate is the share of lookups answered from memory since the "
            + "counters last reset - which they do when the service restarts, when a cache is switched off, and "
            + "when its TTL is changed, so a rate right after any of those describes a very short sample. A lookup "
            + "for something that does not exist counts as a miss and caches nothing, so a server being probed for "
            + "addresses it does not host shows a low rate without anything being wrong with the cache.");
         cacheStats.Settings.Add(new ComStat
         {
            Path = "Cache.HitRates",
            Label = "Hit rate and memory in use, per cache",
            Read = () => DescribeCaches_(),
            Blurb = "A low rate on a busy server usually means the TTL is shorter than the interval between lookups "
                    + "of the same object; a rate of 0% with no memory in use means nothing has been looked up yet, "
                    + "which is not the same thing. Memory in use is measured against the size caps above, and a "
                    + "cache at its cap is evicting entries it would otherwise have kept."
         });
         Tab("Cache").Cards.Add(cacheStats);

         var index = Card("Message indexing", "Builds a search index so IMAP SEARCH and the web client are faster.");
         index.Settings.Add(new ComBool { Path = "MessageIndexing.Enabled", Label = "Enable message indexing" });
         // Tuning lives with the feature it tunes: a tab holding a single on/off
         // switch reads as "there is nothing to adjust here".
         index.Settings.Add(new IniNumber { Path = "IndexerFullMinutes", Label = "Full re-index interval (minutes)", Default = 720, IniStore = iniStore_ });
         index.Settings.Add(new IniNumber { Path = "IndexerFullLimit", Label = "Messages per full-index pass", Default = 25000, IniStore = iniStore_ });
         index.Settings.Add(new IniNumber { Path = "IndexerQuickLimit", Label = "Messages per quick-index pass", Default = 1000, IniStore = iniStore_ });

         // The two actions on MessageIndexing existed over COM and nowhere else, so
         // "search finds nothing" or "search finds messages that were deleted" had
         // no answer short of writing a script. Both report the counts they moved
         // rather than only "done": the whole question an administrator has here is
         // whether the index matches the mail, and a number answers it.
         index.Settings.Add(new ComAction
         {
            Path = "MessageIndexing.Enabled",
            ButtonText = "Index now, and show how far behind it is",
            Blurb = "Wakes the indexer instead of waiting for its next pass, and reports how many of this server's "
                    + "messages are currently indexed. The pass itself runs in the background and is bounded by the "
                    + "limits above, so on a large backlog it makes progress rather than finishing. With indexing "
                    + "switched off there is no indexer to wake, and this says so rather than reporting success.",
            Action = () => RunIndexNow_()
         });
         index.Settings.Add(new ComAction
         {
            Path = "MessageIndexing.Enabled",
            ButtonText = "Discard the index and rebuild it",
            Blurb = "Empties the index and asks the indexer to rebuild it. No mail is touched - the index is derived "
                    + "data - but until the rebuild catches up, IMAP SEARCH finds less than it should. Worth doing "
                    + "when the index has more entries than the server has messages, which means entries for mail "
                    + "that no longer exists.",
            Action = () => RebuildIndex_()
         });
         Tab("Indexing").Cards.Add(index);

         // The three [Database] values that are tuning rather than connection detail.
         //
         // Everything else in that section - server, database name, credentials, type
         // and provider - decides whether the server starts at all, and is owned by
         // the setup wizard. These three change how it behaves once connected, and
         // they had no editor anywhere: an administrator whose server logs
         // "connection pool exhausted" had to hand-edit the file, and a mail server
         // whose database is briefly unreachable at boot had no way to lengthen the
         // retry window without doing the same.
         //
         // Defaults and the SQL CE override are read from IniFileSettings.cpp lines
         // 135-145: five connections, six attempts, five seconds between them, and
         // no_of_dbconnections_ forced to 1 when the type is MSSQLCE regardless of
         // what the file says.
         //
         // Written out here rather than in a helper because the settings-index
         // generator only reads the body of a Build<Section>() method; rows built
         // anywhere else compile, render and save, and are invisible to Ctrl+K.
         string databaseType = iniStore_.IsAvailable
            ? iniStore_.ReadFrom("Database", "Type", "").Trim()
            : "";

         bool builtInDatabase = string.Equals(databaseType, "MSSQLCE", StringComparison.OrdinalIgnoreCase);

         // Named rather than described: an administrator who is about to raise the
         // pool size needs to know it will be ignored BEFORE they raise it, and the
         // only honest way to know is to read the configured type.
         string poolNote =
            "How many database connections the server keeps open and shares between its worker threads. Raising it "
            + "helps only when threads are visibly waiting for a connection - see the database connection acquire "
            + "timeout on the Server limits & expert settings page, which is what they are waiting on.";

         if (builtInDatabase)
         {
            poolNote += "  THIS SERVER USES THE BUILT-IN DATABASE (MSSQLCE), where the value is forced to 1 whatever "
                        + "is set here. That is deliberate: SQL Server Compact does not behave reliably under "
                        + "concurrent connections. Changing it below will have no effect until the server is moved "
                        + "to MySQL, MSSQL or PostgreSQL.";
         }

         var database = Card("Database connections",
            "How the server uses the database it is already configured for. Where that database IS - the server, "
            + "name, credentials and type - is set up by the installation wizard and is deliberately not editable "
            + "here, because a wrong value there stops the server from starting rather than making it slower. "
            + "Applies after a service restart."
            + (databaseType.Length > 0 ? "  Configured database type: " + databaseType + "." : ""));

         database.Settings.Add(new SectionIniNumber
         {
            Section = "Database",
            Path = "NumberOfConnections",
            Label = "Connections in the pool" + (builtInDatabase ? " (ignored: this server uses the built-in database)" : ""),
            Default = 5,
            MinimumValue = 1,
            Blurb = poolNote,
            IniStore = iniStore_
         });

         database.Settings.Add(new SectionIniNumber
         {
            Section = "Database",
            Path = "ConnectionAttempts",
            Label = "Attempts to reach the database at start-up",
            Default = 6,
            MinimumValue = 1,
            Blurb = "A mail server usually starts with the rest of the machine, and on a machine where the database "
                    + "service starts second, the first few attempts fail. Together with the delay below this is the "
                    + "whole window: six attempts five seconds apart is half a minute. Lengthen it rather than "
                    + "delaying the service if the database is on another host that is slower to come up.",
            IniStore = iniStore_
         });

         database.Settings.Add(new SectionIniNumber
         {
            Section = "Database",
            Path = "ConnectionAttemptsDelay",
            Label = "Seconds between those attempts",
            Default = 5,
            MinimumValue = 1,
            IniStore = iniStore_
         });

         Tab("Database").Cards.Add(database);
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

         // The policy applies to MAILBOX passwords, and it sits on the same page as the
         // administrator password because "Password" is where somebody looks for it -
         // not because the two are related. Everything here is off by default, and the
         // card says why that matters rather than leaving it to be discovered.
         var policy = Card("Policy for mailbox passwords",
            "Applied when a password is CHOSEN - here, in the account editor, by a script or by the API - and never "
          + "when one is checked at logon. That distinction is the whole design: an account whose password predates "
          + "the policy keeps working, because locking people out of mailboxes they can open today is a worse outcome "
          + "than the weak password it would be correcting. Tightening these settings therefore affects the next "
          + "password each person sets, not the one they have. Everything is off until you turn it on, so an upgrade "
          + "never starts refusing passwords on its own.");
         policy.Settings.Add(new IniNumber
         {
            Path = "PasswordPolicyMinimumLength",
            Label = "Minimum length (0 = no minimum)",
            Default = 0,
            Blurb = "Length is the requirement that buys the most, and the one people mind least.",
            IniStore = iniStore_
         });
         policy.Settings.Add(new IniBool
         {
            Path = "PasswordPolicyRequireMixedCase",
            Label = "Require both upper and lower case",
            IniStore = iniStore_
         });
         policy.Settings.Add(new IniBool
         {
            Path = "PasswordPolicyRequireDigit",
            Label = "Require at least one digit",
            IniStore = iniStore_
         });
         policy.Settings.Add(new IniBool
         {
            Path = "PasswordPolicyRequireNonAlphanumeric",
            Label = "Require at least one character that is not a letter or digit",
            Blurb = "Anything that is not a letter or a digit counts, rather than a fixed list of punctuation, "
                  + "so a keyboard layout you do not have cannot make the rule unsatisfiable.",
            IniStore = iniStore_
         });
         policy.Settings.Add(new IniBool
         {
            Path = "PasswordPolicyRejectCommon",
            Label = "Reject the most commonly used passwords",
            Blurb = "A short built-in list - \"password\", \"123456\", \"changeme\" and about thirty more - compared "
                  + "without regard to case. It is deliberately not a \"top 10000\" list, which would need a data file "
                  + "and an update path; it catches the passwords that get typed while setting up a mailbox nobody "
                  + "comes back to.",
            IniStore = iniStore_
         });
         Tab("Password").Cards.Add(policy);

         // Separate from the complexity card above, because the two carry very
         // different risks and putting them under one heading would hide that.
         var ageing = Card("Reuse and expiry",
            "These belong together: expiry without history teaches people to alternate between two passwords, "
          + "which is worse than not expiring at all. History only ever refuses a CHANGE, at the moment somebody "
          + "is able to pick something else, so nobody is locked out by it. Expiry refuses a LOGON - read its "
          + "description before turning it on.");
         ageing.Settings.Add(new IniNumber
         {
            Path = "PasswordPolicyHistoryCount",
            Label = "Remember this many previous passwords (0 = no history)",
            Default = 0,
            Blurb = "The current password counts as the most recent one, so setting a password to itself is "
                  + "refused too - the repeat somebody is most likely to try.",
            IniStore = iniStore_
         });
         ageing.Settings.Add(new IniNumber
         {
            Path = "PasswordPolicyMaximumAgeDays",
            Label = "Expire passwords after (days; 0 = never)",
            Default = 0,
            Blurb = "READ THIS FIRST: hMailServer has no self-service password change - IMAP, POP3 and SMTP "
                  + "have no mechanism for it and there is no web page for users - so an expired password can "
                  + "only be reset by an administrator. Turning this on means someone has to be available to do "
                  + "that. Existing app passwords keep working, which is the one way an affected person can still "
                  + "collect mail; Active Directory accounts are exempt, because their password lives in the "
                  + "directory and expires by its policy, not this one. The clock started when this server was "
                  + "upgraded, so nobody is expired the moment you set this.",
            IniStore = iniStore_
         });
         Tab("Password").Cards.Add(ageing);

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

      // ---- live cache statistics ---------------------------------------------

      /// <summary>
      /// One line per cache: hit rate, memory in use, and its cap.
      ///
      /// Two things are stated rather than left to be inferred, because both would
      /// otherwise be read wrongly. Cache&lt;T&gt;::GetHitRate returns 0 when there
      /// have been no hits at all, which covers both "every lookup missed" and "no
      /// lookup has happened" - so a cache with no memory in use is called out as
      /// idle instead of as failing. And the counters are reset by SetTTL and by
      /// SetEnabled, not only by a restart, so the sample after any settings change
      /// on this page starts from zero.
      /// </summary>
      private static string DescribeCaches_()
      {
         dynamic cache = ServerSession.Current.Application.Settings.Cache;
         try
         {
            if (!(bool) cache.Enabled)
            {
               return "Caching is switched off, so every domain, account, alias and distribution-list lookup goes to "
                      + "the database. There are no hit rates to report.";
            }

            var lines = new List<string>();

            foreach ((string name, string rateProperty, string sizeProperty, string capProperty) in new[]
            {
               ("Domains", "DomainHitRate", "DomainCacheSizeKb", "DomainCacheMaxSizeKb"),
               ("Accounts", "AccountHitRate", "AccountCacheSizeKb", "AccountCacheMaxSizeKb"),
               ("Aliases", "AliasHitRate", "AliasCacheSizeKb", "AliasCacheMaxSizeKb"),
               ("Distribution lists", "DistributionListHitRate", "DistributionListCacheSizeKb", "DistributionListCacheMaxSizeKb")
            })
            {
               int rate = (int) ComProperty_(cache, rateProperty);
               int sizeKb = (int) ComProperty_(cache, sizeProperty);
               int capKb = (int) ComProperty_(cache, capProperty);

               string line = name + ": ";

               if (rate == 0 && sizeKb == 0)
               {
                  // "Nothing is cached" is what this state proves. It is NOT proof
                  // that nothing has been looked up: a lookup that found no such
                  // domain or account counts as a miss and caches nothing, so a
                  // server being probed for addresses it does not host sits here
                  // with plenty of traffic. Saying "no lookup has been made" would
                  // read as "this server is idle", which is the opposite of what
                  // that traffic pattern means.
                  line += "nothing is cached - either nothing has been looked up since the counters were reset, or "
                          + "the lookups that happened found nothing to cache.";
               }
               else
               {
                  line += rate + "% of lookups answered from memory, using "
                          + sizeKb.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " KB";

                  if (capKb > 0)
                  {
                     line += " of " + capKb.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " KB";
                     if (sizeKb >= capKb)
                        line += " - at its cap, so entries are being evicted";
                  }

                  line += ".";
               }

               lines.Add(line);
            }

            return string.Join("\r\n", lines);
         }
         finally
         {
            ServerSession.Release((object) cache);
         }
      }

      /// <summary>
      /// Reads one property off a COM object by name. Late-bound because the four
      /// caches differ only by the property name, and writing sixteen dynamic reads
      /// out longhand is where a copy-paste error puts the domain cache's hit rate
      /// beside the account cache's size.
      /// </summary>
      private static object ComProperty_(object owner, string name)
         => owner.GetType().InvokeMember(name, BindingFlags.GetProperty, null, owner, null);

      // ---- message index actions ---------------------------------------------
      //
      // MessageIndexing.Index() and .Clear() existed over COM and nowhere else, so
      // the two questions an administrator actually arrives with - "search finds
      // nothing" and "search finds messages that are gone" - had no answer short of
      // writing a script. Both actions report the counts on either side of the run
      // rather than only "done", because the counts ARE the answer: an index behind
      // the mailbox and an index ahead of it are different faults with different
      // fixes, and neither is visible from a success message.

      /// <summary>Total delivered messages and total indexed, or null when unreadable.</summary>
      private static (long Messages, long Indexed)? ReadIndexCounts_()
      {
         dynamic indexing = ServerSession.Current.Application.Settings.MessageIndexing;
         try
         {
            return ((long) (int) indexing.TotalMessageCount, (long) (int) indexing.TotalIndexedCount);
         }
         catch (Exception)
         {
            return null;
         }
         finally
         {
            ServerSession.Release((object) indexing);
         }
      }

      private static string DescribeIndexCounts_((long Messages, long Indexed)? counts)
      {
         if (counts == null)
            return "";

         long messages = counts.Value.Messages;
         long indexed = counts.Value.Indexed;

         string text = " " + indexed.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
                       + " of " + messages.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
                       + " messages are indexed.";

         if (indexed < messages)
            text += " The rest are still waiting; run this again or leave the schedule to catch up.";
         else if (indexed > messages)
            text += " There are more index entries than messages, which means entries for mail that no longer exists - "
                    + "discard and rebuild the index to clear them.";

         return text;
      }

      /// <summary>
      /// Whether the indexer is switched on, which decides whether either action
      /// below does anything at all.
      ///
      /// MessageIndexer's worker thread is started only when indexing is enabled
      /// (Application::Start, and Configuration::SetMessageIndexing). IndexNow()
      /// does not index - it sets an event that worker waits on - so with indexing
      /// off, "Index" signals a thread that is not running and reports success
      /// having done nothing. Saying so is the whole point of checking.
      /// </summary>
      private static bool IndexingEnabled_()
      {
         dynamic indexing = ServerSession.Current.Application.Settings.MessageIndexing;
         try
         {
            return (bool) indexing.Enabled;
         }
         catch (Exception)
         {
            // Unknown rather than off: refusing to act on a read that failed would
            // be worse than letting the attempt report its own error.
            return true;
         }
         finally
         {
            ServerSession.Release((object) indexing);
         }
      }

      private const string IndexingOffNote =
         "Message indexing is switched off, so there is no indexer running to ask. Tick \"Enable message indexing\" "
         + "above and save first - nothing was done.";

      private static (bool ok, string text) RunIndexNow_()
      {
         if (!IndexingEnabled_())
            return (false, IndexingOffNote);

         dynamic indexing = ServerSession.Current.Application.Settings.MessageIndexing;
         try
         {
            indexing.Index();
         }
         finally
         {
            ServerSession.Release((object) indexing);
         }

         // The counts are read after signalling but describe the state BEFORE the
         // pass, because the pass has not happened yet: IndexNow() sets an event and
         // returns, and the worker picks it up. Reporting them as "now" would be a
         // lie that looks like a stuck indexer, so the wording says which they are.
         return (true, "The indexer has been asked to run; it works in the background and the counts below will not "
                       + "move until it has. As things stand:" + DescribeIndexCounts_(ReadIndexCounts_()));
      }

      private static (bool ok, string text) RebuildIndex_()
      {
         if (!IndexingEnabled_())
            return (false, IndexingOffNote);

         (long Messages, long Indexed)? before = ReadIndexCounts_();

         string warning = "Discard the search index and rebuild it?\r\n\r\nNo mail is touched - the index is derived "
                          + "from it - but until the rebuild catches up, IMAP SEARCH will find less than it should.";

         if (before != null)
         {
            warning += "\r\n\r\n" + before.Value.Indexed.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
                       + " index entries will be discarded and rebuilt from "
                       + before.Value.Messages.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
                       + " messages.";
         }

         if (MessageBox.Show(warning, "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning)
             != MessageBoxResult.Yes)
         {
            return (true, "Nothing was changed.");
         }

         dynamic indexing = ServerSession.Current.Application.Settings.MessageIndexing;
         try
         {
            // Clear() is a DELETE and takes effect immediately; Index() only asks
            // the worker to start rebuilding. So the index really is empty when
            // this returns, and the rebuild really has not happened.
            indexing.Clear();
            indexing.Index();
         }
         finally
         {
            ServerSession.Release((object) indexing);
         }

         return (true, "The index has been emptied and the indexer asked to rebuild it. The rebuild runs in the "
                       + "background, in passes bounded by the limits above, so on a large mailstore it will take "
                       + "several passes - IMAP SEARCH finds less than it should until it finishes.");
      }

      /// <summary>
      /// Utilities.PerformMaintenance(eUpdateIMAPFolderUID), which had no GUI at all.
      ///
      /// What it repairs, from Maintenance::RecalculateFolderUID_: for every IMAP
      /// folder it sets foldercurrentuid to the highest message UID the folder
      /// actually holds, and only where the stored value is LOWER. A folder whose
      /// counter has fallen behind its messages hands the same UID out twice, and an
      /// IMAP client that caches by UID then shows one message in place of another -
      /// a data-integrity symptom with no obvious cause, and the reason this repair
      /// exists. It cannot lower a counter, so it cannot cause the fault it fixes.
      /// </summary>
      private static (bool ok, string text) RecalculateFolderUids_()
      {
         if (MessageBox.Show(
                "Recalculate the IMAP folder UID counters?\r\n\r\nFor every folder, the counter is moved up to the "
                + "highest message UID that folder actually holds. Counters that are already correct or ahead are "
                + "left alone, and no message is changed.\r\n\r\nRun this if clients report messages appearing under "
                + "the wrong UID, or after restoring a database.",
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
         {
            return (true, "Nothing was changed.");
         }

         dynamic utilities = ServerSession.Current.Application.Utilities;
         try
         {
            // eUpdateIMAPFolderUID = 1. The only operation the enum defines; the
            // server answers "Unknown maintenance operation" for anything else.
            ((object) utilities).GetType().InvokeMember(
               "PerformMaintenance", BindingFlags.InvokeMethod, null, (object) utilities, new object[] { 1 });
         }
         finally
         {
            ServerSession.Release((object) utilities);
         }

         return (true, "The folder UID counters have been recalculated. Clients that cached the old values will "
                       + "resynchronise on their next connection.");
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
