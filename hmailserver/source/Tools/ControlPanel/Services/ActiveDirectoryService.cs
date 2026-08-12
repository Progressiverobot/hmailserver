using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Text;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One Active Directory user account returned by a directory search.
   /// </summary>
   public sealed class AdUser
   {
      public string SamAccountName { get; init; } = "";
      public string DisplayName { get; init; } = "";
      public string Email { get; init; } = "";
      public string UserPrincipalName { get; init; } = "";

      /// <summary>Friendly label for list display.</summary>
      public string Label =>
         string.IsNullOrEmpty(DisplayName) ? SamAccountName : DisplayName + "  (" + SamAccountName + ")";
   }

   /// <summary>
   /// Read-only Active Directory browsing used by the account / directory pickers.
   /// Implemented with <see cref="System.DirectoryServices"/> only (no dependency on
   /// System.DirectoryServices.ActiveDirectory). On .NET 10 the assembly comes from the
   /// Windows Desktop shared framework, so no PackageReference is needed.
   /// All calls are defensive: on a non-domain-joined machine they report a friendly
   /// reason rather than throwing.
   /// </summary>
   public static class ActiveDirectoryService
   {
      /// <summary>
      /// True when this computer can reach a directory (i.e. it is domain-joined or a
      /// domain controller). <paramref name="reason"/> explains a negative result.
      /// </summary>
      public static bool IsAvailable(out string reason)
      {
         try
         {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            object dnc = rootDse.Properties["defaultNamingContext"].Value;
            if (dnc == null || string.IsNullOrEmpty(dnc.ToString()))
            {
               reason = "This computer is not joined to an Active Directory domain.";
               return false;
            }
            reason = "";
            return true;
         }
         catch (Exception ex)
         {
            reason = "Active Directory is not reachable from this computer — " +
                     ServerSession.DescribeComError(ex);
            return false;
         }
      }

      /// <summary>
      /// Lists the DNS names of every domain in the current forest. Falls back to the
      /// computer's own domain when the forest partition list cannot be read.
      /// </summary>
      public static List<string> ListDomains()
      {
         var domains = new List<string>();

         string defaultNc = null;
         string configNc = null;
         try
         {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            defaultNc = rootDse.Properties["defaultNamingContext"].Value as string;
            configNc = rootDse.Properties["configurationNamingContext"].Value as string;
         }
         catch
         {
            // Not domain-joined / unreachable — nothing to enumerate.
            return domains;
         }

         // Enumerate every domain crossRef in the forest's Partitions container.
         if (!string.IsNullOrEmpty(configNc))
         {
            try
            {
               using var partitions = new DirectoryEntry("LDAP://CN=Partitions," + configNc);
               using var searcher = new DirectorySearcher(partitions)
               {
                  // crossRef objects that describe a domain naming context have systemFlags
                  // bit 0x2 (FLAG_CR_NTDS_DOMAIN) set.
                  Filter = "(&(objectCategory=crossRef)(systemFlags:1.2.840.113556.1.4.803:=2))",
                  SearchScope = SearchScope.OneLevel,
                  PageSize = 500
               };
               searcher.PropertiesToLoad.Add("dnsRoot");
               using SearchResultCollection results = searcher.FindAll();
               foreach (SearchResult r in results)
               {
                  string dns = GetProp(r, "dnsRoot");
                  if (!string.IsNullOrEmpty(dns) && !domains.Contains(dns, StringComparer.OrdinalIgnoreCase))
                     domains.Add(dns);
               }
            }
            catch
            {
               // Fall through to the default-domain fallback below.
            }
         }

         if (domains.Count == 0 && !string.IsNullOrEmpty(defaultNc))
         {
            string dns = DistinguishedNameToDns(defaultNc);
            if (!string.IsNullOrEmpty(dns))
               domains.Add(dns);
         }

         domains.Sort(StringComparer.OrdinalIgnoreCase);
         return domains;
      }

      /// <summary>
      /// Searches a domain for user accounts. <paramref name="filter"/> is a free-text
      /// fragment matched against the account name, display name and e-mail address
      /// (empty returns every user, capped at <paramref name="max"/>).
      /// </summary>
      public static List<AdUser> QueryUsers(string domainDns, string filter, int max)
      {
         var users = new List<AdUser>();
         if (string.IsNullOrWhiteSpace(domainDns))
            return users;

         using var root = new DirectoryEntry("LDAP://" + domainDns);
         using var searcher = new DirectorySearcher(root)
         {
            SearchScope = SearchScope.Subtree,
            PageSize = 500,
            SizeLimit = max > 0 ? max : 0
         };

         // Real user accounts: person + user, excluding computer accounts and the
         // built-in krbtgt / disabled-by-flag entries is left to the caller's eyes.
         var sb = new StringBuilder("(&(objectCategory=person)(objectClass=user)(sAMAccountName=*)");
         if (!string.IsNullOrWhiteSpace(filter))
         {
            string f = EscapeLdap(filter.Trim());
            sb.Append("(|(sAMAccountName=*").Append(f).Append("*)")
              .Append("(displayName=*").Append(f).Append("*)")
              .Append("(mail=*").Append(f).Append("*)")
              .Append("(userPrincipalName=*").Append(f).Append("*))");
         }
         sb.Append(')');
         searcher.Filter = sb.ToString();

         searcher.PropertiesToLoad.AddRange(new[]
         {
            "sAMAccountName", "displayName", "mail", "userPrincipalName"
         });

         using SearchResultCollection results = searcher.FindAll();
         foreach (SearchResult r in results)
         {
            string sam = GetProp(r, "sAMAccountName");
            if (string.IsNullOrEmpty(sam))
               continue;

            users.Add(new AdUser
            {
               SamAccountName = sam,
               DisplayName = GetProp(r, "displayName"),
               Email = GetProp(r, "mail"),
               UserPrincipalName = GetProp(r, "userPrincipalName")
            });
         }

         users.Sort((a, b) => string.Compare(a.SamAccountName, b.SamAccountName, StringComparison.OrdinalIgnoreCase));
         return users;
      }

      private static string GetProp(SearchResult r, string name)
      {
         var values = r.Properties[name];
         return values != null && values.Count > 0 ? values[0]?.ToString() ?? "" : "";
      }

      /// <summary>Converts e.g. "DC=progressiverobot,DC=local" to "progressiverobot.local".</summary>
      private static string DistinguishedNameToDns(string dn)
      {
         var parts = dn.Split(',')
                       .Select(token => token.Trim())
                       .Where(t => t.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                       .Select(t => t.Substring(3))
                       .ToList();
         return string.Join(".", parts);
      }

      /// <summary>RFC 4515 escaping so search text cannot alter the LDAP filter.</summary>
      private static string EscapeLdap(string s)
      {
         var sb = new StringBuilder(s.Length);
         foreach (char c in s)
         {
            switch (c)
            {
               case '\\': sb.Append("\\5c"); break;
               case '*': sb.Append("\\2a"); break;
               case '(': sb.Append("\\28"); break;
               case ')': sb.Append("\\29"); break;
               case '\0': sb.Append("\\00"); break;
               case '/': sb.Append("\\2f"); break;
               default: sb.Append(c); break;
            }
         }
         return sb.ToString();
      }
   }
}
