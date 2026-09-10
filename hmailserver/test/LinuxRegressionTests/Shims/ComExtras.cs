// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using RegressionTests.Shared;

// The rest of the COM surface the linked fixtures name: the collections and objects
// the REST API has no route for at all. They exist so that a fixture whose other
// tests do run can compile; every member here stops the test that reaches it, with
// the reason, and none of them pretends the server was asked anything.
namespace hMailServer
{
   public class BlockedAttachments
   {
      public BlockedAttachment Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
         return null;
      }

      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
            return 0;
         }
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
      }
   }

   public class BlockedAttachment
   {
      public long ID { get; set; }
      public string Wildcard { get; set; }
      public string Description { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
      }
   }

   /// <summary>An account's own rules, which /api/v1/rules does not carry.</summary>
   public class AccountRules
   {
      public Rule Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
         return null;
      }

      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
            return 0;
         }
      }

      public Rule get_ItemByName(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Rule this[int index]
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
            return null;
         }
      }

      public Rule get_Item(int index)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountRules);
      }

      public void Refresh()
      {
      }
   }

   public class FetchAccounts
   {
      public FetchAccount Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
         return null;
      }

      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
            return 0;
         }
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public FetchAccount this[int index]
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
            return null;
         }
      }

      public FetchAccount get_Item(int index)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
         return null;
      }

      public FetchAccount get_ItemByName(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
      }
   }

   public class FetchAccount
   {
      public long ID { get; set; }
      public string Name { get; set; }
      public string Username { get; set; }
      public string Password { get; set; }
      public string ServerAddress { get; set; }
      public int Port { get; set; }
      public bool Active { get; set; }
      public bool UseSSL { get; set; }
      public eConnectionSecurity ConnectionSecurity { get; set; }
      public int MinutesBetweenFetch { get; set; }
      public bool DeleteMessagesAfterFetch { get; set; }
      public bool ProcessMIMERecipients { get; set; }
      public bool ProcessMIMEDate { get; set; }
      public bool UseAntiSpam { get; set; }
      public bool UseAntiVirus { get; set; }
      public bool EnableRouteRecipients { get; set; }
      public bool UseIMAP { get; set; }
      public string IMAPFolder { get; set; }
      public bool IMAPIdle { get; set; }
      public bool MirrorFolders { get; set; }
      public int DaysToKeepMessages { get; set; }
      public string AuthenticationMethod { get; set; }
      public string OAuth2ClientID { get; set; }
      public string OAuth2ClientSecret { get; set; }
      public string OAuth2RefreshToken { get; set; }
      public string OAuth2TokenEndpoint { get; set; }
      public string OAuth2Scope { get; set; }
      public bool Enabled { get; set; }
      public bool IsLocked { get; set; }
      public int ServerType { get; set; }
      public string PersonalizedFrom { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
      }

      public void Delete()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
      }

      public void DownloadNow()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFetchAccounts);
      }
   }

   public class AppPasswords
   {
      public AppPassword Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
         return null;
      }

      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
            return 0;
         }
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public AppPassword this[int index]
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
            return null;
         }
      }

      public AppPassword get_Item(int index)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      public void Refresh()
      {
      }
   }

   public class AppPassword
   {
      public long ID { get; set; }
      public string Name { get; set; }
      public string Password { get; set; }
      public DateTime LastUsed { get; set; }
      public DateTime CreatedTime { get; set; }

      public string Generate()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
         return null;
      }

      public void SetPassword(string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAppPasswords);
      }
   }

   public class Group
   {
      public long ID { get; set; }
      public string Name { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
      }
   }

   /// <summary>An IMAP folder's ACL, which no REST route reads or writes.</summary>
   public class IMAPFolderPermissions
   {
      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
            return 0;
         }
      }

      public IMAPFolderPermission Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public IMAPFolderPermission this[int index]
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
            return null;
         }
      }

      public IMAPFolderPermission get_Item(int index)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }
   }

   public class IMAPFolderPermission
   {
      public long ID { get; set; }
      public eACLPermissionType PermissionType { get; set; }
      public long PermissionAccountID { get; set; }
      public long PermissionGroupID { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      public void set_Permission(eACLPermission permission, bool value)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
      }

      public bool get_Permission(eACLPermission permission)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoFolderAcl);
         return false;
      }
   }
}
