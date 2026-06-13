using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class RoutesView : UserControl, IPageLifecycle
   {
      public class RouteRow
      {
         public string DomainName { get; set; }
         public string TargetHost { get; set; }
         public int TargetPort { get; set; }
         public int Retries { get; set; }
         public string Auth { get; set; }
      }

      public RoutesView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Reload()
      {
         var rows = new List<RouteRow>();
         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            int count = (int) routes.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic route = routes.Item[i];
               rows.Add(new RouteRow
               {
                  DomainName = (string) route.DomainName,
                  TargetHost = (string) route.TargetSMTPHost,
                  TargetPort = (int) route.TargetSMTPPort,
                  Retries = (int) route.NumberOfTries,
                  Auth = (bool) route.RelayerRequiresAuth ? "Yes" : "No"
               });
               ServerSession.Release(route);
            }
         }
         finally
         {
            ServerSession.Release(routes);
         }

         RouteGrid.ItemsSource = rows;
      }

      private void Add_Click(object sender, RoutedEventArgs e)
      {
         string domain = NewDomain.Text.Trim();
         string host = NewHost.Text.Trim();

         if (domain.Length == 0 || host.Length == 0)
         {
            MessageBox.Show("Domain and target host are required.", "Control Panel");
            return;
         }

         if (!int.TryParse(NewPort.Text.Trim(), out int port))
            port = 25;

         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic route = routes.Add();
            route.DomainName = domain;
            route.TargetSMTPHost = host;
            route.TargetSMTPPort = port;
            route.NumberOfTries = 3;
            route.MinutesBetweenTry = 10;
            route.Save();
            ServerSession.Release(route);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the route: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(routes);
         }

         NewDomain.Text = NewHost.Text = "";
         Reload();
      }

      private void Delete_Click(object sender, RoutedEventArgs e)
      {
         if (RouteGrid.SelectedItem is not RouteRow row)
            return;

         if (MessageBox.Show("Delete the route for '" + row.DomainName + "'?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            int count = (int) routes.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic route = routes.Item[i];
               if ((string) route.DomainName == row.DomainName)
               {
                  route.Delete();
                  ServerSession.Release(route);
                  break;
               }
               ServerSession.Release(route);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the route: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(routes);
         }

         Reload();
      }
   }
}
