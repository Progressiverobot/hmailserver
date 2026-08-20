insert into hm_servermessages (smname, smtext) values ('QUOTA_WARNING', 'Your mailbox is %MACRO_PERCENT%% full.

   Mailbox: %MACRO_MAILBOX%
   Used: %MACRO_USED% MB of %MACRO_QUOTA% MB

When it is completely full, new messages sent to you will be refused and
the sender will be told to try again later. Deleting mail you no longer
need - and emptying the trash folder afterwards - will free the space.

hMailServer
')

insert into hm_servermessages (smname, smtext) values ('QUOTA_WARNING_SUBJECT', 'Your mailbox is almost full')

update hm_dbversion set value = 6022
