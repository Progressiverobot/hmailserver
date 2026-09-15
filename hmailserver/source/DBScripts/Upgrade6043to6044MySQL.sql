alter table hm_domains add column domainexternaltagsubject int not null default 0;

alter table hm_domains add column domainexternaltagheader int not null default 0;

alter table hm_domains add column domainexternaltagtext varchar(100) not null default '';

alter table hm_domains add column domainfirstcontacttip int not null default 0;

alter table hm_domains add column domaindisclaimerenabled int not null default 0;

alter table hm_domains add column domaindisclaimerplaintext text;

alter table hm_domains add column domaindisclaimerhtml text;

create table hm_knownsenders
(
	ksid bigint auto_increment not null, primary key(`ksid`), unique(`ksid`),
	ksaccountid int not null,
	ksaddress varchar(255) not null,
	kscount int not null,
	ksfirstseen varchar(32) not null,
	kslastseen varchar(32) not null
);

CREATE UNIQUE INDEX idx_hm_knownsenders_account ON hm_knownsenders (ksaccountid, ksaddress);

ALTER TABLE hm_knownsenders ENGINE=InnoDB;

ALTER TABLE hm_knownsenders ADD CONSTRAINT fk_hm_knownsenders_account FOREIGN KEY (ksaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6044;
