create table hm_contacts
(
	contactid bigserial not null primary key,
	contactaccountid int not null,
	contactname varchar(255) not null,
	contactaddress varchar(255) not null,
	contactsource smallint not null,
	contactcreated timestamp not null
);

CREATE INDEX idx_hm_contacts_account ON hm_contacts (contactaccountid, contactaddress);

ALTER TABLE hm_contacts ADD CONSTRAINT fk_hm_contacts_account FOREIGN KEY (contactaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6032;
