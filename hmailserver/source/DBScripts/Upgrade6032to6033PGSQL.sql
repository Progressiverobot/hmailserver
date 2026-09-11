create table hm_accountprefs
(
	prefid bigserial not null primary key,
	prefaccountid int not null,
	prefname varchar(64) not null,
	prefvalue varchar(4000) not null
);

CREATE UNIQUE INDEX idx_hm_accountprefs_name ON hm_accountprefs (prefaccountid, prefname);

ALTER TABLE hm_accountprefs ADD CONSTRAINT fk_hm_accountprefs_account FOREIGN KEY (prefaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6033;
