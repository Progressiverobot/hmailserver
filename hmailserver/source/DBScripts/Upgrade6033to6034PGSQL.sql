create table hm_scheduled
(
	schedid bigserial not null primary key,
	schedaccountid int not null,
	schedmessageid bigint not null,
	schedaction smallint not null,
	schedat timestamp not null,
	schedfolderid bigint not null,
	schedcreated timestamp not null
);

CREATE INDEX idx_hm_scheduled_account ON hm_scheduled (schedaccountid);

ALTER TABLE hm_scheduled ADD CONSTRAINT fk_hm_scheduled_account FOREIGN KEY (schedaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6034;
