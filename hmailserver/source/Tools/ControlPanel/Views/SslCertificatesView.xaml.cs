using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class SslCertificatesView : UserControl, IPageLifecycle
   {
      public class CertRow
      {
         public string Name { get; set; }
         public string CertificateFile { get; set; }
         public string PrivateKeyFile { get; set; }
      }

      public SslCertificatesView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Reload()
      {
         var rows = new List<CertRow>();
         dynamic certs = ServerSession.Current.Application.Settings.SSLCertificates;
         try
         {
            int count = (int) certs.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic cert = certs.Item[i];
               rows.Add(new CertRow
               {
                  Name = (string) cert.Name,
                  CertificateFile = (string) cert.CertificateFile,
                  PrivateKeyFile = (string) cert.PrivateKeyFile
               });
               ServerSession.Release(cert);
            }
         }
         finally
         {
            ServerSession.Release(certs);
         }

         CertGrid.ItemsSource = rows;
      }

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

      private void BrowseKey_Click(object sender, RoutedEventArgs e)
      {
         string file = BrowsePem();
         if (file != null)
            NewKeyFile.Text = file;
      }

      private void Add_Click(object sender, RoutedEventArgs e)
      {
         string name = NewCertName.Text.Trim();
         string certFile = NewCertFile.Text.Trim();
         string keyFile = NewKeyFile.Text.Trim();

         if (name.Length == 0 || certFile.Length == 0 || keyFile.Length == 0)
         {
            MessageBox.Show("Name, certificate file and private key file are required.", "Control Panel");
            return;
         }

         dynamic certs = ServerSession.Current.Application.Settings.SSLCertificates;
         try
         {
            dynamic cert = certs.Add();
            cert.Name = name;
            cert.CertificateFile = certFile;
            cert.PrivateKeyFile = keyFile;
            cert.Save();
            ServerSession.Release(cert);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the certificate: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(certs);
         }

         NewCertName.Text = NewCertFile.Text = NewKeyFile.Text = "";
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
               if ((string) cert.Name == row.Name)
               {
                  cert.Delete();
                  ServerSession.Release(cert);
                  break;
               }
               ServerSession.Release(cert);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the certificate: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(certs);
         }

         Reload();
      }
   }
}
