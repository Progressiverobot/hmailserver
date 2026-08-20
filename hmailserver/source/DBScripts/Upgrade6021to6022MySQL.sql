insert into hm_servermessages (smname, smtext) values ('QUOTA_WARNING', 'Your mailbox is %MACRO_PERCENT%% full.\r\n\r\n   Mailbox: %MACRO_MAILBOX%\r\n   Used: %MACRO_USED% MB of %MACRO_QUOTA% MB\r\n\r\nWhen it is completely full, new messages sent to you will be refused and\r\nthe sender will be told to try again later. Deleting mail you no longer\r\nneed - and emptying the trash folder afterwards - will free the space.\r\n\r\nhMailServer\r\n');

insert into hm_servermessages (smname, smtext) values ('QUOTA_WARNING_SUBJECT', 'Your mailbox is almost full');

update hm_dbversion set value = 6022;
