alter table hm_messagerecipients add column recipientdsnnotify int not null default 0;

update hm_dbversion set value = 6005;
