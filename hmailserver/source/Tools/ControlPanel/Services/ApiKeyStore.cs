using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>One key in the REST API key store, as the server reads it.</summary>
   public sealed class ApiKeyRecord
   {
      /// <summary>Lower-case hex, 16 characters. The section suffix, and how a key is revoked.</summary>
      public string Id = "";

      public string Label = "";

      /// <summary>"YYYY-MM-DD HH:MM:SS", local time, exactly as stored.</summary>
      public string Expires = "";

      /// <summary>Empty means any source; otherwise an address, a "lower-upper" range or CIDR.</summary>
      public string AllowedFrom = "";

      /// <summary>
      /// False only for the literal "full". Every other value - a misspelling, a
      /// truncated write, a missing line - is read-only, matching
      /// RestApiServer::LoadKeys_ exactly. Getting this backwards in the GUI would
      /// show a full-authority key as harmless.
      /// </summary>
      public bool ReadOnly = true;

      /// <summary>Empty means every domain.</summary>
      public List<string> Domains = new();

      /// <summary>True when the stored Hash is not a usable SHA-256 digest, in
      /// which case the server ignores the record entirely.</summary>
      public bool Unusable;

      /// <summary>
      /// The stored expiry as a time, or null when it does not parse - which the
      /// server treats as expired.
      /// </summary>
      public DateTime? ExpiresAt =>
         DateTime.TryParseExact(Expires ?? "", ApiKeyStore.TimestampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
            ? value
            : (DateTime?) null;

      /// <summary>
      /// True when the server would refuse this key today. An unreadable or
      /// missing expiry counts as expired, which is what IsExpired_ does.
      /// </summary>
      public bool IsExpired
      {
         get
         {
            DateTime? at = ExpiresAt;
            return at == null || at.Value <= DateTime.Now;
         }
      }
   }

   /// <summary>
   /// Reads, creates and revokes the REST administration API keys.
   ///
   /// Until now these existed only over the API that they authenticate: minting
   /// one meant a hand-written HTTP request carrying the administrator password,
   /// against a listener that is off by default - so the feature was documented,
   /// implemented, and unreachable for anyone who does not write curl commands.
   /// Reading, creating and revoking are all file operations on the server's own
   /// machine, which is where the Control Panel already is.
   ///
   /// Everything here mirrors RestApiServer, and each mirrored decision is named
   /// below so a future change to one side is visible as a difference from the
   /// other. The parts that matter:
   ///
   ///   - The store is <c>hMailServerApiKeys.ini</c> beside hMailServer.INI
   ///     (RestApiServer::GetApiKeyStoreFile), one <c>[Key.&lt;id&gt;]</c> section
   ///     per key.
   ///   - A token is <c>hmapi_</c> followed by 64 lower-case hex characters - 32
   ///     random bytes - and only its lower-case hex SHA-256 is stored, so the
   ///     clear text exists once, in the reply to whoever asked for it.
   ///   - Scope fails closed: only the literal "full" widens a key.
   ///   - Hash is written LAST, because a section without a usable Hash is ignored
   ///     by the server. A half-written section is therefore an unusable record
   ///     rather than a usable key with no restrictions.
   ///   - The server re-reads the file on every request, so a key created or
   ///     revoked here takes effect immediately: no restart, no rebuild.
   ///
   /// It is deliberately NOT possible to read a key back. There is nothing to read
   /// - the store holds a digest - and a page offering to show one would be
   /// promising something the format cannot deliver.
   /// </summary>
   public static class ApiKeyStore
   {
      /// <summary>RestApiServer's ApiKeyTokenPrefix.</summary>
      public const string TokenPrefix = "hmapi_";

      /// <summary>RestApiServer's ApiKeySectionPrefix.</summary>
      public const string SectionPrefix = "Key.";

      /// <summary>RestApiServer's ApiKeySecretBytes.</summary>
      public const int SecretBytes = 32;

      /// <summary>RestApiServer's ApiKeyIdBytes.</summary>
      public const int IdBytes = 8;

      /// <summary>RestApiServer's ApiKeyDefaultLifetimeDays.</summary>
      public const int DefaultLifetimeDays = 90;

      /// <summary>RestApiServer's label ceiling.</summary>
      public const int MaxLabelLength = 64;

      /// <summary>hMailServer's system date format, which is what Expires holds.</summary>
      public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

      public const string ScopeFull = "full";
      public const string ScopeReadOnly = "readonly";

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      /// <summary>
      /// Full path of the key store, or null when hMailServer.INI cannot be found
      /// - which is every case where this machine is not the server.
      /// </summary>
      public static string StoreFile
      {
         get
         {
            var ini = new IniFeatureStore();
            if (!ini.IsAvailable)
               return null;

            string directory = Path.GetDirectoryName(ini.IniPath);
            return directory == null ? null : Path.Combine(directory, "hMailServerApiKeys.ini");
         }
      }

      /// <summary>
      /// Every key in the store, newest-first by expiry so the list has a stable
      /// order. An absent file is no keys rather than an error: the server creates
      /// it when the first key is minted.
      /// </summary>
      public static List<ApiKeyRecord> Read()
      {
         var keys = new List<ApiKeyRecord>();

         string path = StoreFile;
         if (path == null || !File.Exists(path))
            return keys;

         // Parsed from the file's bytes rather than through
         // GetPrivateProfileString, for the reason LoadKeys_ gives: the Windows
         // profile functions keep a cache, and a read served from it can describe
         // a key that has already been deleted.
         string[] lines;
         try
         {
            lines = File.ReadAllLines(path);
         }
         catch (IOException)
         {
            return keys;
         }
         catch (UnauthorizedAccessException)
         {
            return keys;
         }

         ApiKeyRecord current = null;
         bool hashUsable = false;

         void Commit()
         {
            if (current == null)
               return;

            current.Unusable = !hashUsable;
            keys.Add(current);
            current = null;
            hashUsable = false;
         }

         foreach (string rawLine in lines)
         {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
               continue;

            if (line.StartsWith("["))
            {
               Commit();

               if (!line.EndsWith("]"))
                  continue;

               string section = line.Substring(1, line.Length - 2).Trim();
               if (!section.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase))
                  continue;

               // Lower-cased for the same reason LoadKeys_ lower-cases it: revoking
               // names the section by the id, and a hand-written [KEY.AB12] would
               // otherwise be listed under an id that cannot be used to revoke it.
               current = new ApiKeyRecord { Id = section.Substring(SectionPrefix.Length).ToLowerInvariant() };
               continue;
            }

            if (current == null)
               continue;

            int equals = line.IndexOf('=');
            if (equals <= 0)
               continue;

            string name = line.Substring(0, equals).Trim();
            string value = line.Substring(equals + 1).Trim();

            if (string.Equals(name, "Hash", StringComparison.OrdinalIgnoreCase))
               hashUsable = IsLowerHex(value, 64);
            else if (string.Equals(name, "Label", StringComparison.OrdinalIgnoreCase))
               current.Label = value;
            else if (string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase))
               current.Expires = value;
            else if (string.Equals(name, "AllowedFrom", StringComparison.OrdinalIgnoreCase))
               current.AllowedFrom = value;
            else if (string.Equals(name, "Scope", StringComparison.OrdinalIgnoreCase))
               current.ReadOnly = !string.Equals(value, ScopeFull, StringComparison.OrdinalIgnoreCase);
            else if (string.Equals(name, "Domains", StringComparison.OrdinalIgnoreCase))
            {
               current.Domains = value
                  .Split(',')
                  .Select(d => d.Trim().ToLowerInvariant())
                  .Where(d => d.Length > 0)
                  .ToList();
            }
         }

         Commit();

         return keys.OrderBy(k => k.Label, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Id, StringComparer.Ordinal).ToList();
      }

      /// <summary>What a create attempt produced: the token, shown once, or why not.</summary>
      public sealed class CreateResult
      {
         public bool Succeeded;

         /// <summary>The clear-text token. Held only long enough to show it; it is
         /// not stored anywhere and cannot be recovered afterwards.</summary>
         public string Token;

         public string Id;
         public string Error;
      }

      /// <summary>
      /// Mints a key and writes it to the store. The returned token is the only
      /// copy that will ever exist.
      ///
      /// The same validation the server applies, applied before anything is
      /// written: a rejected value here is a message in the dialog, whereas one
      /// written to the file would be a key that authenticates nothing and gives
      /// no clue why.
      /// </summary>
      public static CreateResult Create(string label, bool full, DateTime expires, string allowedFrom, string domains)
      {
         string path = StoreFile;
         if (path == null)
         {
            return new CreateResult
            {
               Error = "hMailServer.INI was not found on this machine, so the key store's location is unknown. "
                       + "API keys can only be managed from the server itself."
            };
         }

         string error = ValidateLabel(label);
         if (error != null)
            return new CreateResult { Error = error };

         if (expires <= DateTime.Now)
            return new CreateResult { Error = "The expiry has to be in the future. A key that is already expired is refused on its first use." };

         string normalizedDomains = NormalizeDomains(domains, out error);
         if (error != null)
            return new CreateResult { Error = error };

         string source = (allowedFrom ?? "").Trim();
         if (source.Length > 0 && !LooksLikeSourceRestriction(source))
         {
            return new CreateResult
            {
               Error = "The source restriction has to be an address (10.0.0.5), a range (10.0.0.1-10.0.0.99) or "
                       + "CIDR (10.0.0.0/24). Leave it empty to accept the key from any address."
            };
         }

         string token = TokenPrefix + RandomLowerHex(SecretBytes);
         string id = RandomLowerHex(IdBytes);
         string section = SectionPrefix + id;
         string hash = HashToken(token);

         if (!File.Exists(path))
         {
            // The same explanation the server writes, because whoever opens this
            // file will be looking for it there and not here. Failure is not
            // fatal - the writes below create the file regardless.
            try
            {
               File.WriteAllText(path, Preamble, new UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
         }

         // Hash last: a section without a usable Hash is ignored by the server, so
         // an interrupted write leaves an unusable record rather than a usable key
         // whose restrictions never landed. Scope and Domains precede it for the
         // same reason - a key that became usable before its restrictions were
         // written would be briefly unrestricted, and briefly is enough.
         bool written =
            WritePrivateProfileString(section, "Label", label.Trim(), path) &&
            WritePrivateProfileString(section, "Expires", expires.ToString(TimestampFormat, CultureInfo.InvariantCulture), path) &&
            WritePrivateProfileString(section, "AllowedFrom", source, path) &&
            WritePrivateProfileString(section, "Scope", full ? ScopeFull : ScopeReadOnly, path) &&
            WritePrivateProfileString(section, "Domains", normalizedDomains, path) &&
            WritePrivateProfileString(section, "Hash", hash, path);

         // Flush the profile cache so the server's next read sees the new section.
         WritePrivateProfileString(null, null, null, path);

         if (!written)
         {
            // Take the half-written section out again rather than leaving a record
            // that lists in the GUI and authenticates nothing.
            WritePrivateProfileString(section, null, null, path);
            WritePrivateProfileString(null, null, null, path);

            return new CreateResult
            {
               Error = "The key store could not be written, so no key was created: " + path
                       + ". The Control Panel needs write access to that folder."
            };
         }

         return new CreateResult { Succeeded = true, Token = token, Id = id };
      }

      /// <summary>
      /// Removes a key's section. Takes effect on the server's next request; there
      /// is nothing to restart.
      /// </summary>
      public static bool Revoke(string id, out string error)
      {
         error = null;

         string path = StoreFile;
         if (path == null || !File.Exists(path))
         {
            error = "The key store was not found, so there is nothing to revoke.";
            return false;
         }

         // Only ids of the shape the store uses, so nothing here can be talked
         // into naming another section.
         if (!IsLowerHex(id, IdBytes * 2))
         {
            error = "That key's id is not in the form this store uses, so its section cannot be identified. "
                    + "Remove it by hand from " + path + ".";
            return false;
         }

         bool deleted = WritePrivateProfileString(SectionPrefix + id, null, null, path);
         WritePrivateProfileString(null, null, null, path);

         if (!deleted)
         {
            error = "The key store could not be written, so the key was NOT revoked: " + path;
            return false;
         }

         return true;
      }

      // ---- the pieces that have to match the server exactly --------------------

      /// <summary>
      /// Lower-case hex SHA-256 over the token's ASCII bytes - RestApiServer's
      /// HashApiKeyToken, which is HashCreator(SHA256).GenerateHashNoSalt(token,
      /// hex). Unsalted and fast on purpose: the input is 256 bits of entropy from
      /// a CSPRNG, so a password KDF would buy nothing and would hand an
      /// unauthenticated stranger a way to burn the server's CPU per request.
      /// </summary>
      public static string HashToken(string token)
      {
         byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(token ?? ""));
         return Convert.ToHexString(digest).ToLowerInvariant();
      }

      /// <summary>Cryptographically random bytes as lower-case hex.</summary>
      public static string RandomLowerHex(int byteCount)
      {
         byte[] bytes = RandomNumberGenerator.GetBytes(byteCount);
         return Convert.ToHexString(bytes).ToLowerInvariant();
      }

      /// <summary>The server's label rule: present, at most 64 characters, no
      /// control characters - because the value becomes an ini line and a log
      /// line, and a CR in either would be read as a second line.</summary>
      public static string ValidateLabel(string label)
      {
         string value = (label ?? "").Trim();

         if (value.Length == 0)
            return "A label is required. It is how this key is told apart from the others months from now.";

         if (value.Length > MaxLabelLength)
            return "The label has to be " + MaxLabelLength + " characters or fewer.";

         foreach (char c in value)
         {
            if (c < 0x20 || c == 0x7F)
               return "The label must not contain control characters.";
         }

         return null;
      }

      /// <summary>
      /// The server's domain normalisation: trimmed, lower-cased, empties dropped.
      /// A list the caller asked for that survives as nothing is an error rather
      /// than "every domain", which is the opposite of what was asked for.
      /// </summary>
      public static string NormalizeDomains(string domains, out string error)
      {
         error = null;

         string value = (domains ?? "").Trim();
         if (value.Length == 0)
            return "";

         var names = new List<string>();

         foreach (string part in value.Split(','))
         {
            string name = part.Trim().ToLowerInvariant();
            if (name.Length == 0)
               continue;

            if (!IsValidDomainName(name))
            {
               error = "'" + name + "' is not a domain name. Give a comma-separated list of domains, or leave the "
                       + "box empty to let the key act on every domain.";
               return null;
            }

            names.Add(name);
         }

         if (names.Count == 0)
         {
            error = "The domain list contains no domain names. Leave the box empty to let the key act on every domain - "
                    + "a list of separators would silently mean the same thing, which is not what you asked for.";
            return null;
         }

         return string.Join(",", names);
      }

      /// <summary>
      /// A domain name for the purposes of this list. Deliberately permissive in
      /// the same direction the server is: the domain need not exist yet, since a
      /// key may be minted before the domain it will manage.
      /// </summary>
      public static bool IsValidDomainName(string name)
      {
         if (string.IsNullOrEmpty(name) || name.Length > 253)
            return false;

         if (name.StartsWith(".") || name.EndsWith(".") || name.Contains(".."))
            return false;

         foreach (char c in name)
         {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
            if (!ok)
               return false;
         }

         return name.Contains('.');
      }

      /// <summary>
      /// Whether a source restriction is one of the three forms the server parses.
      /// Shape only - the server does the authoritative parse - but enough to stop
      /// a typo becoming a key that is refused every request with no explanation.
      /// </summary>
      public static bool LooksLikeSourceRestriction(string value)
      {
         string text = (value ?? "").Trim();
         if (text.Length == 0)
            return false;

         int slash = text.IndexOf('/');
         if (slash > 0)
         {
            string prefix = text.Substring(slash + 1);
            return System.Net.IPAddress.TryParse(text.Substring(0, slash), out _)
                   && prefix.Length > 0 && prefix.Length <= 3
                   && int.TryParse(prefix, out int bits) && bits >= 0 && bits <= 128;
         }

         // A range is "lower-upper". IPv6 addresses contain colons but not
         // hyphens, so splitting on the first hyphen is unambiguous for both
         // families.
         int dash = text.IndexOf('-');
         if (dash > 0)
         {
            return System.Net.IPAddress.TryParse(text.Substring(0, dash).Trim(), out _)
                   && System.Net.IPAddress.TryParse(text.Substring(dash + 1).Trim(), out _);
         }

         return System.Net.IPAddress.TryParse(text, out _);
      }

      private static bool IsLowerHex(string value, int expectedLength)
      {
         if (value == null || value.Length != expectedLength)
            return false;

         foreach (char c in value)
         {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
               return false;
         }

         return true;
      }

      /// <summary>
      /// The explanation the server writes into a new store. Kept in step with
      /// RestApiServer::HandleCreateApiKey_ so that a file created from either
      /// side reads the same.
      /// </summary>
      private const string Preamble =
         "; hMailServer REST administration API keys.\r\n" +
         ";\r\n" +
         "; One section per key. Only the SHA-256 digest of a key is stored, so a\r\n" +
         "; key cannot be recovered from this file: it is shown once, when it is\r\n" +
         "; created, and never again.\r\n" +
         ";\r\n" +
         "; To revoke a key, either use the Control Panel's API keys page, send\r\n" +
         "; DELETE /api/v1/apikeys/<id>, or delete its section below. All three take\r\n" +
         "; effect on the next request - no restart and no rebuild. Deleting this\r\n" +
         "; file revokes every key.\r\n" +
         ";\r\n" +
         "; Expires     YYYY-MM-DD HH:MM:SS, local time. Required. A key whose\r\n" +
         ";             expiry is missing or unreadable counts as expired.\r\n" +
         "; AllowedFrom Optional. An address, a 'lower-upper' range, or CIDR.\r\n" +
         ";             Empty means any source address.\r\n" +
         "; Scope       'full' or 'readonly'. Anything else - including a missing\r\n" +
         ";             line - is readonly, so a typo cannot widen a key. A\r\n" +
         ";             readonly key is refused every request that changes\r\n" +
         ";             something.\r\n" +
         "; Domains     Optional, comma-separated. Empty means every domain. A key\r\n" +
         ";             with a list may only act on those domains, and is refused\r\n" +
         ";             the delivery-queue endpoints outright because the queue is\r\n" +
         ";             server-wide.\r\n" +
         ";\r\n" +
         "; No key of any scope can create or revoke keys: that needs the\r\n" +
         "; administrator password.\r\n" +
         "\r\n";
   }
}
