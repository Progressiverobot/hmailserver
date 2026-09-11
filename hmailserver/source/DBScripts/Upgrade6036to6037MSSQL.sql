create table hm_smimekeys
(
	smimeid int identity(1,1) not null,
	smimeaccountid int not null,
	smimekind tinyint not null,
	smimeaddress nvarchar(255) not null,
	smimename nvarchar(255) not null,
	smimefingerprint varchar(64) not null,
	smimecertificate ntext not null,
	smimechain ntext not null,
	smimekey ntext not null,
	smimenotafter bigint not null,
	smimecreated bigint not null
)

ALTER TABLE hm_smimekeys ADD CONSTRAINT hm_smimekeys_pk PRIMARY KEY NONCLUSTERED (smimeid)

CREATE CLUSTERED INDEX idx_hm_smimekeys_account ON hm_smimekeys (smimeaccountid)

CREATE UNIQUE INDEX idx_hm_smimekeys_entry ON hm_smimekeys (smimeaccountid, smimekind, smimefingerprint)

ALTER TABLE hm_smimekeys ADD CONSTRAINT fk_hm_smimekeys_account FOREIGN KEY (smimeaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

update hm_dbversion set value = 6037
