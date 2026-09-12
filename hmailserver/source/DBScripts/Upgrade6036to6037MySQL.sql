create table hm_smimekeys
(
	smimeid int auto_increment not null, primary key(`smimeid`), unique(`smimeid`),
	smimeaccountid int not null,
	smimekind tinyint not null,
	smimeaddress varchar(255) not null,
	smimename varchar(255) not null,
	smimefingerprint varchar(64) not null,
	smimecertificate text not null,
	smimechain text not null,
	smimekey text not null,
	smimenotafter bigint not null,
	smimecreated bigint not null
);

CREATE INDEX idx_hm_smimekeys_account ON hm_smimekeys (smimeaccountid);

CREATE UNIQUE INDEX idx_hm_smimekeys_entry ON hm_smimekeys (smimeaccountid, smimekind, smimefingerprint);

ALTER TABLE hm_smimekeys ENGINE=InnoDB;

ALTER TABLE hm_smimekeys ADD CONSTRAINT fk_hm_smimekeys_account FOREIGN KEY (smimeaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6037;
