create table hm_contacts
(
	contactid int identity(1,1) not null,
	contactaccountid int not null,
	contactname nvarchar(255) not null,
	contactaddress nvarchar(255) not null,
	contactsource tinyint not null,
	contactcreated datetime not null
)

ALTER TABLE hm_contacts ADD CONSTRAINT hm_contacts_pk PRIMARY KEY (contactid)

CREATE INDEX idx_hm_contacts_account ON hm_contacts (contactaccountid, contactaddress)

ALTER TABLE hm_contacts ADD CONSTRAINT fk_hm_contacts_account FOREIGN KEY (contactaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

update hm_dbversion set value = 6032
