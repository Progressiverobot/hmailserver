create table hm_accountprefs
(
	prefid int identity(1,1) not null,
	prefaccountid int not null,
	prefname nvarchar(64) not null,
	prefvalue nvarchar(4000) not null
)

ALTER TABLE hm_accountprefs ADD CONSTRAINT hm_accountprefs_pk PRIMARY KEY NONCLUSTERED (prefid)

CREATE UNIQUE CLUSTERED INDEX idx_hm_accountprefs_name ON hm_accountprefs (prefaccountid, prefname)

ALTER TABLE hm_accountprefs ADD CONSTRAINT fk_hm_accountprefs_account FOREIGN KEY (prefaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

update hm_dbversion set value = 6033
