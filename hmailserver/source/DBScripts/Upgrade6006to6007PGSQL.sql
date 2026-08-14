alter table hm_domains add column if not exists domaindkimsecondaryselector varchar(255) not null default '';

alter table hm_domains add column if not exists domaindkimsecondaryprivatekeyfile varchar(255) not null default '';

update hm_dbversion set value = 6007;
