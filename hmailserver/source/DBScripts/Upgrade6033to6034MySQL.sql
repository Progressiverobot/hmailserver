create table hm_scheduled
(
	schedid int auto_increment not null, primary key(`schedid`), unique(`schedid`),
	schedaccountid int not null,
	schedmessageid bigint not null,
	schedaction tinyint not null,
	schedat datetime not null,
	schedfolderid bigint not null,
	schedcreated datetime not null
) DEFAULT CHARSET=utf8;

CREATE INDEX idx_hm_scheduled_account ON hm_scheduled (schedaccountid);

ALTER TABLE hm_scheduled ENGINE=InnoDB;

ALTER TABLE hm_scheduled ADD CONSTRAINT fk_hm_scheduled_account FOREIGN KEY (schedaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6034;
