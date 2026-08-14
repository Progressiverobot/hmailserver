alter table hm_domains add domaindkimsecondaryselector varchar(255) not null default '' /* [IGNORE-ERRORS] */;

alter table hm_domains add domaindkimsecondaryprivatekeyfile varchar(255) not null default '' /* [IGNORE-ERRORS] */;

update hm_dbversion set value = 6007;
