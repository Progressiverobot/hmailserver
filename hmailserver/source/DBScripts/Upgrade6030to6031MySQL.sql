alter table hm_fetchaccounts add column famirrorfolders tinyint not null default 0;

update hm_dbversion set value = 6031;
