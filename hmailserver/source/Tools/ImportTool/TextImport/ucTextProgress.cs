// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool.TextImport
{
   public partial class ucTextProgress : UserControl, IWizardPage
   {
      public ucTextProgress()
      {
         InitializeComponent();
      }

      public string Title
      {
         get { return "Import"; }
      }

      public void OnShowPage(Dictionary<string, string> _state)
      {
         textLog.Clear();

         try
         {
            RunImport();
         }
         catch (Exception ex)
         {
            AddToLog("The import failed: " + ex.Message);
         }
      }

      public bool OnLeavePage(bool next)
      {
         return true;
      }

      private void RunImport()
      {
         var parser = new TextFileParser();

         AddToLog("Reading " + ucTextSelect.SelectedFile + "...");
         parser.ParseFile(ucTextSelect.SelectedFile);

         foreach (var error in parser.Errors)
            AddToLog("Line " + error.LineNumber + " skipped: " + error.Reason);

         var domain = Globals.GetApp().Domains.get_ItemByName(ucTextSelect.SelectedDomain);
         var accounts = domain.Accounts;

         var created = 0;
         var updated = 0;
         var failed = 0;

         foreach (var entry in parser.Accounts)
         {
            var address = entry.Name + "@" + ucTextSelect.SelectedDomain;

            try
            {
               hMailServer.Account account = FindAccount(accounts, address);

               var exists = account != null;
               if (!exists)
                  account = accounts.Add();

               account.Address = address;
               account.Password = entry.Password;
               account.Active = true;
               account.MaxSize = entry.MaxSizeMb;
               account.Save();

               if (exists)
                  updated++;
               else
                  created++;
            }
            catch (Exception ex)
            {
               failed++;
               AddToLog(address + " failed: " + ex.Message);
            }

            labelProgress.Text = (created + updated) + " of " + parser.Accounts.Count + " imported.";
            Application.DoEvents();
         }

         var summary = new StringBuilder();
         summary.AppendLine("Import completed.");
         summary.AppendLine("Accounts created: " + created);
         summary.AppendLine("Accounts updated: " + updated);
         if (failed > 0)
            summary.AppendLine("Accounts failed: " + failed);
         if (parser.Errors.Count > 0)
            summary.AppendLine("Lines skipped: " + parser.Errors.Count);

         AddToLog(summary.ToString());
      }

      private static hMailServer.Account FindAccount(hMailServer.Accounts accounts, string address)
      {
         try
         {
            return accounts.get_ItemByAddress(address);
         }
         catch (Exception)
         {
            // The COM API reports a missing account as an error.
            return null;
         }
      }

      private void AddToLog(string message)
      {
         textLog.AppendText(message + Environment.NewLine);
         Application.DoEvents();
      }
   }
}
