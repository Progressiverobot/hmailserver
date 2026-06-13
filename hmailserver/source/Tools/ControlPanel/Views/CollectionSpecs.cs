using hMailServer.ControlPanel.Services;
using static hMailServer.ControlPanel.Views.CollectionEditorView;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Factory for the data-driven list panes. Each method returns a fully
   /// configured <see cref="CollectionEditorView"/> bound to a COM collection.
   /// </summary>
   public static class CollectionSpecs
   {
      private static dynamic AntiSpam => ServerSession.Current.Application.Settings.AntiSpam;
      private static dynamic Settings => ServerSession.Current.Application.Settings;

      public static CollectionEditorView SurblServers() => new(new CollectionSpec
      {
         Title = "SURBL servers",
         Subtitle = "Spam URI Realtime Block Lists. Messages whose body links resolve on these hosts gain spam score.",
         ItemNoun = "SURBL server",
         GetCollection = () => AntiSpam.SURBLServers,
         Fields =
         {
            new FieldSpec { Prop = "Active", Label = "Active", Kind = FieldKind.Bool, GridWidth = 70 },
            new FieldSpec { Prop = "DNSHost", Label = "DNS host" },
            new FieldSpec { Prop = "RejectMessage", Label = "Reject message" },
            new FieldSpec { Prop = "Score", Label = "Score", Kind = FieldKind.Number, GridWidth = 80, Default = 5 }
         }
      });

      public static CollectionEditorView DnsBlackLists() => new(new CollectionSpec
      {
         Title = "DNS blacklists (DNSBL)",
         Subtitle = "Real-time blackhole lists checked against the connecting IP address.",
         ItemNoun = "blacklist",
         GetCollection = () => AntiSpam.DNSBlackLists,
         Fields =
         {
            new FieldSpec { Prop = "Active", Label = "Active", Kind = FieldKind.Bool, GridWidth = 70 },
            new FieldSpec { Prop = "DNSHost", Label = "DNS host" },
            new FieldSpec { Prop = "ExpectedResult", Label = "Expected result", GridWidth = 130 },
            new FieldSpec { Prop = "RejectMessage", Label = "Reject message" },
            new FieldSpec { Prop = "Score", Label = "Score", Kind = FieldKind.Number, GridWidth = 80, Default = 5 }
         }
      });

      public static CollectionEditorView SpamWhiteList() => new(new CollectionSpec
      {
         Title = "Anti-spam white list",
         Subtitle = "Senders or IP ranges that bypass spam protection entirely.",
         ItemNoun = "white-list entry",
         GetCollection = () => AntiSpam.WhiteListAddresses,
         Fields =
         {
            new FieldSpec { Prop = "LowerIPAddress", Label = "Lower IP", GridWidth = 150 },
            new FieldSpec { Prop = "UpperIPAddress", Label = "Upper IP", GridWidth = 150 },
            new FieldSpec { Prop = "EmailAddress", Label = "E-mail address" },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      public static CollectionEditorView GreyListWhiteList() => new(new CollectionSpec
      {
         Title = "Greylisting white list",
         Subtitle = "IP addresses exempt from greylisting delays.",
         ItemNoun = "address",
         GetCollection = () => AntiSpam.GreyListingWhiteAddresses,
         Fields =
         {
            new FieldSpec { Prop = "IPAddress", Label = "IP address", GridWidth = 200 },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      public static CollectionEditorView BlockedAttachments() => new(new CollectionSpec
      {
         Title = "Blocked attachments",
         Subtitle = "File-name wildcards that are stripped from incoming messages (requires attachment blocking on the Anti-virus page).",
         ItemNoun = "rule",
         GetCollection = () => Settings.AntiVirus.BlockedAttachments,
         Fields =
         {
            new FieldSpec { Prop = "Wildcard", Label = "Wildcard", GridWidth = 220, Default = "*.exe" },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      public static CollectionEditorView Groups() => new(new CollectionSpec
      {
         Title = "Groups",
         Subtitle = "Security groups used to grant shared-folder (IMAP ACL) permissions to several accounts at once.",
         ItemNoun = "group",
         GetCollection = () => Settings.Groups,
         Fields =
         {
            new FieldSpec { Prop = "Name", Label = "Group name" }
         }
      });

      public static CollectionEditorView ServerMessages() => new(new CollectionSpec
      {
         Title = "Server messages",
         Subtitle = "The text templates the server returns to clients (greetings, bounce and error messages). These are a fixed set you can edit.",
         ItemNoun = "message",
         CanAdd = false,
         CanDelete = false,
         GetCollection = () => Settings.ServerMessages,
         Fields =
         {
            new FieldSpec { Prop = "Name", Label = "Name", GridWidth = 260 },
            new FieldSpec { Prop = "Text", Label = "Text", Kind = FieldKind.Multiline }
         }
      });
   }
}
