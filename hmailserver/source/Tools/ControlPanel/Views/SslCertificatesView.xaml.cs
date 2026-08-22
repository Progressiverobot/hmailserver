// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using hMailServer.ControlPanel.Services;

// System.Windows.Documents is needed for Run/Inlines and declares its own
// Typography; the alias picks the Control Panel's type scale, the same way
// DnsRecordsView resolves the same clash.
using Typography = hMailServer.ControlPanel.Services.Typography;

// The status marks are System.Windows.Shapes.Path; the implicit usings bring in
// System.IO.Path. The alias picks the drawing one.
using Path = System.Windows.Shapes.Path;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The SSL certificates list, with what this machine can actually verify
   /// about each entry rather than just the two file paths: does the
   /// certificate file exist and parse, when does it expire and how many days
   /// remain, does the key file exist, is it encrypted, and does the key belong
   /// to the certificate. The checks live in
   /// <see cref="CertificateInspector"/> and run off the UI thread; against a
   /// remote server they honestly degrade to "cannot be checked from here"
   /// instead of showing a false green.
   ///
   /// This page is also the editor for the one property of a certificate that
   /// is a secret: <c>PrivateKeyPassword</c>, the passphrase of an encrypted
   /// key file (COM, database version 6009). The stored value is
   /// DPAPI-protected at rest and its COM getter requires the
   /// server-administrator session; this page only ever tests whether it is
   /// EMPTY and never displays it. Without it, an encrypted key does not load
   /// (server error 6170) and every TLS port using the certificate does not
   /// start - a mail outage caused by a configuration mistake, which is why the
   /// wording here is blunt.
   /// </summary>
   public partial class SslCertificatesView : UserControl, IPageLifecycle
   {
      /// <summary>Whether a passphrase is stored on the server for a certificate.</summary>
      internal enum StoredPassphrase
      {
         /// <summary>Could not be read - the getter requires the server-administrator session.</summary>
         Unknown,
         NotSet,
         Set
      }

      public class CertRow
      {
         public string Name { get; set; }
         public string CertificateFile { get; set; }
         public string PrivateKeyFile { get; set; }

         /// <summary>
         /// The short status words, public so the search box (which reflects
         /// over public properties) finds rows by state: typing "expired"
         /// filters to the expired certificates.
         /// </summary>
         public string CertificateState { get; set; }
         public string PrivateKeyState { get; set; }

         // Internal on purpose: ListSearch reflects over public properties, and
         // a finding's ToString would only add noise to the search.
         internal int Id;
         internal StoredPassphrase Passphrase;
         internal CertificateHealth Health;

         internal CertificateFinding CertBadge;        // the "Certificate" grid cell
         internal CertificateFinding KeyBadge;         // the "Private key" grid cell
         internal CertificateFinding PassphraseLine;   // details pane
      }

      /// <summary>
      /// Guards against a slow inspection finishing after a newer reload: only
      /// the results of the latest reload may reach the grid.
      /// </summary>
      private int reloadVersion_;

      public SslCertificatesView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      // ---- loading ------------------------------------------------------------

      private async void Reload(int reselectId = 0)
      {
         int version = ++reloadVersion_;

         var rows = new List<CertRow>();
         string error = null;

         try
         {
            ReadRowsFromCom(rows);
         }
         catch (Exception ex)
         {
            rows.Clear();
            error = ServerSession.DescribeComError(ex);
         }

         bool local = CertificateInspector.SessionIsLocal(ServerSession.Current?.Host);

         if (error == null && rows.Count > 0)
         {
            // The file checks are plain disk reads, but the paths may point at a
            // network share, so they stay off the UI thread. Inspect never throws.
            await Task.Run(() =>
            {
               foreach (CertRow row in rows)
                  row.Health = CertificateInspector.Inspect(row.CertificateFile, row.PrivateKeyFile, local);
            });
         }

         if (version != reloadVersion_)
            return;   // superseded by a newer reload

         foreach (CertRow row in rows)
            ComposeFindings(row);

         CertGrid.ItemsSource = rows;
         ListSearch.Apply(CertGrid, SearchBox.Text);
         StatusText.Show(EmptyStatus, rows.Count, error, "No certificates available yet.");

         if (reselectId != 0)
         {
            CertRow reselect = rows.FirstOrDefault(row => row.Id == reselectId);
            if (reselect != null)
               CertGrid.SelectedItem = reselect;
         }

         UpdateDetailsPane();
      }

      private static void ReadRowsFromCom(List<CertRow> rows)
      {
         dynamic certs = ServerSession.Current.Application.Settings.SSLCertificates;
         try
         {
            int count = (int) certs.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic cert = certs.Item[i];
               try
               {
                  var row = new CertRow
                  {
                     Id = (int) cert.ID,
                     Name = (string) cert.Name,
                     CertificateFile = (string) cert.CertificateFile,
                     PrivateKeyFile = (string) cert.PrivateKeyFile
                  };

                  // Only the EMPTINESS of the stored passphrase is tested; the
                  // value itself is never rendered anywhere on this page. The
                  // getter is gated on the server-administrator session (it
                  // returns a decryptable secret), so a rejection here degrades
                  // to "unknown" rather than to a guess.
                  try
                  {
                     string password = (string) cert.PrivateKeyPassword;
                     row.Passphrase = string.IsNullOrEmpty(password) ? StoredPassphrase.NotSet : StoredPassphrase.Set;
                  }
                  catch (Exception)
                  {
                     row.Passphrase = StoredPassphrase.Unknown;
                  }

                  rows.Add(row);
               }
               finally
               {
                  ServerSession.Release((object) cert);
               }
            }
         }
         finally
         {
            ServerSession.Release((object) certs);
         }
      }

      /// <summary>
      /// Combines the file facts from the inspector with the COM facts this view
      /// holds (is a passphrase stored) into the findings the page renders.
      /// </summary>
      private static void ComposeFindings(CertRow row)
      {
         CertificateHealth health = row.Health ?? new CertificateHealth();

         row.CertBadge = health.CertificateFile;
         row.PassphraseLine = PassphraseFinding(row.Passphrase, health.KeyIsEncrypted);

         // The grid's "Private key" cell shows whichever of the key findings is
         // asking for the most attention; the details pane always shows all of
         // them. StatusLevel is ordered by attention, so the comparison is the
         // enum itself.
         row.KeyBadge = WorstOf(row.PassphraseLine, health.Pair, health.PrivateKeyFile);

         row.CertificateState = row.CertBadge?.Word ?? "";
         row.PrivateKeyState = row.KeyBadge?.Word ?? "";
      }

      /// <summary>
      /// What the stored-passphrase state means for THIS key file. Verified
      /// against SslContextInitializer::GetPassword_: an encrypted key with no
      /// stored passphrase is error 6170, "The key was not loaded", and the
      /// listener does not start; a stored passphrase is only proven right when
      /// the server loads the key.
      /// </summary>
      private static CertificateFinding PassphraseFinding(StoredPassphrase state, bool? keyIsEncrypted)
      {
         switch (state)
         {
            case StoredPassphrase.Set:
               if (keyIsEncrypted == false)
               {
                  return new CertificateFinding(StatusLevel.Information, "Passphrase stored, key not encrypted",
                     "A passphrase is stored, but the key file is not encrypted, so it is never used. "
                     + "Harmless - it simply does nothing.");
               }
               return new CertificateFinding(StatusLevel.Good, "Passphrase stored",
                  "A passphrase is stored for this certificate. It is never displayed here - saving a new one "
                  + "replaces it. Whether it is the RIGHT passphrase is only proven when the server loads the "
                  + "key: after the next restart, check the application error log.");

            case StoredPassphrase.NotSet:
               if (keyIsEncrypted == true)
               {
                  return new CertificateFinding(StatusLevel.Critical, "Encrypted key, no passphrase",
                     "The key file is encrypted and no passphrase is stored, so the key will not load (server "
                     + "error 6170) and every TLS port using this certificate will not start - inbound mail on "
                     + "those ports stops. Enter the passphrase below, or replace the file with an unencrypted key.");
               }
               if (keyIsEncrypted == false)
               {
                  return new CertificateFinding(StatusLevel.Normal, "No passphrase needed",
                     "The key file is not encrypted, so no passphrase is needed and none is stored.");
               }
               return new CertificateFinding(StatusLevel.Information, "No passphrase stored",
                  "Whether the key file needs one cannot be checked from here. If it is encrypted, it will not "
                  + "load without a passphrase (server error 6170) and the TLS ports using this certificate "
                  + "will not start.");

            default:
               return new CertificateFinding(StatusLevel.Information, "Passphrase state unknown",
                  "Whether a passphrase is stored could not be read - reading it requires the "
                  + "server-administrator account. Saving a new one from here still works.");
         }
      }

      private static CertificateFinding WorstOf(params CertificateFinding[] findings)
      {
         CertificateFinding worst = findings.Where(f => f != null).MaxBy(f => f.Level);
         return worst ?? new CertificateFinding(StatusLevel.Information, "Not checked", "This entry was not checked.");
      }

      // ---- rendering ------------------------------------------------------------

      /// <summary>
      /// Colour, shape AND word - the same three channels every status badge in
      /// this application uses, so none of them is load-bearing on its own.
      /// Built in code because the brush must be a resource REFERENCE
      /// (ThemeTokens republishes the status brushes on every theme change; a
      /// bound brush would keep the old colour).
      /// </summary>
      private static FrameworkElement Badge(CertificateFinding finding)
      {
         StatusPresentation presentation = StatusSemantics.For(finding.Level);

         var row = new Grid();
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         var mark = new Path { Width = 11, Height = 11, Stretch = Stretch.Fill, Margin = new Thickness(0, 3, 6, 0), VerticalAlignment = VerticalAlignment.Top };
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
         row.Children.Add(mark);

         var text = new TextBlock { FontSize = Typography.Label, TextWrapping = TextWrapping.Wrap };
         var word = new Run(finding.Word) { FontWeight = FontWeights.SemiBold };
         word.SetResourceReference(TextElement.ForegroundProperty, presentation.BrushKey);
         text.Inlines.Add(word);

         // The cell is compact; the full sentence is in the details pane below
         // and, for convenience, on the tooltip. The accessible name carries all
         // of it, severity first.
         text.ToolTip = finding.Detail;
         System.Windows.Automation.AutomationProperties.SetName(text,
            presentation.SeverityWord + ". " + finding.Word + ". " + finding.Detail);

         Grid.SetColumn(text, 1);
         row.Children.Add(text);
         return row;
      }

      /// <summary>One captioned status sentence in the details pane.</summary>
      private static FrameworkElement DetailLine(string caption, CertificateFinding finding)
      {
         StatusPresentation presentation = StatusSemantics.For(finding.Level);

         var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         var mark = new Path { Width = 11, Height = 11, Stretch = Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
         row.Children.Add(mark);

         var text = new TextBlock { FontSize = Typography.Label, TextWrapping = TextWrapping.Wrap };
         text.Inlines.Add(new Run(caption + ": ") { FontWeight = FontWeights.SemiBold });
         var word = new Run(finding.Word + " — ") { FontWeight = FontWeights.SemiBold };
         word.SetResourceReference(TextElement.ForegroundProperty, presentation.BrushKey);
         text.Inlines.Add(word);
         text.Inlines.Add(new Run(finding.Detail));

         System.Windows.Automation.AutomationProperties.SetName(text,
            caption + ". " + presentation.SeverityWord + ". " + finding.Word + ". " + finding.Detail);

         Grid.SetColumn(text, 1);
         row.Children.Add(text);
         return row;
      }

      private void CertificateBadge_Loaded(object sender, RoutedEventArgs e)
      {
         if (sender is ContentControl cell && cell.DataContext is CertRow row && row.CertBadge != null)
            cell.Content = Badge(row.CertBadge);
      }

      private void KeyBadge_Loaded(object sender, RoutedEventArgs e)
      {
         if (sender is ContentControl cell && cell.DataContext is CertRow row && row.KeyBadge != null)
            cell.Content = Badge(row.KeyBadge);
      }

      private void CertGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
         => UpdateDetailsPane();

      private void UpdateDetailsPane()
      {
         if (CertGrid.SelectedItem is not CertRow row)
         {
            DetailsCard.Visibility = Visibility.Collapsed;
            return;
         }

         DetailsCard.Visibility = Visibility.Visible;
         DetailsTitle.Text = row.Name;
         PassphraseBox.Password = "";

         DetailsLines.Children.Clear();

         CertificateHealth health = row.Health ?? new CertificateHealth();
         if (health.CertificateFile != null)
            DetailsLines.Children.Add(DetailLine("Certificate file", health.CertificateFile));
         if (health.PrivateKeyFile != null)
            DetailsLines.Children.Add(DetailLine("Private key file", health.PrivateKeyFile));
         if (health.Pair != null)
            DetailsLines.Children.Add(DetailLine("Key matches certificate", health.Pair));
         if (row.PassphraseLine != null)
            DetailsLines.Children.Add(DetailLine("Passphrase", row.PassphraseLine));
      }

      // ---- the passphrase editor -------------------------------------------------

      private void SavePassphrase_Click(object sender, RoutedEventArgs e)
      {
         if (CertGrid.SelectedItem is not CertRow row)
            return;

         string passphrase = PassphraseBox.Password;
         if (string.IsNullOrEmpty(passphrase))
         {
            MessageBox.Show("Type the new passphrase first. To remove the stored one, use \"Remove stored passphrase\".",
               "Control Panel");
            return;
         }

         if (row.Passphrase == StoredPassphrase.Set &&
             MessageBox.Show("A passphrase is already stored for '" + row.Name + "'. Saving replaces it - the old one "
                + "cannot be recovered from here. Replace it?", "Control Panel",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
         {
            return;
         }

         if (WritePassphrase(row, passphrase, out string error))
         {
            PassphraseBox.Password = "";
            Reload(row.Id);
         }
         else
         {
            MessageBox.Show("Could not save the passphrase: " + error, "Control Panel");
         }
      }

      private void ClearPassphrase_Click(object sender, RoutedEventArgs e)
      {
         if (CertGrid.SelectedItem is not CertRow row)
            return;

         if (MessageBox.Show("Remove the stored passphrase for '" + row.Name + "'? If the key file is encrypted, "
               + "the key will stop loading at the next restart (server error 6170) and the TLS ports using this "
               + "certificate will not start.", "Control Panel",
               MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
         {
            return;
         }

         if (WritePassphrase(row, "", out string error))
            Reload(row.Id);
         else
            MessageBox.Show("Could not remove the passphrase: " + error, "Control Panel");
      }

      /// <summary>
      /// Writes the passphrase to one certificate, found by ID rather than by
      /// name because names are not guaranteed unique and a secret written to
      /// the wrong certificate would fail silently at the next restart.
      /// </summary>
      private static bool WritePassphrase(CertRow row, string passphrase, out string error)
      {
         error = null;
         dynamic certs = null;
         try
         {
            certs = ServerSession.Current.Application.Settings.SSLCertificates;
            int count = (int) certs.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic cert = certs.Item[i];
               try
               {
                  if ((int) cert.ID == row.Id)
                  {
                     cert.PrivateKeyPassword = passphrase;
                     cert.Save();
                     return true;
                  }
               }
               finally
               {
                  ServerSession.Release((object) cert);
               }
            }

            error = "the certificate no longer exists - reload the page.";
            return false;
         }
         catch (Exception ex)
         {
            error = ServerSession.DescribeComError(ex);
            return false;
         }
         finally
         {
            ServerSession.Release((object) certs);
         }
      }

      // ---- search / browse / add / delete ----------------------------------------

      private void Search_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(CertGrid, SearchBox.Text);

      private static string BrowsePem()
      {
         var dialog = new OpenFileDialog
         {
            Filter = "PEM files (*.pem;*.crt;*.key)|*.pem;*.crt;*.key|All files (*.*)|*.*"
         };
         return dialog.ShowDialog() == true ? dialog.FileName : null;
      }

      private void BrowseCert_Click(object sender, RoutedEventArgs e)
      {
         string file = BrowsePem();
         if (file != null)
            NewCertFile.Text = file;
      }

      private async void BrowseKey_Click(object sender, RoutedEventArgs e)
      {
         string file = BrowsePem();
         if (file == null)
            return;

         NewKeyFile.Text = file;

         // The one thing this page can find out for the administrator before it
         // hurts: whether the picked key file is passphrase-protected. Local
         // sessions only - a remote server's paths are not this machine's files.
         if (!CertificateInspector.SessionIsLocal(ServerSession.Current?.Host))
            return;

         bool? encrypted = await Task.Run(() =>
         {
            try
            {
               return File.Exists(file) ? CertificateInspector.LooksLikeEncryptedPem(file) : (bool?) null;
            }
            catch (Exception)
            {
               return null;
            }
         });

         if (encrypted == true)
         {
            ShowAddHint(StatusLevel.Warning,
               "This key file is encrypted (passphrase-protected). Enter its passphrase in the box below before "
               + "adding, or the key will not load (server error 6170) and the TLS ports using this certificate "
               + "will not start.");
         }
         else
         {
            AddHintRow.Visibility = Visibility.Collapsed;
         }
      }

      /// <summary>Colour, shape and word for the add-form hint, like every other status here.</summary>
      private void ShowAddHint(StatusLevel level, string message)
      {
         StatusPresentation presentation = StatusSemantics.For(level);
         ShapeMarkVisuals.ApplyMark(AddHintMark, presentation.Shape, presentation.BrushKey);

         AddHintText.Inlines.Clear();
         var word = new Run(presentation.SeverityWord + " — ") { FontWeight = FontWeights.SemiBold };
         word.SetResourceReference(TextElement.ForegroundProperty, presentation.BrushKey);
         AddHintText.Inlines.Add(word);
         AddHintText.Inlines.Add(new Run(message));
         System.Windows.Automation.AutomationProperties.SetName(AddHintText,
            presentation.SeverityWord + ". " + message);

         AddHintRow.Visibility = Visibility.Visible;
      }

      private async void Add_Click(object sender, RoutedEventArgs e)
      {
         string name = NewCertName.Text.Trim();
         string certFile = NewCertFile.Text.Trim();
         string keyFile = NewKeyFile.Text.Trim();
         string passphrase = NewKeyPassphrase.Password;

         if (name.Length == 0 || certFile.Length == 0 || keyFile.Length == 0)
         {
            MessageBox.Show("Name, certificate file and private key file are required.", "Control Panel");
            return;
         }

         // Catch the outage before it is configured: an encrypted key with no
         // passphrase will not load (server error 6170) and the ports using it
         // will not start. Only determinable when the file is on this machine.
         if (passphrase.Length == 0 && CertificateInspector.SessionIsLocal(ServerSession.Current?.Host))
         {
            bool? encrypted = await Task.Run(() =>
            {
               try
               {
                  return File.Exists(keyFile) ? CertificateInspector.LooksLikeEncryptedPem(keyFile) : (bool?) null;
               }
               catch (Exception)
               {
                  return null;
               }
            });

            if (encrypted == true &&
                MessageBox.Show("The private key file is encrypted (passphrase-protected) and no passphrase was "
                   + "entered. The certificate will be added, but its key will not load (server error 6170) and "
                   + "the TLS ports using it will not start until the passphrase is saved.\n\nAdd it anyway?",
                   "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
               return;
            }
         }

         // Current cannot be null here today (this page only exists after
         // sign-in and nothing ever clears the session), but this handler has
         // awaited since the click and is async void - if a sign-out path ever
         // arrives, failing soft here beats the crash dialog.
         ServerSession session = ServerSession.Current;
         if (session == null)
            return;

         dynamic certs = session.Application.Settings.SSLCertificates;
         try
         {
            dynamic cert = certs.Add();
            cert.Name = name;
            cert.CertificateFile = certFile;
            cert.PrivateKeyFile = keyFile;
            if (passphrase.Length > 0)
               cert.PrivateKeyPassword = passphrase;
            cert.Save();
            ServerSession.Release((object) cert);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the certificate: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release((object) certs);
         }

         NewCertName.Text = NewCertFile.Text = NewKeyFile.Text = "";
         NewKeyPassphrase.Password = "";
         AddHintRow.Visibility = Visibility.Collapsed;
         Reload();
      }

      private void Delete_Click(object sender, RoutedEventArgs e)
      {
         if (CertGrid.SelectedItem is not CertRow row)
            return;

         if (MessageBox.Show("Delete certificate '" + row.Name + "'?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic certs = ServerSession.Current.Application.Settings.SSLCertificates;
         try
         {
            int count = (int) certs.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic cert = certs.Item[i];
               if ((int) cert.ID == row.Id)
               {
                  cert.Delete();
                  ServerSession.Release((object) cert);
                  break;
               }
               ServerSession.Release((object) cert);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the certificate: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release((object) certs);
         }

         Reload();
      }
   }
}
