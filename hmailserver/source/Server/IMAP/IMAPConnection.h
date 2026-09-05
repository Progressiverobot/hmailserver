// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once


#include "IMAPNotificationClient.h"
#include "../common/TCPIP/TCPConnection.h"


using namespace std;

namespace HM
{

   class IMAPCommand;
   class IMAPCommandAppend;
   class IMAPFolders;
   class IMAPFolder;
   class IMAPCommandArgument;
   class Message;
   class Messages;
   class IMAPMailboxChangeNotifier;
   class IMailboxChangeClient;
   class ScramSha256;

   class IMAPClientCommand
   {

   public:
      String Command;
      String Tag;
      String EntireLine;
      
      std::vector<String> vecLiteralData;
   };

   class IMAPConnection : public TCPConnection
   {
   public:
      IMAPConnection(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context);
	   virtual ~IMAPConnection();
      void Initialize();

      enum eIMAPCommandType
      {
         IMAP_UNKNOWN = 0,
         IMAP_CAPABILITY = 101,
         IMAP_LOGIN = 102,
         IMAP_LIST = 103,
         IMAP_LSUB = 104,
         IMAP_SELECT = 105,
         IMAP_FETCH = 106,
         IMAP_UID = 107,
         IMAP_LOGOUT = 108,
         IMAP_NOOP = 109,
         IMAP_SUBSCRIBE = 110,
         IMAP_CREATE = 111,
         IMAP_EXPUNGE = 112,
         IMAP_DELETE = 113,
         IMAP_UNSUBSCRIBE = 114,
         IMAP_STATUS = 115,
         IMAP_CLOSE = 116,
         IMAP_APPEND = 117,
         IMAP_STORE = 118,
         IMAP_RENAME = 119,
         IMAP_COPY = 120,
         IMAP_EXAMINE = 121,
         IMAP_SEARCH = 122,
         IMAP_AUTHENTICATE = 123,
         IMAP_CHECK = 124,
         IMAP_GETQUOTAROOT = 125,
         IMAP_GETQUOTA = 126,
         IMAP_SORT = 127,
         IMAP_IDLE = 128,
         IMAP_MYRIGHTS = 129,
         IMAP_NAMESPACE = 130,
         IMAP_GETACL = 131,
         IMAP_SETACL = 132,
         IMAP_DELETEACL = 133,
         IMAP_LISTRIGHTS = 134,
         IMAP_STARTTLS = 135,
         IMAP_MOVE = 136,
         IMAP_ID = 137,
         IMAP_UNSELECT = 138,
         IMAP_ENABLE = 139,
         IMAP_THREAD = 140,
         IMAP_UNAUTHENTICATE = 141,
         IMAP_SETQUOTA = 142,
         IMAP_REPLACE = 143,
         IMAP_GETMETADATA = 144,
         IMAP_SETMETADATA = 145
      };

      void ParseData(const AnsiString &Request);
      void ParseData(std::shared_ptr<ByteBuffer> pByteBuffer);
      void SendAsciiData(const AnsiString & sData);
      
      std::shared_ptr<const Account> GetAccount() { return account_; }

      // The account whose DIRECTORY holds the messages of the folder currently
      // selected. That is the logged-in account for a personal folder, and the
      // OWNER for a delegated one - a message file lives under the mailbox it
      // belongs to, not under whoever is reading it. Anything that turns a
      // Message into a path must ask this rather than GetAccount(), or a
      // delegate reading a shared folder looks in their own directory, finds
      // nothing, and is served the "file does not exist on the server"
      // placeholder instead of the mail.
      std::shared_ptr<const Account> GetAccountOwningCurrentFolder();

      // The account whose directory holds a given folder's messages. Use this for
      // any DESTINATION folder (APPEND/COPY/MOVE); the one above only answers for
      // the selected folder.
      std::shared_ptr<const Account> GetAccountOwningFolder(std::shared_ptr<IMAPFolder> folder);
      
      void RefreshIMAPFolders();
      void NotifyFolderChange(eIMAPCommandType active_command);
      
      std::shared_ptr<IMAPFolders> GetAccountFolders() const { return imap_folders_;}
      std::shared_ptr<IMAPFolders> GetPublicFolders() const { return public_imap_folders_;}
      
      std::shared_ptr<IMAPFolder> GetFolderByFullPath(const String &sFolderName);
      std::shared_ptr<IMAPFolder> GetFolderByFullPath(std::vector<String> &vecFolderPath);

      // The selected folder, the read-only flag, the idling flag and the session's
      // \Recent set are read by notification clients running on OTHER connections'
      // threads - a delivery to this mailbox notifies every session that has it
      // selected, synchronously, on the delivering thread. Every accessor below takes
      // state_mutex_, and IMAPNotificationClient holds it across a notification so
      // the folder cannot be closed between its idling check and its send.
      // Lock order, everywhere: this lock first, then the notification client's own.
      typedef boost::lock_guard<boost::recursive_mutex> StateLock;
      boost::recursive_mutex& GetStateMutex() const { return state_mutex_; }

