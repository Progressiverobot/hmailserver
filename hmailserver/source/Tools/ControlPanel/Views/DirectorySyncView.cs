using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using hMailServer.ControlPanel.Services;

using Typography = hMailServer.ControlPanel.Services.Typography;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The directory as an account source: which mailboxes the directory says should
   /// exist here, and the two buttons that show it and then do it.
   ///
   /// Separate from the LDAP authentication page on purpose, because they answer
   /// different questions. That page is about whether a person who already has a
   /// mailbox can log into it. This one is about which people have a mailbox at all -
   /// and it is the only page in the product that creates accounts in bulk, which is
   /// why almost everything below is about making what is about to happen visible
   /// before it happens.
   ///
   /// Three rules shape the whole page:
   ///
   /// APPLY IS LOCKED UNTIL A PREVIEW HAS BEEN READ. Not a dialog that can be clicked
   /// through - the button is disabled until a preview has run with the same options,
   /// and any change to those options locks it again. The server makes the preview
   /// trustworthy (one shared decider, so a preview cannot describe an action the
   /// apply would not take); this makes it unavoidable.
   ///
   /// THE DISABLE SWITCH IS PART OF THE PREVIEW. Marking absent accounts inactive is
   /// the one thing here that takes a working mailbox away, so it is passed to the
   /// preview as well as the apply. A preview run without it would report those
   /// accounts as "left alone" and the apply would then disable them - the preview and
   /// the apply answering different questions, which is the failure the shared decider
   /// exists to prevent and which this page must not reintroduce.
   ///
   /// THE ROWS THAT COST SOMETHING ARE COUNTED SEPARATELY. Four hundred rows of equal
   /// weight is a log, not a screen. Creates are what was asked for; skips are
   /// mailboxes that were expected and will not appear, and disables take one away.
   /// Those two get their own number, and it is the number the confirmation quotes.
   ///
   /// The parsing has no WPF in it and lives in <see cref="DirectorySyncReport"/>,
   /// where it is tested directly.
   /// </summary>
   public class DirectorySyncView : UserControl, IPageLifecycle
   {
      private const string IniSection = "LDAP";

      private readonly IniFeatureStore store_ = new IniFeatureStore();

      private readonly Wpf.Ui.Controls.TextBox filter_ = new();
      private readonly Wpf.Ui.Controls.TextBox mailAttribute_ = new();
      private readonly Wpf.Ui.Controls.TextBox usernameAttribute_ = new();
      private readonly Wpf.Ui.Controls.TextBox displayNameAttribute_ = new();
      private readonly Wpf.Ui.Controls.TextBox maxUsers_ = new();
      private readonly Wpf.Ui.Controls.TextBox scheduleMinutes_ = new();

      private readonly ComboBox domainChoice_ = new();
      private readonly CheckBox disableMissing_ = new();

      private Wpf.Ui.Controls.Button previewButton_;
      private Wpf.Ui.Controls.Button applyButton_;
      private Wpf.Ui.Controls.Button saveButton_;

      private readonly TextBlock stateText_ = new();
      private readonly TextBlock summaryText_ = new();
      private readonly TextBlock attentionText_ = new();
      private readonly StackPanel rows_ = new();
      private readonly TextBlock footerStatus_ = new();

      /// <summary>
      /// The options the last successful preview was run with, or null when no preview
      /// has been run since the page was opened or since an option changed. This is
      /// what Apply is gated on - not a flag, because a flag cannot tell whether the
      /// preview that was read is a preview of what is about to happen.
      /// </summary>
      private string previewedOptions_;

      private bool loading_;

      /// <summary>
      /// Whether the server's domain list was read in full. False means "unknown",
      /// never "none" - a distinction the state card depends on, because a server this
      /// page cannot talk to and a server with no linked domains are different
      /// problems with different fixes.
      /// </summary>
      private bool domainsRead_;

      public DirectorySyncView()
      {
         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "Directory synchronisation" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "Creates and updates mailboxes to match an LDAP directory. Only domains that have an Active "
                   + "Directory domain name set take part, so this cannot provision into a hosted domain by "
                   + "accident. Nothing here ever deletes an account or a message: the most it does is mark one "
                   + "inactive, and only when you ask it to."
         };
         subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
         heading.Children.Add(subtitle);
         root.Children.Add(heading);

         var cards = new StackPanel { Margin = new Thickness(0, 0, 12, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
         cards.Children.Add(BuildStateCard_());
         cards.Children.Add(BuildSelectionCard_());
         cards.Children.Add(BuildRunCard_());
         cards.Children.Add(BuildScheduleCard_());

         var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         Grid.SetRow(scroll, 1);
         root.Children.Add(scroll);

         FrameworkElement footer = BuildFooter_();
         Grid.SetRow(footer, 2);
         root.Children.Add(footer);

         Content = root;
      }

      public void OnEnter()
      {
         Load_();
      }

      public void OnLeave()
      {
      }

      // =========================================================================
      // Layout
      // =========================================================================

      private static Border Card_(string cardTitle, string blurb, out StackPanel content)
      {
         var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
         border.SetResourceReference(StyleProperty, "Card");

         var panel = new StackPanel();
         panel.Children.Add(new TextBlock
         {
            Text = cardTitle,
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
         });

         if (!string.IsNullOrEmpty(blurb))
         {
            panel.Children.Add(new TextBlock
            {
               Text = blurb,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 0, 0, 14)
            });
         }

         border.Child = panel;
         content = panel;
         return border;
      }

      private static FrameworkElement LabelledBox_(string label, Wpf.Ui.Controls.TextBox box,
         string automationId, string caption, string placeholder = "")
      {
         var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
         panel.Children.Add(new TextBlock { Text = label, FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 4) });

         box.FontSize = Typography.Body;
         box.MaxWidth = 620;
         box.MinWidth = 320;
         box.HorizontalAlignment = HorizontalAlignment.Left;
         box.PlaceholderText = placeholder;
         System.Windows.Automation.AutomationProperties.SetName(box, label);
         System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
         System.Windows.Automation.AutomationProperties.SetHelpText(box, caption);
         panel.Children.Add(box);

         panel.Children.Add(new TextBlock
         {
            Text = caption,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(0, 4, 0, 0)
         });

         return panel;
      }

      /// <summary>
      /// Whether provisioning can happen at all, worked out and stated before anything
      /// is pressed. Three things have to be true and each has a different fix, so the
      /// card names which one is missing rather than saying "not configured".
      /// </summary>
      private Border BuildStateCard_()
      {
         Border card = Card_("Can this server provision from the directory?", null, out StackPanel content);

         stateText_.FontSize = Typography.Body;
         stateText_.TextWrapping = TextWrapping.Wrap;
         System.Windows.Automation.AutomationProperties.SetAutomationId(stateText_, "dirsync-state");
         System.Windows.Automation.AutomationProperties.SetLiveSetting(stateText_,
            System.Windows.Automation.AutomationLiveSetting.Polite);
         content.Children.Add(stateText_);

         return card;
      }

      private Border BuildSelectionCard_()
      {
         Border card = Card_("Which directory entries become mailboxes",
            "These are read by the server from the [LDAP] section of hMailServer.INI, and re-read within two "
            + "seconds of a save, so no restart is needed. The defaults suit Active Directory; a directory that is "
            + "not Windows will not have sAMAccountName and needs the attribute names changed.",
            out StackPanel content);

         content.Children.Add(LabelledBox_("Search filter", filter_, "dirsync-filter",
            "The LDAP filter that selects the people who should have a mailbox. The default takes enabled person "
            + "objects that carry a mail address - which deliberately excludes disabled leavers, service accounts "
            + "and computers, all of which a broader filter would provision mailboxes for.",
            // The server's kDefaultSyncFilter, character for character, INCLUDING the
            // userAccountControl clause. That clause is the whole of what the caption
            // above promises, and an earlier version of this placeholder omitted it -
            // which made this control the only place in the product that stated the
            // default filter, and stated it wrong. An administrator narrowing the
            // filter to one OU starts from what they can see here; starting from a
            // version without the exclusion produces a filter that provisions a live,
            // mail-accepting mailbox for every disabled leaver the directory still
            // holds, and LdapSettings honours it because it is not empty.
            placeholder: "(&(objectClass=user)(objectCategory=person)(mail=*)"
                         + "(!(userAccountControl:1.2.840.113556.1.4.803:=2)))"));

         content.Children.Add(LabelledBox_("Address attribute", mailAttribute_, "dirsync-mail-attribute",
            "Which attribute holds the mailbox address. proxyAddresses is understood: its scheme prefix is removed "
            + "and the primary SMTP: value is preferred over the lower-case aliases.",
            placeholder: "mail"));

         content.Children.Add(LabelledBox_("Logon name attribute", usernameAttribute_, "dirsync-username-attribute",
            "Which attribute holds the name the person logs in with. This is what is stored against the account and "
            + "what the directory is asked about at every logon afterwards, so guessing it from the address is not "
            + "good enough - an entry that does not carry it is reported rather than provisioned.",
            placeholder: "sAMAccountName"));

         content.Children.Add(LabelledBox_("Display name attribute", displayNameAttribute_, "dirsync-displayname-attribute",
            "Split into first and last name, and only ever written to an account that has neither. A name typed here "
            + "by an administrator is never overwritten by the directory.",
            placeholder: "displayName"));

         content.Children.Add(LabelledBox_("Maximum entries to read", maxUsers_, "dirsync-max-users",
            "A ceiling on one pass, so a search base pointed one level too high cannot be answered by reading an "
            + "entire forest. If it is hit, the run says so and disables nothing at all - beyond the limit, an entry "
            + "that exists is indistinguishable from one that was deleted.",
            placeholder: "5000"));

         return card;
      }

      private Border BuildRunCard_()
      {
         Border card = Card_("Preview, then apply",
            "Preview changes nothing and reports exactly what Apply would do - they share one decider in the "
            + "server, so a preview cannot describe an action the apply would not take. Apply stays disabled until "
            + "a preview has been run with the options as they stand.",
            out StackPanel content);

         var domainPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
         domainPanel.Children.Add(new TextBlock
         {
            Text = "Domain",
            FontSize = Typography.Body,
            Margin = new Thickness(0, 0, 0, 4)
         });
         domainChoice_.MinWidth = 320;
         domainChoice_.MaxWidth = 620;
         domainChoice_.HorizontalAlignment = HorizontalAlignment.Left;
         System.Windows.Automation.AutomationProperties.SetName(domainChoice_, "Which domain to synchronise");
         System.Windows.Automation.AutomationProperties.SetAutomationId(domainChoice_, "dirsync-domain");
         domainChoice_.SelectionChanged += (s, e) => InvalidatePreview_();
         domainPanel.Children.Add(domainChoice_);
         domainPanel.Children.Add(new TextBlock
         {
            Text = "Only domains with an Active Directory domain name set are listed - that field is what links a "
                   + "mail domain to a directory. Bringing one domain over at a time is the sane way to do this the "
                   + "first time.",
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(0, 4, 0, 0)
         });
         content.Children.Add(domainPanel);

         disableMissing_.Content = "Also mark accounts inactive when their directory entry has gone";
         disableMissing_.FontSize = Typography.Body;
         disableMissing_.Margin = new Thickness(0, 0, 0, 4);
         System.Windows.Automation.AutomationProperties.SetName(disableMissing_,
            "Also mark accounts inactive when their directory entry has gone");
         System.Windows.Automation.AutomationProperties.SetAutomationId(disableMissing_, "dirsync-disable-missing");
         System.Windows.Automation.AutomationProperties.SetHelpText(disableMissing_,
            "Off by default. An account that stops being visible is far more often a filter that changed than a "
            + "person who left, and the two mistakes do not cost the same. No message is ever deleted and no "
            + "account is removed - re-enabling one restores everything.");
         disableMissing_.Checked += (s, e) => InvalidatePreview_();
         disableMissing_.Unchecked += (s, e) => InvalidatePreview_();
         content.Children.Add(disableMissing_);

         content.Children.Add(new TextBlock
         {
            Text = "Off by default. An account that stops being visible in the directory is far more often a search "
                   + "that changed than a person who left, and the two mistakes do not cost the same. Nothing is "
                   + "disabled at all when the enumeration was cut short or came back empty.",
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(24, 0, 0, 14)
         });

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

         previewButton_ = new Wpf.Ui.Controls.Button
         {
            Content = "Preview",
            Margin = new Thickness(0, 0, 8, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary
         };
         System.Windows.Automation.AutomationProperties.SetName(previewButton_,
            "Show what a synchronisation would do, changing nothing");
         System.Windows.Automation.AutomationProperties.SetAutomationId(previewButton_, "dirsync-preview");
         previewButton_.Click += async (s, e) => await RunAsync_(false);
         buttons.Children.Add(previewButton_);

         applyButton_ = new Wpf.Ui.Controls.Button { Content = "Apply", IsEnabled = false };
         System.Windows.Automation.AutomationProperties.SetName(applyButton_,
            "Create and update the accounts the preview listed");
         System.Windows.Automation.AutomationProperties.SetAutomationId(applyButton_, "dirsync-apply");
         applyButton_.Click += async (s, e) => await ApplyAsync_();
         buttons.Children.Add(applyButton_);

         content.Children.Add(buttons);

         summaryText_.FontSize = Typography.Body;
         summaryText_.TextWrapping = TextWrapping.Wrap;
         summaryText_.Margin = new Thickness(0, 0, 0, 4);
         System.Windows.Automation.AutomationProperties.SetAutomationId(summaryText_, "dirsync-summary");
         System.Windows.Automation.AutomationProperties.SetLiveSetting(summaryText_,
            System.Windows.Automation.AutomationLiveSetting.Polite);
         content.Children.Add(summaryText_);

         attentionText_.FontSize = Typography.Caption;
         attentionText_.TextWrapping = TextWrapping.Wrap;
         attentionText_.Margin = new Thickness(0, 0, 0, 10);
         System.Windows.Automation.AutomationProperties.SetAutomationId(attentionText_, "dirsync-attention");
         content.Children.Add(attentionText_);

         rows_.Margin = new Thickness(0, 4, 0, 0);
         content.Children.Add(rows_);

         return card;
      }

      private Border BuildScheduleCard_()
      {
         Border card = Card_("Unattended synchronisation",
            "A schedule creates and updates accounts with nobody watching. It never disables one, whatever the "
            + "checkbox above is set to - departures are left to a person who has read a preview. Switch this on "
            + "only after a preview has told you what it is going to do.",
            out StackPanel content);

         content.Children.Add(LabelledBox_("Run every (minutes)", scheduleMinutes_, "dirsync-schedule",
            "0 switches the schedule off, which is the default and is what every existing installation stays at. "
            + "Values between 1 and 14 are treated as 15, and anything over a week as a week. This one needs a "
            + "service restart: the task is registered when the server starts.",
            placeholder: "0"));

         return card;
      }

      private FrameworkElement BuildFooter_()
      {
         var footer = new Grid { Margin = new Thickness(0, 14, 12, 0) };

         footerStatus_.VerticalAlignment = VerticalAlignment.Center;
         footerStatus_.FontSize = Typography.Caption;
         footerStatus_.TextWrapping = TextWrapping.Wrap;
         footerStatus_.MaxWidth = 520;
         footerStatus_.HorizontalAlignment = HorizontalAlignment.Left;
         footerStatus_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         footer.Children.Add(footerStatus_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

         var reload = new Wpf.Ui.Controls.Button { Content = "Reload", Margin = new Thickness(0, 0, 8, 0) };
         System.Windows.Automation.AutomationProperties.SetName(reload, "Re-read these settings from hMailServer.INI");
         System.Windows.Automation.AutomationProperties.SetAutomationId(reload, "dirsync-reload");
         reload.Click += (s, e) => Load_();
         buttons.Children.Add(reload);

         saveButton_ = new Wpf.Ui.Controls.Button
         {
            Content = "Save changes",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary
         };
         System.Windows.Automation.AutomationProperties.SetName(saveButton_, "Save these settings to hMailServer.INI");
         System.Windows.Automation.AutomationProperties.SetAutomationId(saveButton_, "dirsync-save");
         saveButton_.Click += (s, e) => Save_();
         buttons.Children.Add(saveButton_);

         footer.Children.Add(buttons);
         return footer;
      }

      // =========================================================================
      // Reading and writing the [LDAP] section
      // =========================================================================
      //
      // The same constraint LdapSettingsView carries and for the same reason:
      // IniFeatureStore writes [Settings], and these keys live in [LDAP], so a write
      // through it would land where the server never looks. The COM ini accessors are
      // section-less for the same reason, so these editors need the file itself.
      //
      // Preview and Apply do NOT: they are COM calls that run inside the server. So a
      // Control Panel that cannot reach the INI can still run a synchronisation - it
      // just cannot change what the synchronisation selects.

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
         StringBuilder result, int size, string filePath);

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private string IniRead_(string key, string fallback)
      {
         if (!store_.IsAvailable)
            return fallback;

         // 4096, the same buffer LdapSettings::ReadString_ uses, so this page sees
         // exactly the value - and exactly the truncation - the server sees.
         var buffer = new StringBuilder(4096);
         GetPrivateProfileString(IniSection, key, fallback, buffer, buffer.Capacity, store_.IniPath);
         return buffer.ToString().Trim();
      }

      private void Load_()
      {
         loading_ = true;

         try
         {
            filter_.Text = IniRead_("SyncFilter", "");
            mailAttribute_.Text = IniRead_("SyncMailAttribute", "mail");
            usernameAttribute_.Text = IniRead_("SyncUsernameAttribute", "sAMAccountName");
            displayNameAttribute_.Text = IniRead_("SyncDisplayNameAttribute", "displayName");
            maxUsers_.Text = IniRead_("SyncMaxUsers", "5000");
            scheduleMinutes_.Text = IniRead_("SyncScheduleMinutes", "0");

            bool editable = store_.IsAvailable;
            filter_.IsEnabled = editable;
            mailAttribute_.IsEnabled = editable;
            usernameAttribute_.IsEnabled = editable;
            displayNameAttribute_.IsEnabled = editable;
            maxUsers_.IsEnabled = editable;
            scheduleMinutes_.IsEnabled = editable;
            if (saveButton_ != null)
               saveButton_.IsEnabled = editable;

            LoadDomains_();
            RefreshState_();

            footerStatus_.Text = editable
               ? ""
               : "hMailServer.INI cannot be reached from this computer, so these selections cannot be edited here. "
                 + "Preview and Apply still work: they run inside the server.";
         }
         finally
         {
            loading_ = false;
         }

         InvalidatePreview_();
      }

      private void Save_()
      {
         if (!store_.IsAvailable)
            return;

         try
         {
            WritePrivateProfileString(IniSection, "SyncFilter", filter_.Text.Trim(), store_.IniPath);
            WritePrivateProfileString(IniSection, "SyncMailAttribute", mailAttribute_.Text.Trim(), store_.IniPath);
            WritePrivateProfileString(IniSection, "SyncUsernameAttribute", usernameAttribute_.Text.Trim(), store_.IniPath);
            WritePrivateProfileString(IniSection, "SyncDisplayNameAttribute", displayNameAttribute_.Text.Trim(), store_.IniPath);
            WritePrivateProfileString(IniSection, "SyncMaxUsers", maxUsers_.Text.Trim(), store_.IniPath);
            WritePrivateProfileString(IniSection, "SyncScheduleMinutes", scheduleMinutes_.Text.Trim(), store_.IniPath);

            // Flush the cache the API keeps, so the value is on disk before the server
            // next compares the file's write time.
            WritePrivateProfileString(null, null, null, store_.IniPath);

            footerStatus_.Text = "Saved. The server picks the selection up within two seconds; a change to the "
                                 + "schedule needs a service restart.";

            // A saved change alters what a run would select, so a preview taken before
            // it no longer describes what Apply would do.
            InvalidatePreview_();
         }
         catch (Exception ex)
         {
            footerStatus_.Text = "Could not save: " + ex.Message;
         }
      }

      // =========================================================================
      // The server side
      // =========================================================================

      /// <summary>
      /// Fills the domain list with the domains that are actually eligible.
      ///
      /// Listing every domain would be the wrong help: a domain with no Active
      /// Directory domain name cannot be provisioned into, and choosing one would
      /// produce a run that reports every entry as skipped. Listing only the linked
      /// ones makes the prerequisite visible as an absence.
      /// </summary>
      private void LoadDomains_()
      {
         domainChoice_.Items.Clear();
         domainChoice_.Items.Add("All linked domains");
         domainsRead_ = false;

         dynamic domains = null;

         try
         {
            domains = ServerSession.Current.Application.Domains;

            int count = (int) domains.Count;

            for (int i = 0; i < count; i++)
            {
               dynamic domain = null;

               try
               {
                  domain = domains.Item[i];

                  string linked = (string) domain.ADDomainName;

                  if (!string.IsNullOrEmpty(linked) && linked.Trim().Length > 0)
                     domainChoice_.Items.Add((string) domain.Name);
               }
               catch
               {
                  // One domain that cannot be read costs that domain's row, not the
                  // list. A page that threw here would be unusable because of a single
                  // malformed record.
               }
               finally
               {
                  ServerSession.Release((object) domain);
               }
            }

            // Set only after the whole walk, so a connection that died half way
            // through leaves this false rather than reporting a partial list as a
            // complete one. A page that said "no domain is linked" on the strength of
            // half a list would be sending an administrator to fix something that is
            // already correct.
            domainsRead_ = true;
         }
         catch
         {
            // No server connection. The state card says so, and says it is a
            // connection problem rather than a configuration one; the list keeps its
            // "All" entry so the control is never empty.
         }
         finally
         {
            ServerSession.Release((object) domains);
         }

         domainChoice_.SelectedIndex = 0;
      }

      /// <summary>
      /// Says whether a run can do anything, and if not, which of the three
      /// prerequisites is missing. Each has a different fix and a different page.
      /// </summary>
      private void RefreshState_()
      {
         int linkedDomains = Math.Max(0, domainChoice_.Items.Count - 1);

         // Two things this card must never do, both of which are the same mistake:
         // report an unread value as a configured one. IniRead_ answers its fallback
         // when the INI cannot be reached, so "Enabled" reads as 0 on a Control Panel
         // running on another machine - and saying "directory access is switched off"
         // there would send an administrator to turn on something that is already on.
         // The same for the domain list: a server this page cannot talk to has no
         // readable domains, which is not the same fact as having no linked ones.
         if (!store_.IsAvailable)
         {
            stateText_.Text = "Cannot tell from here. hMailServer.INI is not reachable from this computer, so the "
                              + "[LDAP] section cannot be read - and it is that section, not anything on this page, "
                              + "which decides whether a run can happen. Press Preview: it runs inside the server "
                              + "and will say exactly what is missing, if anything is.";
            stateText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return;
         }

         if (!domainsRead_)
         {
            stateText_.Text = "Cannot tell from here - the server's domain list could not be read, so which domains "
                              + "are linked to a directory is unknown. That is a connection problem rather than a "
                              + "configuration one.";
            stateText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return;
         }

         bool ldapOn = string.Equals(IniRead_("Enabled", "0"), "1", StringComparison.Ordinal);
         string searchBase = IniRead_("SearchBase", "");

         var problems = new List<string>();

         if (!ldapOn)
            problems.Add("directory access is switched off ([LDAP] Enabled=0 - the Directory authentication page turns it on)");

         if (searchBase.Length == 0)
            problems.Add("no search base is set, so there is nowhere to enumerate from");

         if (linkedDomains == 0)
            problems.Add("no domain on this server has an Active Directory domain name set, so no domain is eligible "
                         + "(set it on the domain's own page)");

         if (problems.Count == 0)
         {
            stateText_.Text = "Yes. " + linkedDomains + " domain(s) are linked to a directory and will take part. "
                              + "Run a preview first: the shape of a directory is rarely what anyone expects, and the "
                              + "skipped rows are where the surprises are.";
            stateText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            return;
         }

         stateText_.Text = "Not yet - " + string.Join("; ", problems) + ".";
         stateText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
      }

      /// <summary>
      /// The identity of a run, as a string. Apply is enabled only while this matches
      /// what the last preview was run with, so changing the domain, the disable
      /// switch or any selection locks Apply again without anyone having to remember
      /// to.
      /// </summary>
      private string CurrentOptions_()
      {
         // Read from the INI, deliberately, and not from the editors above.
         //
         // A run uses what is SAVED - the server reads its own [LDAP] section - so an
         // edit that has not been saved changes nothing about what Apply would do, and
         // locking Apply for one would be telling the administrator that something
         // changed when it did not. A save does invalidate the preview, because the
         // values below then differ; Save_ also invalidates explicitly, so it holds
         // even for a save that writes the same values back.
         return (domainChoice_.SelectedIndex <= 0 ? "" : (string) domainChoice_.SelectedItem)
                + "|" + (disableMissing_.IsChecked == true ? "1" : "0")
                + "|" + IniRead_("SyncFilter", "")
                + "|" + IniRead_("SyncMailAttribute", "mail")
                + "|" + IniRead_("SyncUsernameAttribute", "sAMAccountName")
                + "|" + IniRead_("SyncDisplayNameAttribute", "displayName")
                + "|" + IniRead_("SyncMaxUsers", "5000")
                + "|" + IniRead_("Enabled", "0")
                + "|" + IniRead_("SearchBase", "");
      }

      private void InvalidatePreview_()
      {
         if (loading_)
            return;

         previewedOptions_ = null;

         if (applyButton_ != null)
            applyButton_.IsEnabled = false;
      }

      private async Task RunAsync_(bool apply)
      {
         string domain = domainChoice_.SelectedIndex <= 0 ? "" : (string) domainChoice_.SelectedItem;
         bool disableMissing = disableMissing_.IsChecked == true;
         string options = CurrentOptions_();

         previewButton_.IsEnabled = false;
         applyButton_.IsEnabled = false;
         summaryText_.Text = apply ? "Applying..." : "Reading the directory...";
         attentionText_.Text = "";
         rows_.Children.Clear();

         try
         {
            // Off the UI thread: a directory read is a network round trip per page, and
            // a large directory takes long enough that a frozen window would look like
            // a hang.
            Tuple<bool, string> outcome = await Task.Run(() => Invoke_(domain, apply, disableMissing));

            DirectorySyncReport report = DirectorySyncReport.Parse(outcome.Item1, outcome.Item2);

            Render_(report, apply);

            // Only a SUCCESSFUL preview unlocks Apply, and only for the options it was
            // run with. A refused preview tells the administrator nothing about what an
            // apply would do, so it must not be treated as one that was read.
            if (!apply && report.Succeeded)
            {
               previewedOptions_ = options;
               applyButton_.IsEnabled = true;
            }
            else if (apply)
            {
               // The world has changed, so the preview that was read no longer
               // describes what another apply would do.
               previewedOptions_ = null;
            }
         }
         catch (Exception ex)
         {
            summaryText_.Text = "The run failed: " + ServerSession.DescribeComError(ex);
            previewedOptions_ = null;
         }
         finally
         {
            previewButton_.IsEnabled = true;
         }
      }

      private async Task ApplyAsync_()
      {
         // The gate. Not a courtesy check - the button is disabled outside this
         // condition too, and this catches the case where the options changed between
         // the click and the handler.
         if (previewedOptions_ == null || previewedOptions_ != CurrentOptions_())
         {
            summaryText_.Text = "Run a preview first: the options have changed since the last one, so it no longer "
                                + "describes what this would do.";
            applyButton_.IsEnabled = false;
            return;
         }

         MessageBoxResult answer = MessageBox.Show(
            "This creates and updates mailboxes on the server to match the directory"
            + (disableMissing_.IsChecked == true
               ? ", and marks accounts inactive when their directory entry has gone."
               : ". No account will be disabled.")
            + "\r\n\r\nNo message is deleted and no account is removed. Continue?",
            "Apply directory synchronisation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

         if (answer != MessageBoxResult.OK)
            return;

         // Re-checked AFTER the dialog, not only before it.
         //
         // MessageBox.Show blocks this method but not the world: it pumps messages, so
         // while it is up the domain list can be reloaded, the checkbox can be toggled
         // by a keyboard accelerator, and - the case that actually matters - somebody
         // else can edit hMailServer.INI, which CurrentOptions_ reads. The gate above
         // ran against the options as they were when the button was clicked; this one
         // runs against the options the run is about to use. Without it, the whole
         // point of the gate is available to be raced by the confirmation intended to
         // make it safer.
         if (previewedOptions_ != CurrentOptions_())
         {
            summaryText_.Text = "Nothing was applied: the options changed while the confirmation was open, so the "
                                + "preview no longer describes what this would do. Run the preview again.";
            applyButton_.IsEnabled = false;
            previewedOptions_ = null;
            return;
         }

         await RunAsync_(true);
      }

      /// <summary>The COM call itself, on a background thread.</summary>
      private static Tuple<bool, string> Invoke_(string domain, bool apply, bool disableMissing)
      {
         dynamic settings = null;

         try
         {
            settings = ServerSession.Current.Application.Settings;

            // Written out long-hand rather than as a ternary, and `text` initialised
            // rather than merely declared. Both are because the call is dispatched
            // dynamically: definite-assignment analysis cannot see through a dynamic
            // invocation, so an out parameter it fills does not count as assigned at
            // compile time.
            string text = string.Empty;
            bool ok;

            if (apply)
               ok = (bool) settings.ApplyDirectorySync(domain, disableMissing, out text);
            else
               ok = (bool) settings.PreviewDirectorySync(domain, disableMissing, out text);

            return Tuple.Create(ok, text ?? string.Empty);
         }
         finally
         {
            ServerSession.Release((object) settings);
         }
      }

      private void Render_(DirectorySyncReport report, bool applied)
      {
         rows_.Children.Clear();

         summaryText_.Text = report.Summary;

         if (!report.Succeeded)
         {
            // "Nothing was changed" only when there is genuinely nothing to show. A
            // failed run normally means the run never started - LDAP off, no search
            // base, the directory unreachable - and then this is true and the row list
            // is empty anyway. But it is NOT safe to assert as a rule: a run can fail
            // partway with rows already carried out, and printing "nothing was changed"
            // over a list that says otherwise is the worst thing this page could do.
            // So the claim is made only when the list is empty, and the rows are drawn
            // either way.
            attentionText_.Text = report.Rows.Count == 0
               ? (applied ? "Nothing was changed." : "Nothing was read.")
               : (applied
                  ? "The run did not finish. The rows below say what had already been done before it stopped - "
                    + "read them before running it again."
                  : "The run did not finish. What was read before it stopped is below.");

            RenderRows_(report);
            return;
         }

         int attention = report.AttentionCount;

         if (applied)
         {
            int failed = 0;
            int done = 0;

            foreach (DirectorySyncRow row in report.Rows)
            {
               if (row.Outcome == DirectorySyncOutcome.Failed) failed++;
               else if (row.Outcome == DirectorySyncOutcome.Done) done++;
            }

            // Deliberately phrased around what FAILED rather than what succeeded. A run
            // reports success when it completed, not when every account was created -
            // Apply_ counts individual refusals in `failed` and leaves the run itself
            // successful - so "applied" on its own tells an administrator very little.
            attentionText_.Text = failed == 0
               ? done + " change(s) carried out, none failed."
               : failed + " of " + (failed + done) + " change(s) could not be carried out and are listed first, "
                 + "with the server's reason on each. The rest were applied.";
         }
         else
         {
            attentionText_.Text = attention == 0
               ? "Nothing here needs a decision: no mailbox is being skipped and none is being disabled."
               : attention + " row(s) need reading before you apply - a skipped entry is a mailbox somebody expects "
                 + "and will not get, and a disabled one is a mailbox being taken away.";
         }

         RenderRows_(report);
      }

      private void RenderRows_(DirectorySyncReport report)
      {
         // The rows that cost something first. Four hundred lines of equal weight is a
         // log; the six that matter have to be at the top or they are not read at all.
         foreach (DirectorySyncRow row in report.Rows)
         {
            if (row.NeedsAttention)
               rows_.Children.Add(Row_(row, true));
         }

         foreach (DirectorySyncRow row in report.Rows)
         {
            if (!row.NeedsAttention)
               rows_.Children.Add(Row_(row, false));
         }

         if (report.SkipBreakdown.Count == 0)
            return;

         // The aggregate, drawn as an aggregate. It used to arrive here as extra rows
         // headed "(no address)" - the same words the server uses for a real directory
         // entry with no mail attribute - so a count of skipped entries read as two more
         // unidentifiable mailboxes. It is also placed HERE, directly under the rows it
         // summarises, rather than trailing four hundred lines below them.
         rows_.Children.Add(new TextBlock
         {
            Text = "Skipped, by reason",
            FontSize = Typography.Body,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 4)
         });

         foreach (string line in report.SkipBreakdown)
         {
            // The server writes "22\tthere is no domain of that name on this server".
            // Split so the count and the reason are two readable pieces rather than one
            // line with an unexplained gap in it.
            string[] parts = line.Split('\t');

            rows_.Children.Add(new TextBlock
            {
               Text = parts.Length >= 2
                  ? parts[0].Trim() + "  -  " + string.Join(" ", parts, 1, parts.Length - 1).Trim()
                  : line,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.75,
               Margin = new Thickness(0, 0, 0, 2)
            });
         }
      }

      private static FrameworkElement Row_(DirectorySyncRow row, bool emphasise)
      {
         var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

         var head = new TextBlock
         {
            Text = (row.Address.Length == 0 ? "(no address)" : row.Address) + "  -  " + row.Action,
            FontSize = Typography.Body,
            FontWeight = emphasise ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap
         };
         panel.Children.Add(head);

         panel.Children.Add(new TextBlock
         {
            Text = row.Detail,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
         });

         return panel;
      }
   }
}
