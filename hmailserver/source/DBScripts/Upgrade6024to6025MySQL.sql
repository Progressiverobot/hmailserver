alter table hm_messages modify column messageflags smallint unsigned not null;

alter table hm_accounts add column accountantispamenabled tinyint not null default 1;

alter table hm_accounts add column accountspammarkthreshold int not null default -1;

alter table hm_accounts add column accountspamdeletethreshold int not null default -1;

alter table hm_distributionlists add column distributionlistmoderatoraddress varchar(255) not null default '';

alter table hm_distributionlists add column distributionlistbounceaddress varchar(255) not null default '';

update hm_dbversion set value = 6025;