      std::shared_ptr<IMAPFolder> GetCurrentFolder() const;

      bool CheckPermission(std::shared_ptr<IMAPFolder> pFolder, int iPermission);
      void CheckFolderPermissions(std::shared_ptr<IMAPFolder> pFolder, bool &readAccess, bool &writeAccess);

      void CloseCurrentFolder();
      void SetCurrentFolder(std::shared_ptr<IMAPFolder> pFolder, bool readOnly);
   
      void SendResponseString(const String &sTag, const String &sResponse, const String &sMessage);

      bool GetIsIdling() const;
      void SetIsIdling(bool bNewVal);

      // RFC 7162 (CONDSTORE/QRESYNC): whether the client has switched these
      // extensions on for this session via ENABLE (or a CONDSTORE-enabling command).
      bool GetCondstoreEnabled() const { return condstore_enabled_; }
      void SetCondstoreEnabled(bool bNewVal) { condstore_enabled_ = bNewVal; }
      bool GetQResyncEnabled() const { return qresync_enabled_; }
      void SetQResyncEnabled(bool bNewVal) { qresync_enabled_ = bNewVal; }

      // RFC 6855 (UTF8=ACCEPT): whether the client has accepted UTF-8 message data
      // for this session via ENABLE UTF8=ACCEPT.
      bool GetUtf8AcceptEnabled() const { return utf8_accept_enabled_; }
      void SetUtf8AcceptEnabled(bool bNewVal) { utf8_accept_enabled_ = bNewVal; }

      // RFC 9051 (IMAP4rev2): whether the client has switched the session to
      // IMAP4rev2 semantics via "ENABLE IMAP4rev2" (ESEARCH responses by default,
      // no RECENT / \Recent, the obsolete [UNSEEN] response code suppressed, and
      // UTF-8 acceptance).
      bool GetImap4Rev2Enabled() const { return imap4rev2_enabled_; }
      void SetImap4Rev2Enabled(bool bNewVal) { imap4rev2_enabled_ = bNewVal; }

      // RFC 5182 (SEARCHRES): the most recent "SEARCH RETURN (SAVE)" result for this
      // session, stored as UIDs so the "$" marker stays stable across expunges. An
      // empty vector means the saved result is the empty set.
      void SetSavedSearchResult(const std::vector<__int64> &uids) { saved_search_result_ = uids; }
      const std::vector<__int64> & GetSavedSearchResult() const { return saved_search_result_; }

      // RFC 5802/7677 (SCRAM-SHA-256): the in-progress SASL conversation for this
      // connection, or null when no SCRAM authentication is underway.
      std::shared_ptr<ScramSha256> GetScramSession() const { return scram_session_; }
      void SetScramSession(std::shared_ptr<ScramSha256> session) { scram_session_ = session; }

      // RFC 7162 (QRESYNC): compress a list of UIDs into a sequence-set string
      // (e.g. "1:3,5,7:9") for use in "* VANISHED" responses. Sorts and de-dupes.
      static String CompactUidSet(std::vector<__int64> uids);

      /*
         RFC 7162 (QRESYNC), the sequence set for a VANISHED (EARLIER) response when
         the expunge records that would have answered the client precisely are gone.

         Section 3.2.6:

            "Note: A server that receives a mod-sequence smaller than <minmodseq>,
            where <minmodseq> is the value of the smallest expunged mod-sequence it
            remembers minus one, MUST behave as if it was requested to report all
            expunged messages from the provided UID set parameter."

         "All expunged messages from the provided UID set" is computable without any
         history: it is every UID in the requested ranges that the mailbox does not
         currently hold. That is a SUPERSET of the precise answer - it names UIDs the
         client may never have had, and UIDs it was told about on an earlier
         connection - and that is exactly why it is the safe answer. A VANISHED
         (EARLIER) UID a client does not know is a no-op for it; a UID left OUT is a
         message the client goes on believing is there.

         Ranges are clamped to 1:highestUid, because a UID above the mailbox's
         highest issued UID has never existed and reporting it says nothing. They are
         sorted and merged first, so the sequence set comes out ascending however the
         client wrote its UID set.
      */
      static String CompactMissingUidSet(std::shared_ptr<Messages> messages,
                                         std::vector<std::pair<unsigned int, unsigned int>> ranges,
                                         unsigned int highestUid);

      // RFC 7162 (QRESYNC): build untagged FETCH lines (FLAGS/UID/MODSEQ) for every
      // message in the current folder whose MODSEQ is greater than sinceModSeq.
      String GetQResyncChangedFetch(__int64 sinceModSeq);

      void SetDelayedChangeNotification(std::shared_ptr<ChangeNotification> pNotification);

      void Login(std::shared_ptr<const Account> account);
      void Logout(const String &goodbyeMessage);

