alter table hm_messages add column messagesavedate datetime not null default '2001-01-01 00:00:00';

update hm_messages set messagesavedate = messagecreatetime;

update hm_dbversion set value = 6013;
