ALTER TABLE hm_domains ADD domainexternaltagsubject int not null CONSTRAINT df_domainexternaltagsubject DEFAULT 0

ALTER TABLE hm_domains ADD domainexternaltagheader int not null CONSTRAINT df_domainexternaltagheader DEFAULT 0

ALTER TABLE hm_domains ADD domainexternaltagtext nvarchar(100) not null CONSTRAINT df_domainexternaltagtext DEFAULT ''

ALTER TABLE hm_domains ADD domainfirstcontacttip int not null CONSTRAINT df_domainfirstcontacttip DEFAULT 0

ALTER TABLE hm_domains ADD domaindisclaimerenabled int not null CONSTRAINT df_domaindisclaimerenabled DEFAULT 0

ALTER TABLE hm_domains ADD domaindisclaimerplaintext ntext not null CONSTRAINT df_domaindisclaimerplaintext DEFAULT ''

ALTER TABLE hm_domains ADD domaindisclaimerhtml ntext not null CONSTRAINT df_domaindisclaimerhtml DEFAULT ''

create table hm_knownsenders
(
	ksid bigint identity(1,1) not null,
	ksaccountid int not null,
	ksaddress nvarchar(255) not null,
	kscount int not null,
	ksfirstseen nvarchar(32) not null,
	kslastseen nvarchar(32) not null
)

ALTER TABLE hm_knownsenders ADD CONSTRAINT hm_knownsenders_pk PRIMARY KEY NONCLUSTERED (ksid)

CREATE UNIQUE CLUSTERED INDEX idx_hm_knownsenders_account ON hm_knownsenders (ksaccountid, ksaddress)

ALTER TABLE hm_knownsenders ADD CONSTRAINT fk_hm_knownsenders_account FOREIGN KEY (ksaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

update hm_dbversion set value = 6045
