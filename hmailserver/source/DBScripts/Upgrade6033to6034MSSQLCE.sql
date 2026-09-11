create table hm_scheduled
(
	schedid int identity(1,1) not null,
	schedaccountid int not null,
	schedmessageid bigint not null,
	schedaction tinyint not null,
	schedat datetime not null,
	schedfolderid bigint not null,
	schedcreated datetime not null
)

ALTER TABLE hm_scheduled ADD CONSTRAINT hm_scheduled_pk PRIMARY KEY (schedid)

CREATE INDEX idx_hm_scheduled_account ON hm_scheduled (schedaccountid)

ALTER TABLE hm_scheduled ADD CONSTRAINT fk_hm_scheduled_account FOREIGN KEY (schedaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

update hm_dbversion set value = 6034