      // RFC 8437: returns the session to the not-authenticated state without
      // disconnecting - every trace of the user discarded, TLS and the ENABLEd
      // extensions kept.
      void Unauthenticate();

      // Defense-in-depth brute-force protection: counts failed authentication
      // attempts on this single connection and returns true once the hard cap
      // is reached, regardless of whether the per-IP auto-ban is enabled.
      bool RegisterAuthenticationFailure();

      // Fires the OnClientLogon script event (shared by the LOGIN command and
      // every AUTHENTICATE mechanism: PLAIN, SCRAM-SHA-256, XOAUTH2/OAUTHBEARER).
      void FireOnClientLogon(const String &sUsername, bool isAuthenticated);

      bool IsAuthenticated();
      bool GetCurrentFolderReadOnly() const;

      std::shared_ptr<IMAPNotificationClient> GetNotificationClient() {return notification_client_;}

      void StartHandshake();
	 
	  void SetRecentMessages(const std::set<__int64> &messages);
      void ClearRecentMessages();
      void AddRecentMessage(__int64 message_id);
      void RemoveRecentMessage(__int64 message_id);
      bool IsRecentMessage(__int64 message_id) const;
      size_t GetRecentMessageCount() const;


      void SetCommandBuffer(const String &sval);

      // The connection-local APPEND handler - the UID command hands UID REPLACE
      // to it (RFC 8508), the same way ParseData hands it binary literal data.
      std::shared_ptr<IMAPCommandAppend> GetAppendCommandHandler();


   protected:

      virtual void OnConnected();
      virtual void OnHandshakeCompleted();
      virtual void OnHandshakeFailed() {};
      virtual AnsiString GetCommandSeparator() const;

      // IMAP commands are "<tag> <COMMAND> ...", so the verb is the second token.
      virtual AnsiString GetOtelOperationName_(const AnsiString &sData) const;

      void LogClientCommand_(const String &sClientData);
      
      virtual void OnExcessiveDataReceived();
      virtual void OnConnectionTimeout();
            
      eIMAPCommandType GetCommandType(String & sCommand);

      std::map<eIMAPCommandType, std::shared_ptr<IMAPCommand> > mapCommandHandlers;
      std::map<eIMAPCommandType, std::shared_ptr<IMAPCommand> > mapStaticHandlers;

   private:

      std::set<__int64> recent_messages_;

      bool InternalParseData(const AnsiString &Request);
      void SendBanner_();
      void SetAccount_(std::shared_ptr<const Account> account) { account_ = account; }

      void Disconnect_();
      bool IsReceivingLiteralDataForLoginCommand_() const;

      // Resolves "#Users <delim> owner-address <delim> folder..." into a folder in
      // the owner's tree, or null. Null is returned - indistinguishable from "no
      // such folder" - when the namespace is disabled, the owner does not exist,
      // the folder does not exist, or the caller lacks the lookup right on it, so
      // no command that resolves a path can be used to probe which accounts and
      // folders exist. The rights decision itself is ACLManager's.
      std::shared_ptr<IMAPFolder> GetDelegatedFolderByPath_(const std::vector<String> &vecFolderPath);

      bool AskForLiteralData_(const String &sInput);

      void EndIdleMode_();
      int GetLiteralSize_(const String &sCommand, bool *nonSynchronizing = nullptr);

      bool AnswerCommand(std::shared_ptr<IMAPClientCommand> command);
      std::shared_ptr<const Account> account_;

      std::shared_ptr<IMAPFolders> imap_folders_;
      std::shared_ptr<IMAPFolders> public_imap_folders_;

      std::shared_ptr<ChangeNotification> delayed_change_notification_;

      // Guards current_folder_, current_folder_read_only_, is_idling_ and
      // recent_messages_. See GetStateMutex.
      mutable boost::recursive_mutex state_mutex_;

      // Folder info
      std::shared_ptr<IMAPFolder> current_folder_;
      bool current_folder_read_only_;

      String command_buffer_;
      bool is_idling_;

      bool condstore_enabled_;
      bool qresync_enabled_;
      bool utf8_accept_enabled_;
      bool imap4rev2_enabled_;

      // RFC 5182 (SEARCHRES): UIDs saved by the last "SEARCH RETURN (SAVE)".
      std::vector<__int64> saved_search_result_;

      // RFC 5802/7677 (SCRAM-SHA-256): in-progress SASL conversation, or null.
      std::shared_ptr<ScramSha256> scram_session_;

      int literal_data_to_receive_;
      String literal_buffer_;

      // RFC 7888 (LITERAL-): the literal currently being received was sent as
      // {n+}, so the client is not waiting and no continuation may be sent.
      bool current_literal_is_nonsync_ = false;

      bool pending_disconnect_;

      int authentication_failure_count_;

      std::shared_ptr<IMAPNotificationClient> notification_client_;

      int log_level_;
   };
   
}
